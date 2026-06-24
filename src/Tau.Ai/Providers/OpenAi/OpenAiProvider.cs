using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

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
        _httpClient = httpClient ?? new HttpClient();
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

        var apiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (model.Headers is not null)
            foreach (var (key, value) in model.Headers)
                request.Headers.TryAddWithoutValidation(key, value);

        if (options.Headers is not null)
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, options.Signal).ConfigureAwait(false);
        await StreamOptionHelpers.InvokeResponseCallbackAsync(options, model, response).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(options.Signal).ConfigureAwait(false);
            stream.Push(new ErrorEvent($"OpenAI API error {(int)response.StatusCode}: {errorBody}"));
            return;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(options.Signal).ConfigureAwait(false);

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

        await foreach (var sse in SseParser.ParseAsync(responseStream, options.Signal))
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

    private static Dictionary<string, object> BuildRequestBody(
        Model model, LlmContext context, StreamOptions options)
    {
        var compatibility = ResolveCompatibility(model);
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
        var converted = OpenAiMessageConverter.ConvertMessages(
            context.Messages,
            supportsImages: model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase),
            requiresThinkingAsText: compatibility.RequiresThinkingAsText,
            requiresToolResultName: compatibility.RequiresToolResultName,
            requiresAssistantAfterToolResult: compatibility.RequiresAssistantAfterToolResult);
        foreach (var msg in converted.EnumerateArray())
            messages.Add(msg);

        body["messages"] = messages;

        if (context.Tools is { Count: > 0 })
        {
            var tools = OpenAiMessageConverter.ConvertTools(context.Tools, compatibility.SupportsStrictMode);
            body["tools"] = tools;
            if (compatibility.ZaiToolStream)
            {
                body["tool_stream"] = true;
            }
        }

        if (options.Temperature.HasValue)
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
            ThinkingFormat = NormalizeThinkingFormat(compat?.ThinkingFormat),
            OpenRouterRouting = compat?.OpenRouterRouting,
            VercelGatewayRouting = compat?.VercelGatewayRouting,
            ZaiToolStream = compat?.ZaiToolStream ?? false,
            SupportsStrictMode = compat?.SupportsStrictMode ?? false
        };
    }

    private static string NormalizeThinkingFormat(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "openrouter" => "openrouter",
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
        public string ThinkingFormat { get; init; } = "openai";
        public IDictionary<string, object>? OpenRouterRouting { get; init; }
        public VercelGatewayRouting? VercelGatewayRouting { get; init; }
        public bool ZaiToolStream { get; init; }
        public bool SupportsStrictMode { get; init; }
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
