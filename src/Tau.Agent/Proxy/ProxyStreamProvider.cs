using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Agent.Proxy;

/// <summary>
/// Proxies assistant streams through a server that owns provider auth.
/// Mirrors pi-mono's packages/agent/src/proxy.ts surface for .NET hosts.
/// </summary>
public sealed class ProxyStreamProvider : IStreamProvider
{
    public const string DefaultApi = "proxy";
    private const string DefaultStreamPath = "/api/stream";
    private readonly HttpClient _httpClient;

    public ProxyStreamProvider(HttpClient? httpClient = null, string api = DefaultApi)
    {
        _httpClient = httpClient ?? new HttpClient();
        Api = string.IsNullOrWhiteSpace(api) ? DefaultApi : api.Trim();
    }

    public string Api { get; }

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TryPushError(stream, ex.Message);
            }
        });

        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        Stream(model, context, options);

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream)
    {
        var resolved = ResolveOptions(model, options);
        var partial = CreatePartial(model);
        var body = BuildRequestBody(model, context, options);
        var json = JsonSerializer.Serialize(body, ProxyStreamJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildStreamUri(resolved.ProxyUrl, resolved.StreamPath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolved.AuthToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            stream.Push(new ErrorEvent(await BuildHttpErrorAsync(response).ConfigureAwait(false), partial));
            return;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var completed = false;

        await foreach (var sse in SseParser.ParseAsync(responseStream).ConfigureAwait(false))
        {
            if (sse.Data == "[DONE]")
            {
                break;
            }

            var evt = ProcessProxyEvent(sse.Data, ref partial);
            if (evt is null)
            {
                continue;
            }

            stream.Push(evt);
            if (evt is DoneEvent or ErrorEvent)
            {
                completed = true;
                break;
            }
        }

        if (!completed)
        {
            stream.Push(new ErrorEvent("Proxy stream ended without a terminal event.", partial));
        }
    }

    private static Dictionary<string, object> BuildRequestBody(
        Model model,
        LlmContext context,
        StreamOptions options)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = ConvertModel(model),
            ["context"] = ConvertContext(context),
            ["options"] = ConvertOptions(options)
        };
        return body;
    }

    private static Dictionary<string, object> ConvertModel(Model model)
    {
        var result = new Dictionary<string, object>
        {
            ["id"] = model.Id,
            ["name"] = model.Name,
            ["api"] = model.Api,
            ["provider"] = model.Provider,
            ["reasoning"] = model.Reasoning,
            ["inputModalities"] = model.InputModalities.Select(static modality => (object)modality).ToList()
        };
        AddIfNotNull(result, "baseUrl", model.BaseUrl);
        AddIfNotNull(result, "contextWindow", model.ContextWindow);
        AddIfNotNull(result, "maxOutputTokens", model.MaxOutputTokens);
        if (model.Headers is { Count: > 0 })
        {
            result["headers"] = new Dictionary<string, string>(model.Headers, StringComparer.OrdinalIgnoreCase);
        }

        if (model.Cost is { } cost)
        {
            result["cost"] = new Dictionary<string, object>
            {
                ["inputPerMillion"] = cost.InputPerMillion,
                ["outputPerMillion"] = cost.OutputPerMillion
            };
            if (cost.CacheReadPerMillion.HasValue)
            {
                ((Dictionary<string, object>)result["cost"])["cacheReadPerMillion"] = cost.CacheReadPerMillion.Value;
            }

            if (cost.CacheWritePerMillion.HasValue)
            {
                ((Dictionary<string, object>)result["cost"])["cacheWritePerMillion"] = cost.CacheWritePerMillion.Value;
            }
        }

        return result;
    }

    private static Dictionary<string, object> ConvertContext(LlmContext context)
    {
        var result = new Dictionary<string, object>
        {
            ["messages"] = context.Messages.Select(message => (object)ConvertMessage(message)).ToList()
        };
        AddIfNotNull(result, "systemPrompt", context.SystemPrompt);
        if (context.Tools is { Count: > 0 })
        {
            result["tools"] = context.Tools.Select(tool => (object)ConvertTool(tool)).ToList();
        }

        return result;
    }

    private static Dictionary<string, object> ConvertOptions(StreamOptions options)
    {
        var result = new Dictionary<string, object>();
        AddIfNotNull(result, "temperature", options.Temperature);
        AddIfNotNull(result, "maxTokens", options.MaxTokens);
        if (options is SimpleStreamOptions { Reasoning: { } reasoning })
        {
            result["reasoning"] = FormatThinkingLevel(reasoning);
        }

        return result;
    }

    private static Dictionary<string, object> ConvertMessage(ChatMessage message)
    {
        return message switch
        {
            UserMessage user => new Dictionary<string, object>
            {
                ["role"] = user.Role,
                ["content"] = user.Content.Select(block => (object)ConvertContentBlock(block)).ToList()
            },
            AssistantMessage assistant => ConvertAssistantMessage(assistant),
            ToolResultMessage tool => new Dictionary<string, object>
            {
                ["role"] = tool.Role,
                ["toolCallId"] = tool.ToolCallId,
                ["content"] = tool.Content.Select(block => (object)ConvertContentBlock(block)).ToList(),
                ["isError"] = tool.IsError
            },
            _ => new Dictionary<string, object> { ["role"] = message.Role }
        };
    }

    private static Dictionary<string, object> ConvertAssistantMessage(AssistantMessage message)
    {
        var result = new Dictionary<string, object>
        {
            ["role"] = message.Role,
            ["content"] = message.Content.Select(block => (object)ConvertContentBlock(block)).ToList()
        };
        if (message.Usage is { } usage)
        {
            result["usage"] = ConvertUsage(usage);
        }

        AddIfNotNull(result, "stopReason", FormatStopReason(message.StopReason));
        AddIfNotNull(result, "errorMessage", message.ErrorMessage);
        AddIfNotNull(result, "api", message.Api);
        AddIfNotNull(result, "provider", message.Provider);
        AddIfNotNull(result, "model", message.Model);
        AddIfNotNull(result, "responseId", message.ResponseId);
        if (message.Timestamp.HasValue)
        {
            result["timestamp"] = message.Timestamp.Value.ToUnixTimeMilliseconds();
        }

        return result;
    }

    private static Dictionary<string, object> ConvertContentBlock(ContentBlock block)
    {
        var result = block switch
        {
            TextContent text => new Dictionary<string, object>
            {
                ["type"] = text.Type,
                ["text"] = text.Text
            },
            ThinkingContent thinking => new Dictionary<string, object>
            {
                ["type"] = thinking.Type,
                ["thinking"] = thinking.Thinking,
                ["redacted"] = thinking.Redacted
            },
            ImageContent image => new Dictionary<string, object>
            {
                ["type"] = image.Type,
                ["data"] = image.Data,
                ["mimeType"] = image.MimeType
            },
            ToolCallContent toolCall => new Dictionary<string, object>
            {
                ["type"] = toolCall.Type,
                ["id"] = toolCall.Id,
                ["name"] = toolCall.Name,
                ["arguments"] = toolCall.Arguments
            },
            _ => new Dictionary<string, object> { ["type"] = block.Type }
        };

        switch (block)
        {
            case TextContent { TextSignature: { } signature }:
                result["textSignature"] = signature;
                break;
            case ThinkingContent { ThinkingSignature: { } signature }:
                result["thinkingSignature"] = signature;
                break;
            case ToolCallContent { ThoughtSignature: { } signature }:
                result["thoughtSignature"] = signature;
                break;
        }

        return result;
    }

    private static Dictionary<string, object> ConvertTool(Tool tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["parameterSchema"] = tool.ParameterSchema
    };

    private static Dictionary<string, object> ConvertUsage(Usage usage)
    {
        var result = new Dictionary<string, object>
        {
            ["input"] = usage.InputTokens,
            ["output"] = usage.OutputTokens
        };
        AddIfNotNull(result, "cacheRead", usage.CacheReadTokens);
        AddIfNotNull(result, "cacheWrite", usage.CacheWriteTokens);
        AddIfNotNull(result, "serviceTier", usage.ServiceTier);
        if (usage.Cost is { } cost)
        {
            result["cost"] = new Dictionary<string, object>
            {
                ["input"] = cost.Input,
                ["output"] = cost.Output,
                ["cacheRead"] = cost.CacheRead,
                ["cacheWrite"] = cost.CacheWrite,
                ["total"] = cost.Total
            };
        }

        return result;
    }

    private static AssistantMessage CreatePartial(Model model) => new()
    {
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        Usage = new Usage(0, 0),
        Timestamp = DateTimeOffset.UtcNow,
        Content = []
    };

    private static StreamEvent? ProcessProxyEvent(string json, ref AssistantMessage partial)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = GetString(root, "type");
        switch (type)
        {
            case "start":
                return new StartEvent(partial);
            case "text_start":
            {
                var index = RequireContentIndex(root);
                SetBlock(ref partial, index, new TextContent(""));
                return new TextStartEvent(index, partial);
            }
            case "text_delta":
            {
                var index = RequireContentIndex(root);
                var delta = GetString(root, "delta") ?? string.Empty;
                UpdateBlock<TextContent>(ref partial, index, text => text with { Text = text.Text + delta });
                return new TextDeltaEvent(index, delta, partial);
            }
            case "text_end":
            {
                var index = RequireContentIndex(root);
                var signature = GetString(root, "contentSignature", "content_signature");
                UpdateBlock<TextContent>(ref partial, index, text => text with { TextSignature = signature });
                return new TextEndEvent(index, partial);
            }
            case "thinking_start":
            {
                var index = RequireContentIndex(root);
                SetBlock(ref partial, index, new ThinkingContent(""));
                return new ThinkingStartEvent(index, partial);
            }
            case "thinking_delta":
            {
                var index = RequireContentIndex(root);
                var delta = GetString(root, "delta") ?? string.Empty;
                UpdateBlock<ThinkingContent>(ref partial, index, thinking => thinking with { Thinking = thinking.Thinking + delta });
                return new ThinkingDeltaEvent(index, delta, partial);
            }
            case "thinking_end":
            {
                var index = RequireContentIndex(root);
                var signature = GetString(root, "contentSignature", "content_signature");
                UpdateBlock<ThinkingContent>(ref partial, index, thinking => thinking with { ThinkingSignature = signature });
                return new ThinkingEndEvent(index, partial);
            }
            case "toolcall_start":
            {
                var index = RequireContentIndex(root);
                var id = GetString(root, "id") ?? Guid.NewGuid().ToString("N");
                var name = GetString(root, "toolName", "tool_name") ?? string.Empty;
                SetBlock(ref partial, index, new ToolCallContent(id, name, string.Empty));
                return new ToolCallStartEvent(index, partial);
            }
            case "toolcall_delta":
            {
                var index = RequireContentIndex(root);
                var delta = GetString(root, "delta") ?? string.Empty;
                UpdateBlock<ToolCallContent>(ref partial, index, toolCall => toolCall with { Arguments = toolCall.Arguments + delta });
                return new ToolCallDeltaEvent(index, delta, partial);
            }
            case "toolcall_end":
            {
                var index = RequireContentIndex(root);
                return new ToolCallEndEvent(index, partial);
            }
            case "done":
            {
                partial = partial with
                {
                    StopReason = MapStopReason(GetString(root, "reason")),
                    Usage = ReadUsage(root) ?? partial.Usage,
                    Timestamp = DateTimeOffset.UtcNow
                };
                return new DoneEvent(partial);
            }
            case "error":
            {
                var message = GetString(root, "errorMessage", "error_message") ?? "Proxy stream error.";
                partial = partial with
                {
                    StopReason = StopReason.Error,
                    ErrorMessage = message,
                    Usage = ReadUsage(root) ?? partial.Usage,
                    Timestamp = DateTimeOffset.UtcNow
                };
                return new ErrorEvent(message, partial);
            }
            default:
                return null;
        }
    }

    private static void SetBlock(ref AssistantMessage partial, int index, ContentBlock block)
    {
        if (index < 0)
        {
            throw new InvalidOperationException("Proxy event contentIndex must be non-negative.");
        }

        var content = partial.Content.ToList();
        while (content.Count <= index)
        {
            content.Add(new TextContent(""));
        }

        content[index] = block;
        partial = partial with { Content = content };
    }

    private static void UpdateBlock<TBlock>(
        ref AssistantMessage partial,
        int index,
        Func<TBlock, ContentBlock> update)
        where TBlock : ContentBlock
    {
        if (index < 0 || index >= partial.Content.Count || partial.Content[index] is not TBlock block)
        {
            throw new InvalidOperationException($"Received proxy event for missing {typeof(TBlock).Name} block at index {index}.");
        }

        var content = partial.Content.ToList();
        content[index] = update(block);
        partial = partial with { Content = content };
    }

    private static int RequireContentIndex(JsonElement element) =>
        GetInt(element, "contentIndex", "content_index") ??
        throw new InvalidOperationException("Proxy event is missing contentIndex.");

    private static Usage? ReadUsage(JsonElement element)
    {
        if (!TryGetProperty(element, out var usage, "usage") || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = GetInt(usage, "input", "inputTokens", "input_tokens", "prompt_tokens") ?? 0;
        var output = GetInt(usage, "output", "outputTokens", "output_tokens", "completion_tokens") ?? 0;
        var cacheRead = GetInt(usage, "cacheRead", "cacheReadTokens", "cache_read_tokens");
        var cacheWrite = GetInt(usage, "cacheWrite", "cacheWriteTokens", "cache_write_tokens");
        var serviceTier = GetString(usage, "serviceTier", "service_tier");
        return new Usage(input, output, cacheRead, cacheWrite, serviceTier, ReadUsageCost(usage));
    }

    private static UsageCost? ReadUsageCost(JsonElement usage)
    {
        if (!TryGetProperty(usage, out var cost, "cost") || cost.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new UsageCost(
            Input: GetDecimal(cost, "input", "input_cost") ?? 0m,
            Output: GetDecimal(cost, "output", "output_cost") ?? 0m,
            CacheRead: GetDecimal(cost, "cacheRead", "cache_read") ?? 0m,
            CacheWrite: GetDecimal(cost, "cacheWrite", "cache_write") ?? 0m);
    }

    private static StopReason MapStopReason(string? value) =>
        value switch
        {
            "length" => StopReason.MaxTokens,
            "toolUse" or "tool_use" => StopReason.ToolUse,
            "error" => StopReason.Error,
            "aborted" or "cancelled" => StopReason.Aborted,
            _ => StopReason.EndTurn
        };

    private static string? FormatStopReason(StopReason? reason) =>
        reason switch
        {
            StopReason.EndTurn => "stop",
            StopReason.MaxTokens => "length",
            StopReason.ToolUse => "toolUse",
            StopReason.ContentFilter => "contentFilter",
            StopReason.Error => "error",
            StopReason.Aborted => "aborted",
            _ => null
        };

    private static string FormatThinkingLevel(ThinkingLevel level) =>
        level switch
        {
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "xhigh",
            _ => "medium"
        };

    private static ResolvedProxyOptions ResolveOptions(Model model, StreamOptions options)
    {
        var proxyOptions = options as ProxyStreamOptions;
        var proxyUrl = FirstNonEmpty(
            proxyOptions?.ProxyUrl,
            GetMetadataString(options, "proxyUrl"),
            model.BaseUrl);
        if (proxyUrl is null)
        {
            throw new InvalidOperationException("Proxy URL is required. Set ProxyStreamOptions.ProxyUrl or Model.BaseUrl.");
        }

        var authToken = FirstNonEmpty(
            proxyOptions?.AuthToken,
            GetMetadataString(options, "authToken"),
            options.ApiKey);
        if (authToken is null)
        {
            throw new InvalidOperationException("Proxy auth token is required. Set ProxyStreamOptions.AuthToken or StreamOptions.ApiKey.");
        }

        var streamPath = FirstNonEmpty(
            proxyOptions?.StreamPath,
            GetMetadataString(options, "streamPath"),
            DefaultStreamPath)!;
        return new ResolvedProxyOptions(proxyUrl, authToken, streamPath);
    }

    private static Uri BuildStreamUri(string proxyUrl, string streamPath)
    {
        var baseUri = new Uri(proxyUrl.EndsWith("/", StringComparison.Ordinal) ? proxyUrl : proxyUrl + "/", UriKind.Absolute);
        return Uri.TryCreate(streamPath, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(baseUri, streamPath.TrimStart('/'));
    }

    private static async Task<string> BuildHttpErrorAsync(HttpResponseMessage response)
    {
        var fallback = $"Proxy error: {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (GetString(root, "error") is { } error)
            {
                return $"Proxy error: {error}";
            }

            if (TryGetProperty(root, out var errorElement, "error") &&
                errorElement.ValueKind == JsonValueKind.Object &&
                GetString(errorElement, "message") is { } message)
            {
                return $"Proxy error: {message}";
            }
        }
        catch (JsonException)
        {
            return $"Proxy error: {(int)response.StatusCode}: {body}";
        }

        return fallback;
    }

    private static void TryPushError(AssistantMessageStream stream, string message)
    {
        try
        {
            stream.Push(new ErrorEvent(message));
        }
        catch (InvalidOperationException)
        {
            // The consumer may already have received a terminal proxy event.
        }
    }

    private static void AddIfNotNull<T>(IDictionary<string, object> target, string key, T? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(static value => string.IsNullOrWhiteSpace(value) ? null : value.Trim()).FirstOrDefault(static value => value is not null);

    private static string? GetMetadataString(StreamOptions options, string key) =>
        options.Metadata is not null &&
        options.Metadata.TryGetValue(key, out var value) &&
        value is not null
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string? GetString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var intValue) ? intValue : null;
    }

    private static decimal? GetDecimal(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetDecimal(out var decimalValue) ? decimalValue : null;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record ResolvedProxyOptions(string ProxyUrl, string AuthToken, string StreamPath);
}

public sealed record ProxyStreamOptions : SimpleStreamOptions
{
    public string? AuthToken { get; init; }
    public string? ProxyUrl { get; init; }
    public string StreamPath { get; init; } = "/api/stream";
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(float?))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(decimal?))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(long?))]
internal partial class ProxyStreamJsonContext : JsonSerializerContext;
