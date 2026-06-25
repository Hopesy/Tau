using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Anthropic;

/// <summary>
/// Anthropic Messages API streaming provider.
/// Endpoint: POST /v1/messages with "stream": true.
/// </summary>
public sealed class AnthropicProvider : IStreamProvider
{
    private const string DefaultAnthropicVersion = "2023-06-01";
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    private const string FineGrainedToolStreamingBeta = "fine-grained-tool-streaming-2025-05-14";
    private readonly HttpClient _httpClient;

    public AnthropicProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => "anthropic-messages";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, reasoning: null).ConfigureAwait(false);
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
        var stream = new AssistantMessageStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, options.Reasoning).ConfigureAwait(false);
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

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream,
        ThinkingLevel? reasoning)
    {
        if (StreamOptionHelpers.PushAbortedIfCanceled(options, stream, model, Api))
        {
            return;
        }

        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com";
        var url = $"{baseUrl}/v1/messages";

        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options, reasoning)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, AnthropicRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var apiKey = options.ApiKey ?? ProviderEnvironment.GetValue("ANTHROPIC_API_KEY", options.Env);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        request.Headers.TryAddWithoutValidation("anthropic-version", DefaultAnthropicVersion);
        if (BuildAnthropicBetaHeader(model, context, options as AnthropicOptions) is { } betaHeader)
            request.Headers.TryAddWithoutValidation("anthropic-beta", betaHeader);

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
                stream.Push(new ErrorEvent($"Anthropic API error {(int)response.StatusCode}: {errorBody}"));
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);

            var initial = new AssistantMessage
            {
                Api = Api,
                Provider = model.Provider,
                Model = model.Id,
                Content = []
            };

            var parser = new AnthropicStreamParser(initial, stream);

            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (string.IsNullOrEmpty(sse.Data))
                    continue;
                if (parser.ParseEvent(sse.EventType, sse.Data))
                    break;
            }
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    private static Dictionary<string, object> BuildRequestBody(
        Model model,
        LlmContext context,
        StreamOptions options,
        ThinkingLevel? reasoning)
    {
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
        var cacheControl = BuildCacheControl(model, options);
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["max_tokens"] = options.MaxTokens ?? model.MaxOutputTokens ?? 4096
        };

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            body["system"] = BuildSystemPrompt(context.SystemPrompt!, cacheControl);
        }

        body["messages"] = AnthropicMessageConverter.ConvertMessages(
            context.Messages,
            cacheControl,
            allowEmptySignature: model.Compat?.AllowEmptySignature == true);

        if (context.Tools is { Count: > 0 })
        {
            var toolCacheControl = model.Compat?.SupportsCacheControlOnTools == false ? null : cacheControl;
            body["tools"] = AnthropicMessageConverter.ConvertTools(
                context.Tools,
                supportsEagerToolInputStreaming: model.Compat?.SupportsEagerToolInputStreaming ?? true,
                cacheControl: toolCacheControl);
        }

        var thinking = BuildThinking(model, options, reasoning);

        if (options.Temperature.HasValue && !ThinkingIsEnabled(thinking) && model.Compat?.SupportsTemperature != false)
            body["temperature"] = options.Temperature.Value;
        if (options.TopP.HasValue)
            body["top_p"] = options.TopP.Value;

        if (thinking is not null)
            body["thinking"] = thinking;

        if (thinking is not null &&
            IsAdaptiveThinking(thinking) &&
            reasoning is { } simpleReasoning)
        {
            body["output_config"] = new Dictionary<string, object>
            {
                ["effort"] = MapAdaptiveThinkingEffort(model, simpleReasoning)
            };
        }

        if (options is AnthropicOptions anthropicOptions)
        {
            if (!string.IsNullOrWhiteSpace(anthropicOptions.Effort) &&
                thinking is not null &&
                IsAdaptiveThinking(thinking))
                body["output_config"] = new Dictionary<string, object> { ["effort"] = anthropicOptions.Effort! };

            if (anthropicOptions.ToolChoice is { } toolChoice)
                body["tool_choice"] = MapToolChoice(toolChoice);
        }

        if (TryGetMetadataString(options.Metadata, "user_id", out var userId))
            body["metadata"] = new Dictionary<string, object> { ["user_id"] = userId! };

        return body;
    }

    private static object BuildSystemPrompt(
        string systemPrompt,
        IReadOnlyDictionary<string, object>? cacheControl)
    {
        var text = UnicodeTextSanitizer.RemoveUnpairedSurrogates(systemPrompt);
        if (cacheControl is null)
        {
            return text;
        }

        return new List<object>
        {
            new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = text,
                ["cache_control"] = cacheControl
            }
        };
    }

    private static IReadOnlyDictionary<string, object>? BuildCacheControl(Model model, StreamOptions options)
    {
        if (options.CacheRetention == CacheRetention.None)
        {
            return null;
        }

        var cacheControl = new Dictionary<string, object>
        {
            ["type"] = "ephemeral"
        };
        if (options.CacheRetention == CacheRetention.Long &&
            model.Compat?.SupportsLongCacheRetention != false)
        {
            cacheControl["ttl"] = "1h";
        }

        return cacheControl;
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

    private static Dictionary<string, object>? BuildThinking(
        Model model,
        StreamOptions options,
        ThinkingLevel? reasoning)
    {
        if (!model.Reasoning)
            return null;

        if (options is AnthropicOptions anthropicOptions)
        {
            if (anthropicOptions.ThinkingEnabled == false)
                return new Dictionary<string, object> { ["type"] = "disabled" };

            if (anthropicOptions.ThinkingEnabled == true)
            {
                var display = string.IsNullOrWhiteSpace(anthropicOptions.ThinkingDisplay)
                    ? "summarized"
                    : anthropicOptions.ThinkingDisplay!;

                if (UsesAdaptiveThinking(model))
                    return new Dictionary<string, object>
                    {
                        ["type"] = "adaptive",
                        ["display"] = display
                    };

                return new Dictionary<string, object>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = anthropicOptions.ThinkingBudgetTokens ?? 1_024,
                    ["display"] = display
                };
            }
        }

        if (!reasoning.HasValue)
            return null;

        if (UsesAdaptiveThinking(model))
            return new Dictionary<string, object>
            {
                ["type"] = "adaptive",
                ["display"] = "summarized"
            };

        var budget = StreamOptionHelpers.GetThinkingBudget(
            (options as SimpleStreamOptions)?.ThinkingBudgets,
            reasoning.Value,
            defaultMinimal: 1_024,
            defaultLow: 2_048,
            defaultMedium: 8_192,
            defaultHigh: 16_384);
        return new Dictionary<string, object>
        {
            ["type"] = "enabled",
            ["budget_tokens"] = budget,
            ["display"] = "summarized"
        };
    }

    private static bool ThinkingIsEnabled(Dictionary<string, object>? thinking) =>
        thinking is not null &&
        (!thinking.TryGetValue("type", out var type) ||
         !string.Equals(Convert.ToString(type), "disabled", StringComparison.Ordinal));

    private static bool IsAdaptiveThinking(Dictionary<string, object> thinking) =>
        thinking.TryGetValue("type", out var type) &&
        string.Equals(Convert.ToString(type), "adaptive", StringComparison.Ordinal);

    private static string MapAdaptiveThinkingEffort(Model model, ThinkingLevel reasoning)
    {
        var key = StreamOptionHelpers.ToReasoningEffortName(reasoning, allowExtraHigh: true);
        if (model.Compat?.ReasoningEffortMap is not null &&
            model.Compat.ReasoningEffortMap.TryGetValue(key, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        return reasoning switch
        {
            ThinkingLevel.Minimal or ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "high",
            _ => "high"
        };
    }

    private static object MapToolChoice(AnthropicToolChoice choice)
    {
        if (choice.IsTool)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "tool",
                ["name"] = choice.Name!
            };
        }

        return new Dictionary<string, object>
        {
            ["type"] = choice.Kind
        };
    }

    private static string? BuildAnthropicBetaHeader(Model model, LlmContext context, AnthropicOptions? options)
    {
        var betaFeatures = new List<string>();

        if (context.Tools is { Count: > 0 } &&
            model.Compat?.SupportsEagerToolInputStreaming == false)
        {
            betaFeatures.Add(FineGrainedToolStreamingBeta);
        }

        if (options?.InterleavedThinking == true && !UsesAdaptiveThinking(model))
        {
            betaFeatures.Add(InterleavedThinkingBeta);
        }

        return betaFeatures.Count == 0 ? null : string.Join(",", betaFeatures);
    }

    private static bool UsesAdaptiveThinking(Model model) =>
        model.Compat?.ForceAdaptiveThinking ?? SupportsAdaptiveThinking(model.Id);

    private static bool SupportsAdaptiveThinking(string modelId) =>
        modelId.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4-7", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("opus-4.7", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("sonnet-4.6", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetMetadataString(
        IDictionary<string, object>? metadata,
        string key,
        out string? value)
    {
        value = null;
        if (metadata is null || !metadata.TryGetValue(key, out var raw))
            return false;

        value = raw switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            JsonElement { ValueKind: JsonValueKind.String } element when !string.IsNullOrWhiteSpace(element.GetString()) => element.GetString(),
            _ => null
        };

        return value is not null;
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

public record AnthropicOptions : StreamOptions
{
    public bool? ThinkingEnabled { get; init; }
    public int? ThinkingBudgetTokens { get; init; }
    public string? Effort { get; init; }
    public string? ThinkingDisplay { get; init; }
    public bool? InterleavedThinking { get; init; }
    public AnthropicToolChoice? ToolChoice { get; init; }
}

public sealed record AnthropicToolChoice
{
    private AnthropicToolChoice(string kind, string? name)
    {
        Kind = kind;
        Name = name;
    }

    public string Kind { get; }
    public string? Name { get; }
    public bool IsTool => Name is not null;

    public static AnthropicToolChoice FromString(string choice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choice);
        return new AnthropicToolChoice(choice, name: null);
    }

    public static AnthropicToolChoice Tool(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new AnthropicToolChoice("tool", name);
    }

    public static implicit operator AnthropicToolChoice(string choice) =>
        FromString(choice);

    public static implicit operator string?(AnthropicToolChoice? choice) =>
        choice?.ToString();

    public override string ToString() =>
        IsTool ? $"tool:{Name}" : Kind;
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
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
internal partial class AnthropicRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
