using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>
/// OpenAI chat completions streaming provider.
/// Also works with OpenAI-compatible APIs (together.ai, groq, etc.) via Model.BaseUrl.
/// </summary>
public sealed class OpenAiProvider : IStreamProvider
{
    private readonly HttpClient _httpClient;

    public OpenAiProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => "openai-chat-completions";

    public bool SupportsTranscriptContext => true;

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        options = StreamOptionHelpers.WithCacheDefaults(options);
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

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        options = SimpleTokenOptions.WithContextLimit(model, context, options);
        return Stream(model, context, options);
    }

    /// <summary>【AI】【Chat Completions】发送请求并使用相同的 grammar 映射处理历史与响应。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">会话上下文。</param>
    /// <param name="options">生成和传输选项。</param>
    /// <param name="stream">输出事件流。</param>
    /// <returns>HTTP 请求与响应解析任务。</returns>
    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var url = $"{baseUrl}/chat/completions";

        var grammarInputs = OpenAiMessageConverter.CreateGrammarToolInputProperties(model, context);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options, grammarInputs)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, OpenAiRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var apiKey = options.ApiKey ?? ProviderEnvironment.GetValue("OPENAI_API_KEY", options.Env);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        SessionAffinityHeaders.Apply(request, model, options, "openai-completions");
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            await StreamOptionHelpers.ApplyHeadersCallbackAsync(options, model, request).ConfigureAwait(false);
            using var response = await ProviderHttpRetry.SendAsync(_httpClient, request, model, options, requestTimeout.Token,
                retryTransportErrors: true).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                stream.Push(new ErrorEvent($"OpenAI API error {(int)response.StatusCode}: {errorBody}"));
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);

            await OpenAiStreamParser.ProcessStreamAsync(responseStream, stream, model, Api,
                grammarInputs, ResolveCompatibility(model).SupportsFinishReason, requestTimeout.Token, options.OnProviderStreamEvent).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    /// <summary>【AI】【Chat Completions】按模型能力组装系统声明、工具和生成选项。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">旧式或消息式上下文。</param>
    /// <param name="options">请求选项。</param>
    /// <param name="grammarInputs">折叠前建立的历史语法工具输入映射。</param>
    /// <returns>请求报文。</returns>
    internal static Dictionary<string, object> BuildRequestBody(
        Model model, LlmContext context, StreamOptions options, IReadOnlyDictionary<string, string> grammarInputs)
    {
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
        context = Transcript.ResolveTranscript(context, model.Compat?.SupportsMidConvoSystemMessages == true);
        var transcriptTools = Transcript.ResolveTranscriptTools(context.Messages,
            model.Compat?.SupportsMidConvoSystemMessages == true && model.Compat.SupportsMidConvoToolAdditions == true);
        var compatibility = ResolveCompatibility(model);
        var cacheControl = BuildCacheControl(compatibility, options);
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["stream"] = true
        };
        if (compatibility.SupportsUsageInStreaming)
        {
            body["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
        }
        if (compatibility.SupportsStore)
        {
            body["store"] = false;
        }
        AddPromptCacheParameters(model, compatibility, options, body);

        var messages = new List<object>();
        var replay = MessageTransformer.TransformMessages(context.Messages, model, OpenAiToolCallIds.Normalize);

        // 1. 【AI】【Chat Completions】指令与工具新增保持会话位置，旧模型已提前合并
        var converted = OpenAiMessageConverter.ConvertMessageObjects(
            replay,
                     supportsImages: model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase),
                     requiresThinkingAsText: compatibility.RequiresThinkingAsText,
                     requiresToolResultName: compatibility.RequiresToolResultName,
                     requiresAssistantAfterToolResult: compatibility.RequiresAssistantAfterToolResult,
                     requiresReasoningContentOnAssistantMessages: compatibility.RequiresReasoningContentOnAssistantMessages,
                     modelReasoning: model.Reasoning,
                     instructionRole: model.Reasoning && compatibility.SupportsDeveloperRole ? "developer" : "system",
                     anchorsToolAdditions: transcriptTools.AnchorsAdditions,
                     supportsStrictMode: compatibility.SupportsStrictMode,
                     supportsOpenAiGrammarTools: model.Compat?.SupportsOpenAiGrammarTools == true,
                     grammarToolInputProperties: grammarInputs,
                     provider: model.Provider);
        foreach (var msg in converted)
            messages.Add(msg);

        List<object>? tools = null;
        if (transcriptTools.RequestTools is { Count: > 0 })
        {
            tools = OpenAiMessageConverter.ConvertToolObjects(transcriptTools.RequestTools, compatibility.SupportsStrictMode,
                model.Compat?.SupportsOpenAiGrammarTools == true);
            if (compatibility.ZaiToolStream)
            {
                body["tool_stream"] = true;
            }
        }
        else if (OpenAiMessageConverter.HasToolHistory(context.Messages))
        {
            tools = [];
        }

        if (cacheControl is not null)
        {
            ApplyAnthropicCacheControl(messages, tools, cacheControl);
        }

        body["messages"] = messages;
        if (tools is not null)
        {
            body["tools"] = tools;
        }

        if (options.Temperature.HasValue && compatibility.SupportsTemperature)
            body["temperature"] = options.Temperature.Value;
        if (options.MaxTokens.HasValue)
            body[compatibility.MaxTokensField] = options.MaxTokens.Value;
        if (options.TopP.HasValue)
            body["top_p"] = options.TopP.Value;

        AddToolChoice(options, body);
        if (model.Compat?.VllmPriority is { } priority) body["priority"] = priority;
        OpenAiReasoning.Apply(model, options, body);
        AddRouting(compatibility, body);
        StreamOptionHelpers.ApplySamplingParams(body, model, options);

        return body;
    }

    private static void AddPromptCacheParameters(
        Model model,
        ResolvedOpenAiCompatibility compatibility,
        StreamOptions options,
        Dictionary<string, object> body)
    {
        if (ShouldSendPromptCacheKey(model, compatibility, options) &&
            !string.IsNullOrWhiteSpace(options.SessionId))
        {
            body["prompt_cache_key"] = OpenAiResponsesShared.ClampOpenAiPromptCacheKey(options.SessionId)!;
        }

        if (options.CacheRetention == CacheRetention.Long && compatibility.SupportsLongCacheRetention)
        {
            body["prompt_cache_retention"] = "24h";
        }
    }

    private static bool ShouldSendPromptCacheKey(
        Model model,
        ResolvedOpenAiCompatibility compatibility,
        StreamOptions options)
    {
        if (options.CacheRetention == CacheRetention.None)
        {
            return false;
        }

        var baseUrl = model.BaseUrl ?? string.Empty;
        return baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase) ||
            (options.CacheRetention == CacheRetention.Long && compatibility.SupportsLongCacheRetention);
    }

    private static IReadOnlyDictionary<string, object>? BuildCacheControl(
        ResolvedOpenAiCompatibility compatibility,
        StreamOptions options)
    {
        if (!string.Equals(compatibility.CacheControlFormat, "anthropic", StringComparison.OrdinalIgnoreCase) ||
            options.CacheRetention == CacheRetention.None)
        {
            return null;
        }

        var cacheControl = new Dictionary<string, object>
        {
            ["type"] = "ephemeral"
        };
        if (options.CacheRetention == CacheRetention.Long && compatibility.SupportsLongCacheRetention)
        {
            cacheControl["ttl"] = "1h";
        }

        return cacheControl;
    }

    private static void ApplyAnthropicCacheControl(
        List<object> messages,
        List<object>? tools,
        IReadOnlyDictionary<string, object> cacheControl)
    {
        AddCacheControlToSystemPrompt(messages, cacheControl);
        AddCacheControlToLastTool(tools, cacheControl);
        AddCacheControlToLastConversationMessage(messages, cacheControl);
    }

    private static void AddCacheControlToSystemPrompt(
        List<object> messages,
        IReadOnlyDictionary<string, object> cacheControl)
    {
        foreach (var message in messages.OfType<Dictionary<string, object>>())
        {
            if (!message.TryGetValue("role", out var role))
            {
                continue;
            }

            var roleText = Convert.ToString(role);
            if (roleText is "system" or "developer")
            {
                AddCacheControlToTextContent(message, cacheControl);
                return;
            }
        }
    }

    /// <summary>【AI】【缓存边界】从末尾查找可缓存的用户、助手或工具结果正文，空正文继续向前查找。</summary>
    /// <param name="messages">已转换的会话消息。</param><param name="cacheControl">缓存策略。</param>
    private static void AddCacheControlToLastConversationMessage(
        List<object> messages,
        IReadOnlyDictionary<string, object> cacheControl)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not Dictionary<string, object> message ||
                !message.TryGetValue("role", out var role))
            {
                continue;
            }

            var roleText = Convert.ToString(role);
            if (roleText is "user" or "assistant" or "tool" &&
                AddCacheControlToTextContent(message, cacheControl))
            {
                return;
            }
        }
    }

    private static void AddCacheControlToLastTool(
        List<object>? tools,
        IReadOnlyDictionary<string, object> cacheControl)
    {
        if (tools is not { Count: > 0 } ||
            tools[^1] is not Dictionary<string, object> tool)
        {
            return;
        }

        tool["cache_control"] = cacheControl;
    }

    private static bool AddCacheControlToTextContent(
        Dictionary<string, object> message,
        IReadOnlyDictionary<string, object> cacheControl)
    {
        if (!message.TryGetValue("content", out var content))
        {
            return false;
        }

        if (content is string text)
        {
            if (text.Length == 0)
            {
                return false;
            }

            message["content"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = cacheControl
                }
            };
            return true;
        }

        if (content is not List<object> parts)
        {
            return false;
        }

        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (parts[i] is not Dictionary<string, object> part ||
                !part.TryGetValue("type", out var type) ||
                !string.Equals(Convert.ToString(type), "text", StringComparison.Ordinal))
            {
                continue;
            }

            part["cache_control"] = cacheControl;
            return true;
        }

        return false;
    }

    /// <summary>【AI】【工具选择】保留简化入口的显式策略，并转换原生函数选择对象。</summary>
    /// <param name="options">原生或简化选项。</param><param name="body">待发送请求体。</param>
    private static void AddToolChoice(StreamOptions options, Dictionary<string, object> body)
    {
        if (options is SimpleStreamOptions { ToolChoice: { } simpleChoice })
        {
            body["tool_choice"] = simpleChoice;
            return;
        }
        if (options is not OpenAiOptions { ToolChoice: { } toolChoice })
        {
            return;
        }

        body["tool_choice"] = toolChoice.IsFunction
            ? new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = toolChoice.FunctionName!
                }
            }
            : toolChoice.Kind;
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

    /// <summary>【AI】【网关路由】显式路由配置适用于代理地址，保留已配置的空数组。</summary>
    /// <param name="compatibility">显式路由配置。</param><param name="body">请求体。</param>
    private static void AddRouting(
        ResolvedOpenAiCompatibility compatibility,
        Dictionary<string, object> body)
    {
        if (compatibility.OpenRouterRouting is not null)
        {
            body["provider"] = compatibility.OpenRouterRouting;
        }

        if (compatibility.VercelGatewayRouting is not { } routing)
        {
            return;
        }

        var gateway = new Dictionary<string, object>();
        if (routing.Only is not null)
        {
            gateway["only"] = routing.Only.ToArray();
        }

        if (routing.Order is not null)
        {
            gateway["order"] = routing.Order.ToArray();
        }

        if (gateway.Count > 0)
        {
            body["providerOptions"] = new Dictionary<string, object>
            {
                ["gateway"] = gateway
            };
        }
    }

    /// <summary>【AI】【兼容投影】将自动检测及显式覆盖后的能力转换为请求组装视图。</summary>
    /// <param name="model">目标模型。</param><returns>已补足默认值的请求能力。</returns>
    private static ResolvedOpenAiCompatibility ResolveCompatibility(Model model)
    {
        var compat = OpenAiCompatibility.Resolve(model);
        return new ResolvedOpenAiCompatibility
        {
            SupportsStore = compat?.SupportsStore ?? false,
            SupportsDeveloperRole = compat?.SupportsDeveloperRole ?? false,
            SupportsUsageInStreaming = compat?.SupportsUsageInStreaming ?? true,
            SupportsFinishReason = compat?.SupportsFinishReason ?? true,
            MaxTokensField = string.Equals(compat?.MaxTokensField, "max_completion_tokens", StringComparison.OrdinalIgnoreCase)
                ? "max_completion_tokens"
                : "max_tokens",
            RequiresToolResultName = compat?.RequiresToolResultName ?? false,
            RequiresAssistantAfterToolResult = compat?.RequiresAssistantAfterToolResult ?? false,
            RequiresThinkingAsText = compat?.RequiresThinkingAsText ?? false,
            RequiresReasoningContentOnAssistantMessages = compat?.RequiresReasoningContentOnAssistantMessages ?? false,
            OpenRouterRouting = compat?.OpenRouterRouting,
            VercelGatewayRouting = compat?.VercelGatewayRouting,
            ZaiToolStream = compat?.ZaiToolStream ?? false,
            SupportsStrictMode = compat?.SupportsStrictMode ?? false,
            CacheControlFormat = compat?.CacheControlFormat,
            SupportsLongCacheRetention = compat?.SupportsLongCacheRetention ?? true,
            SupportsTemperature = compat?.SupportsTemperature ?? true
        };
    }

    private sealed record ResolvedOpenAiCompatibility
    {
        public bool SupportsStore { get; init; }
        public bool SupportsDeveloperRole { get; init; }
        public bool SupportsUsageInStreaming { get; init; }
        public bool SupportsFinishReason { get; init; } = true;
        public string MaxTokensField { get; init; } = "max_tokens";
        public bool RequiresToolResultName { get; init; }
        public bool RequiresAssistantAfterToolResult { get; init; }
        public bool RequiresThinkingAsText { get; init; }
        public bool RequiresReasoningContentOnAssistantMessages { get; init; }
        public IDictionary<string, object>? OpenRouterRouting { get; init; }
        public VercelGatewayRouting? VercelGatewayRouting { get; init; }
        public bool ZaiToolStream { get; init; }
        public bool SupportsStrictMode { get; init; }
        public string? CacheControlFormat { get; init; }
        public bool SupportsLongCacheRetention { get; init; } = true;
        public bool SupportsTemperature { get; init; } = true;
    }

}

public record OpenAiOptions : StreamOptions
{
    /// <summary>【AI】【思考预算】原生 Completions 请求的各等级自定义预算。</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }
    public OpenAiToolChoice? ToolChoice { get; init; }
    public string? ReasoningEffort { get; init; }
}

public sealed record OpenAiToolChoice
{
    private OpenAiToolChoice(string kind, string? functionName)
    {
        Kind = string.IsNullOrWhiteSpace(kind)
            ? throw new ArgumentException("Tool choice kind cannot be empty.", nameof(kind))
            : kind;
        FunctionName = functionName;
    }

    public string Kind { get; }
    public string? FunctionName { get; }
    public bool IsFunction => FunctionName is not null;

    public static OpenAiToolChoice FromString(string choice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choice);
        return new OpenAiToolChoice(choice, functionName: null);
    }

    public static OpenAiToolChoice Function(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new OpenAiToolChoice("function", name);
    }

    public static implicit operator OpenAiToolChoice(string choice) =>
        FromString(choice);

    public static implicit operator string?(OpenAiToolChoice? choice) =>
        choice?.ToString();

    public override string ToString() =>
        IsFunction ? $"function:{FunctionName}" : Kind;
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(object))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan?))]
internal partial class OpenAiRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
