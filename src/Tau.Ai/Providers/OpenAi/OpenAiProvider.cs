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

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        return Stream(model, context, options);
    }

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

        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, OpenAiRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var apiKey = options.ApiKey ?? ProviderEnvironment.GetValue("OPENAI_API_KEY", options.Env);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        ApplySessionAffinityHeader(request, model, options);
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
            await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                stream.Push(new ErrorEvent($"OpenAI API error {(int)response.StatusCode}: {errorBody}"));
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);

            var partial = new AssistantMessage
            {
                Api = Api,
                Provider = model.Provider,
                Model = model.Id,
                Content = []
            };
            stream.Push(new StartEvent(partial));

            var toolCallAccumulators = new Dictionary<int, OpenAiStreamParser.ToolCallAccumulator>();
            var contentIndex = 0;

            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (sse.Data == "[DONE]")
                    break;

                if (OpenAiStreamParser.ParseChunk(
                    sse.Data, stream, ref partial, ref toolCallAccumulators, ref contentIndex))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    private static Dictionary<string, object> BuildRequestBody(
        Model model, LlmContext context, StreamOptions options)
    {
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
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

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            messages.Add(new Dictionary<string, object>
            {
                ["role"] = model.Reasoning && compatibility.SupportsDeveloperRole ? "developer" : "system",
                ["content"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(context.SystemPrompt!)
            });
        }

        // Serialize existing messages
        var converted = OpenAiMessageConverter.ConvertMessageObjects(
            context.Messages,
                     supportsImages: model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase),
                     requiresThinkingAsText: compatibility.RequiresThinkingAsText,
                     requiresToolResultName: compatibility.RequiresToolResultName,
                     requiresAssistantAfterToolResult: compatibility.RequiresAssistantAfterToolResult,
                     requiresReasoningContentOnAssistantMessages: compatibility.RequiresReasoningContentOnAssistantMessages,
                     modelReasoning: model.Reasoning);
        foreach (var msg in converted)
            messages.Add(msg);

        List<object>? tools = null;
        if (context.Tools is { Count: > 0 })
        {
            tools = OpenAiMessageConverter.ConvertToolObjects(context.Tools, compatibility.SupportsStrictMode);
            if (compatibility.ZaiToolStream)
            {
                body["tool_stream"] = true;
            }
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
        AddReasoning(model, options, compatibility, body);
        AddRouting(model, compatibility, body);

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
            if (roleText is "user" or "assistant" &&
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

    private static void AddToolChoice(StreamOptions options, Dictionary<string, object> body)
    {
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

    private static void AddReasoning(
        Model model,
        StreamOptions options,
        ResolvedOpenAiCompatibility compatibility,
        Dictionary<string, object> body)
    {
        if (!model.Reasoning)
        {
            return;
        }

        var effort = ResolveReasoningEffort(model, options, compatibility);
        if (string.IsNullOrWhiteSpace(effort))
        {
            return;
        }

        switch (compatibility.ThinkingFormat)
        {
            case "zai":
            case "qwen":
                body["enable_thinking"] = true;
                break;
            case "qwen-chat-template":
                body["chat_template_kwargs"] = new Dictionary<string, object>
                {
                    ["enable_thinking"] = true,
                    ["preserve_thinking"] = true
                };
                break;
            case "openrouter":
                body["reasoning"] = new Dictionary<string, object> { ["effort"] = effort };
                break;
            case "deepseek":
                body["thinking"] = new Dictionary<string, object> { ["type"] = "enabled" };
                break;
            default:
                if (compatibility.SupportsReasoningEffort)
                {
                    body["reasoning_effort"] = effort;
                }
                break;
        }
    }

    private static string? ResolveReasoningEffort(
        Model model,
        StreamOptions options,
        ResolvedOpenAiCompatibility compatibility)
    {
        if (options is OpenAiOptions { ReasoningEffort: { } effort } &&
            !string.IsNullOrWhiteSpace(effort))
        {
            return MapReasoningEffort(effort, model, compatibility.ReasoningEffortMap);
        }

        return options is SimpleStreamOptions { Reasoning: { } reasoning }
            ? MapReasoningEffort(reasoning, model, compatibility.ReasoningEffortMap)
            : null;
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

    private static void AddRouting(
        Model model,
        ResolvedOpenAiCompatibility compatibility,
        Dictionary<string, object> body)
    {
        var baseUrl = model.BaseUrl ?? string.Empty;
        if (baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase) &&
            compatibility.OpenRouterRouting is { Count: > 0 })
        {
            body["provider"] = compatibility.OpenRouterRouting;
        }

        if (!baseUrl.Contains("ai-gateway.vercel.sh", StringComparison.OrdinalIgnoreCase) ||
            compatibility.VercelGatewayRouting is not { } routing)
        {
            return;
        }

        var gateway = new Dictionary<string, object>();
        if (routing.Only is { Count: > 0 })
        {
            gateway["only"] = routing.Only.ToArray();
        }

        if (routing.Order is { Count: > 0 })
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

    private static string MapReasoningEffort(
        ThinkingLevel level,
        Model model,
        IReadOnlyDictionary<string, string> reasoningEffortMap)
    {
        var normalized = level == ThinkingLevel.ExtraHigh && !ModelCatalog.SupportsXhigh(model)
            ? "high"
            : level switch
            {
                ThinkingLevel.Minimal => "minimal",
                ThinkingLevel.Low => "low",
                ThinkingLevel.Medium => "medium",
                ThinkingLevel.High => "high",
                ThinkingLevel.ExtraHigh => "xhigh",
                _ => "medium"
            };

        return MapReasoningEffort(normalized, model, reasoningEffortMap);
    }

    private static string MapReasoningEffort(
        string effort,
        Model model,
        IReadOnlyDictionary<string, string> reasoningEffortMap)
    {
        var normalized = effort.Trim().ToLowerInvariant();
        if (normalized == "xhigh" && !ModelCatalog.SupportsXhigh(model))
        {
            normalized = "high";
        }

        return reasoningEffortMap.TryGetValue(normalized, out var mapped) ? mapped : normalized;
    }

    private static ResolvedOpenAiCompatibility ResolveCompatibility(Model model)
    {
        var compat = model.Compat;
        return new ResolvedOpenAiCompatibility
        {
            SupportsStore = compat?.SupportsStore ?? false,
            SupportsDeveloperRole = compat?.SupportsDeveloperRole ?? false,
            SupportsReasoningEffort = compat?.SupportsReasoningEffort ?? false,
            ReasoningEffortMap = compat?.ReasoningEffortMap ?? EmptyReasoningEffortMap,
            SupportsUsageInStreaming = compat?.SupportsUsageInStreaming ?? true,
            MaxTokensField = string.Equals(compat?.MaxTokensField, "max_completion_tokens", StringComparison.OrdinalIgnoreCase)
                ? "max_completion_tokens"
                : "max_tokens",
            RequiresToolResultName = compat?.RequiresToolResultName ?? false,
            RequiresAssistantAfterToolResult = compat?.RequiresAssistantAfterToolResult ?? false,
            RequiresThinkingAsText = compat?.RequiresThinkingAsText ?? false,
            RequiresReasoningContentOnAssistantMessages = compat?.RequiresReasoningContentOnAssistantMessages ?? false,
            ThinkingFormat = NormalizeThinkingFormat(compat?.ThinkingFormat),
            OpenRouterRouting = compat?.OpenRouterRouting,
            VercelGatewayRouting = compat?.VercelGatewayRouting,
            ZaiToolStream = compat?.ZaiToolStream ?? false,
            SupportsStrictMode = compat?.SupportsStrictMode ?? false,
            CacheControlFormat = compat?.CacheControlFormat,
            SupportsLongCacheRetention = compat?.SupportsLongCacheRetention ?? true,
            SupportsTemperature = compat?.SupportsTemperature ?? true
        };
    }

    private static string NormalizeThinkingFormat(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "openrouter" => "openrouter",
            "deepseek" => "deepseek",
            "zai" => "zai",
            "qwen" => "qwen",
            "qwen-chat-template" => "qwen-chat-template",
            _ => "openai"
        };

    private static readonly IReadOnlyDictionary<string, string> EmptyReasoningEffortMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private sealed record ResolvedOpenAiCompatibility
    {
        public bool SupportsStore { get; init; }
        public bool SupportsDeveloperRole { get; init; }
        public bool SupportsReasoningEffort { get; init; }
        public IReadOnlyDictionary<string, string> ReasoningEffortMap { get; init; } = EmptyReasoningEffortMap;
        public bool SupportsUsageInStreaming { get; init; }
        public string MaxTokensField { get; init; } = "max_tokens";
        public bool RequiresToolResultName { get; init; }
        public bool RequiresAssistantAfterToolResult { get; init; }
        public bool RequiresThinkingAsText { get; init; }
        public bool RequiresReasoningContentOnAssistantMessages { get; init; }
        public string ThinkingFormat { get; init; } = "openai";
        public IDictionary<string, object>? OpenRouterRouting { get; init; }
        public VercelGatewayRouting? VercelGatewayRouting { get; init; }
        public bool ZaiToolStream { get; init; }
        public bool SupportsStrictMode { get; init; }
        public string? CacheControlFormat { get; init; }
        public bool SupportsLongCacheRetention { get; init; } = true;
        public bool SupportsTemperature { get; init; } = true;
    }

    private static void ApplySessionAffinityHeader(HttpRequestMessage request, Model model, StreamOptions options)
    {
        if (model.Compat?.SendSessionAffinityHeaders == true &&
            !string.IsNullOrWhiteSpace(options.SessionId))
        {
            request.Headers.Remove("x-session-affinity");
            request.Headers.TryAddWithoutValidation("x-session-affinity", options.SessionId);
        }
    }
}

public record OpenAiOptions : StreamOptions
{
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
[System.Text.Json.Serialization.JsonSerializable(typeof(float?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan?))]
internal partial class OpenAiRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
