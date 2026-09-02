using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.OpenAiCompat;

internal sealed class OpenAiCompatibleProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _api;
    private readonly string _defaultBaseUrl;
    private readonly string _requestPath;
    private readonly string _authHeaderName;
    private readonly string? _authHeaderPrefix;

    public OpenAiCompatibleProvider(
        string api,
        string defaultBaseUrl,
        string requestPath = "/chat/completions",
        string authHeaderName = "Authorization",
        string? authHeaderPrefix = "Bearer ",
        HttpClient? httpClient = null)
    {
        _api = api;
        _defaultBaseUrl = defaultBaseUrl;
        _requestPath = requestPath;
        _authHeaderName = authHeaderName;
        _authHeaderPrefix = authHeaderPrefix;
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => _api;

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
            {
                StreamOptionHelpers.PushAborted(stream, model, Api);
            }
            catch (Exception ex)
            {
                stream.Push(new ErrorEvent(ex.Message));
            }
        });

        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        Stream(model, context, options);

    private async Task StreamInternalAsync(Model model, LlmContext context, StreamOptions options, AssistantMessageStream stream)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? _defaultBaseUrl : model.BaseUrl!.TrimEnd('/');
        var url = $"{baseUrl}{_requestPath}";

        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, OpenAiCompatJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyAuthHeader(request, options.ApiKey);
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestTimeout.Token).ConfigureAwait(false);
            await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                stream.Push(new ErrorEvent($"{_api} error {(int)response.StatusCode}: {ErrorBody.Format(errorBody)}"));
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);

            var partial = new AssistantMessage
            {
                Api = _api,
                Provider = model.Provider,
                Model = model.Id,
                Content = []
            };
            stream.Push(new StartEvent(partial));

            var toolCallAccumulators = new Dictionary<int, OpenAi.OpenAiStreamParser.ToolCallAccumulator>();
            var contentIndex = 0;
            var completed = false;

            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (sse.Data == "[DONE]")
                {
                    break;
                }

                completed = OpenAi.OpenAiStreamParser.ParseChunk(
                    sse.Data,
                    stream,
                    ref partial,
                    ref toolCallAccumulators,
                    ref contentIndex);
                if (completed)
                {
                    break;
                }
            }

            if (!completed)
            {
                OpenAi.OpenAiStreamParser.Complete(
                    stream,
                    ref partial,
                    toolCallAccumulators,
                    model.Compat?.SupportsFinishReason == true);
            }
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    private void ApplyAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || Auth.EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            return;
        }

        if (_authHeaderName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                (_authHeaderPrefix ?? "Bearer ").TrimEnd(),
                _authHeaderPrefix is null ? apiKey : apiKey);
            return;
        }

        var headerValue = _authHeaderPrefix is null ? apiKey : $"{_authHeaderPrefix}{apiKey}";
        request.Headers.TryAddWithoutValidation(_authHeaderName, headerValue);
    }

    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.Remove(key);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static Dictionary<string, object> BuildRequestBody(Model model, LlmContext context, StreamOptions options)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true }
        };

        var messages = new List<object>();
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            messages.Add(new Dictionary<string, object>
            {
                ["role"] = "system",
                ["content"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(context.SystemPrompt!)
            });
        }

        foreach (var msg in OpenAi.OpenAiMessageConverter.ConvertMessages(
                     context.Messages,
                     requiresToolResultName: model.Compat?.RequiresToolResultName ?? false,
                     requiresAssistantAfterToolResult: model.Compat?.RequiresAssistantAfterToolResult ?? false).EnumerateArray())
        {
            messages.Add(msg);
        }

        body["messages"] = messages;

        if (context.Tools is { Count: > 0 })
        {
            body["tools"] = OpenAi.OpenAiMessageConverter.ConvertTools(context.Tools);
        }
        else if (OpenAi.OpenAiMessageConverter.HasToolHistory(context.Messages))
        {
            body["tools"] = new List<object>();
        }

        if (options.Temperature.HasValue)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.MaxTokens.HasValue)
        {
            var maxTokensField = string.Equals(model.Compat?.MaxTokensField, "max_completion_tokens", StringComparison.OrdinalIgnoreCase)
                ? "max_completion_tokens"
                : "max_tokens";
            body[maxTokensField] = options.MaxTokens.Value;
        }

        if (options.TopP.HasValue)
        {
            body["top_p"] = options.TopP.Value;
        }

        AddToolChoice(options, body);
        AddReasoning(model, options, body);
        AddProviderRouting(model, body);
        StreamOptionHelpers.ApplySamplingParams(body, model, options);

        return body;
    }

    /// <summary>
    /// 将显式工具选择策略写入 OpenAI-compatible 请求。
    /// 即使当前没有工具定义，也要保留调用方显式指定的 tool_choice。
    /// </summary>
    /// <param name="options">当前流式请求选项。</param>
    /// <param name="body">待发送的请求体。</param>
    private static void AddToolChoice(StreamOptions options, Dictionary<string, object> body)
    {
        object? choice = options switch
        {
            OpenAi.OpenAiOptions { ToolChoice: { } openAi } => openAi.IsFunction
                ? new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object> { ["name"] = openAi.FunctionName! }
                }
                : openAi.Kind,
            SimpleStreamOptions { ToolChoice: { } simple } => simple,
            _ => null
        };

        if (choice is not null)
        {
            body["tool_choice"] = choice;
        }
    }

    /// <summary>
    /// 按模型兼容性设置 OpenAI-compatible 推理字段。
    /// </summary>
    /// <param name="model">目标模型及其 compat 元数据。</param>
    /// <param name="options">当前简单流选项。</param>
    /// <param name="body">待发送的请求体。</param>
    private static void AddReasoning(Model model, StreamOptions options, Dictionary<string, object> body)
    {
        if (!model.Reasoning)
        {
            return;
        }

        var (enabled, effort) = ResolveReasoning(model, options);
        var format = model.Compat?.ThinkingFormat?.Trim().ToLowerInvariant();
        switch (format)
        {
            case "baseten":
                var basetenArgs = BuildChatTemplateValues(model, enabled, effort, options);
                basetenArgs["enable_thinking"] = enabled;
                body["chat_template_args"] = basetenArgs;
                if (model.Compat?.SupportsReasoningEffort == true)
                {
                    if (enabled && effort is not null)
                    {
                        body["reasoning_effort"] = effort;
                    }
                }
                break;
            case "chat-template":
                var chatTemplateArgs = BuildChatTemplateValues(model, enabled, effort, options);
                if (chatTemplateArgs.Count > 0)
                {
                    body["chat_template_kwargs"] = chatTemplateArgs;
                }
                break;
            case "qwen-chat-template":
                body["chat_template_kwargs"] = new Dictionary<string, object>
                {
                    ["enable_thinking"] = enabled,
                    ["preserve_thinking"] = true
                };
                break;
            case "zai":
            case "qwen":
                body["enable_thinking"] = enabled;
                if (enabled && model.Compat?.SupportsReasoningEffort == true && effort is not null)
                {
                    body["reasoning_effort"] = effort;
                }
                break;
            case "openrouter":
                if (enabled && effort is not null)
                {
                    body["reasoning"] = new Dictionary<string, object> { ["effort"] = effort };
                }
                else if (model.ThinkingLevelMap?.TryGetValue("off", out var openRouterOff) == true && openRouterOff is not null)
                {
                    body["reasoning"] = new Dictionary<string, object> { ["effort"] = openRouterOff };
                }
                break;
            case "deepseek":
                if (enabled || model.Compat?.SupportsDisabledThinking == true)
                {
                    body["thinking"] = new Dictionary<string, object> { ["type"] = enabled ? "enabled" : "disabled" };
                }
                if (enabled && model.Compat?.SupportsReasoningEffort == true && effort is not null)
                {
                    body["reasoning_effort"] = effort;
                }
                break;
            case "together":
                body["reasoning"] = new Dictionary<string, object> { ["enabled"] = enabled };
                if (enabled && model.Compat?.SupportsReasoningEffort == true && effort is not null)
                {
                    body["reasoning_effort"] = effort;
                }
                break;
            case "string-thinking":
                if (enabled && effort is not null)
                {
                    body["thinking"] = effort;
                }
                else if (model.ThinkingLevelMap?.TryGetValue("off", out var stringOff) == true && stringOff is not null)
                {
                    body["thinking"] = stringOff;
                }
                break;
            case "ant-ling":
                if (enabled && effort is not null)
                {
                    body["reasoning"] = new Dictionary<string, object> { ["effort"] = effort };
                }
                break;
            default:
                if (enabled && model.Compat?.SupportsReasoningEffort == true && effort is not null)
                {
                    body["reasoning_effort"] = effort;
                }
                break;
        }

        if (enabled && model.Compat?.ThinkingTokenBudgetField is { Length: > 0 } budgetField)
        {
            var level = options is SimpleStreamOptions { Reasoning: { } requested }
                ? requested
                : ThinkingLevel.Medium;
            var budget = StreamOptionHelpers.GetThinkingBudget(
                options is SimpleStreamOptions simple ? simple.ThinkingBudgets : null,
                level,
                defaultMinimal: 1_024,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 16_384);
            if (budget > 0)
            {
                body[budgetField] = budget;
            }
        }
    }

    /// <summary>解析 OpenAI-compatible 请求的启用状态和 provider 推理级别。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="options">流式选项，可能包含 provider 专用 effort 或通用 reasoning。</param>
    /// <returns>是否启用推理以及映射后的 effort；关闭或不支持时 effort 为空。</returns>
    private static (bool Enabled, string? Effort) ResolveReasoning(Model model, StreamOptions options)
    {
        if (options is OpenAi.OpenAiOptions { ReasoningEffort: { Length: > 0 } explicitEffort })
        {
            var enabled = !explicitEffort.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                          !explicitEffort.Equals("off", StringComparison.OrdinalIgnoreCase);
            return (enabled, enabled ? explicitEffort : null);
        }

        if (options is not SimpleStreamOptions { Reasoning: { } requested })
        {
            return (false, null);
        }

        if (requested == ThinkingLevel.Off)
        {
            return (false, null);
        }

        var normalized = requested switch
        {
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "xhigh",
            _ => null
        };
        if (normalized is null)
        {
            return (true, null);
        }

        if (model.Compat?.ReasoningEffortMap?.TryGetValue(normalized, out var mapped) == true)
        {
            return (mapped is not null, mapped);
        }

        // 1. OpenRouter 运行时元数据使用 thinkingLevelMap 声明可用 effort；
        //    当 compat.reasoningEffortMap 未提供时也必须尊重该映射，避免把不支持的级别发给上游
        if (model.ThinkingLevelMap?.TryGetValue(normalized, out var levelMapped) == true)
        {
            return (levelMapped is not null, levelMapped);
        }

        return (true, normalized);
    }

    /// <summary>解析模型声明的 chat template 参数占位符。</summary>
    /// <param name="model">包含兼容配置的模型。</param>
    /// <param name="enabled">是否启用推理。</param>
    /// <param name="effort">映射后的推理级别。</param>
    /// <param name="options">请求选项，用于读取自定义预算。</param>
    /// <returns>可序列化的 provider 参数字典。</returns>
    private static Dictionary<string, object> BuildChatTemplateValues(
        Model model,
        bool enabled,
        string? effort,
        StreamOptions options)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var budget = options is SimpleStreamOptions { Reasoning: { } level }
            ? StreamOptionHelpers.GetThinkingBudget(
                options is SimpleStreamOptions simple ? simple.ThinkingBudgets : null,
                level,
                defaultMinimal: 1_024,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 16_384)
            : 0;

        if (model.Compat?.ChatTemplateArgs is { } chatTemplateArgs)
        {
            foreach (var (key, value) in chatTemplateArgs)
            {
                result[key] = ResolveChatTemplateValue(value, enabled, effort, budget);
            }
        }

        return result;
    }

    private static object ResolveChatTemplateValue(object value, bool enabled, string? effort, int budget)
    {
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("$var", out var variable) && variable.ValueKind == JsonValueKind.String)
        {
            return variable.GetString()?.ToLowerInvariant() switch
            {
                "thinking.enabled" => enabled,
                "thinking.effort" => effort ?? "none",
                "thinking.budget" => budget,
                _ => value
            };
        }

        return value;
    }

    /// <summary>将枚举推理级别映射为 provider 接受的字符串。</summary>
    /// <param name="level">通用推理级别。</param>
    /// <param name="map">模型级别覆盖映射。</param>
    /// <returns>provider 推理级别字符串。</returns>
    private static string MapThinkingLevel(ThinkingLevel level, IReadOnlyDictionary<string, string>? map)
    {
        var normalized = level switch
        {
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "xhigh",
            _ => "none"
        };
        return map is not null && map.TryGetValue(normalized, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : normalized;
    }

    /// <summary>将模型级路由兼容配置写入 OpenAI-compatible 请求体。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="body">待发送的请求体。</param>
    private static void AddProviderRouting(Model model, Dictionary<string, object> body)
    {
        if (model.Compat?.OpenRouterRouting is { Count: > 0 } openRouter &&
            (model.BaseUrl?.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            body["provider"] = openRouter;
        }

        if (model.Compat?.VercelGatewayRouting is not { } vercel ||
            !(model.BaseUrl?.Contains("ai-gateway.vercel.sh", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return;
        }

        var gateway = new Dictionary<string, object>();
        if (vercel.Only is { Count: > 0 }) gateway["only"] = vercel.Only.ToArray();
        if (vercel.Order is { Count: > 0 }) gateway["order"] = vercel.Order.ToArray();
        if (gateway.Count > 0)
        {
            body["providerOptions"] = new Dictionary<string, object>
            {
                ["gateway"] = gateway
            };
        }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(object))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan?))]
internal partial class OpenAiCompatJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
