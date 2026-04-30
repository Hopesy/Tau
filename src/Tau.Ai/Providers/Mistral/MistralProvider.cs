using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Mistral;

public sealed class MistralProvider : IStreamProvider
{
    private const int MistralToolCallIdLength = 9;
    private readonly HttpClient _httpClient;

    public MistralProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Api => "mistral-conversations";

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
                stream.Push(new ErrorEvent(ex.Message));
            }
        });
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        var nativeOptions = new MistralOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens ?? model.MaxOutputTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            MaxRetryDelay = options.MaxRetryDelay,
            Metadata = options.Metadata,
            PromptMode = model.Reasoning && options.Reasoning is not null && !UsesReasoningEffort(model)
                ? "reasoning"
                : null,
            ReasoningEffort = model.Reasoning && options.Reasoning is not null && UsesReasoningEffort(model)
                ? "high"
                : null
        };
        return Stream(model, context, nativeOptions);
    }

    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream)
    {
        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://api.mistral.ai/v1";
        var url = $"{baseUrl}/chat/completions";
        var body = BuildRequestBody(model, context, options);
        var json = JsonSerializer.Serialize(body, MistralJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeader(request, options.ApiKey);
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);
        if (!string.IsNullOrWhiteSpace(options.SessionId) && !request.Headers.Contains("x-affinity"))
        {
            request.Headers.TryAddWithoutValidation("x-affinity", options.SessionId);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            stream.Push(new ErrorEvent($"{Api} error {(int)response.StatusCode}: {errorBody}"));
            return;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
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
        await foreach (var sse in SseParser.ParseAsync(responseStream))
        {
            if (sse.Data == "[DONE]")
            {
                break;
            }

            ApplyMistralMetadata(sse.Data, ref partial);
            OpenAiStreamParser.ParseChunk(
                sse.Data,
                stream,
                ref partial,
                ref toolCallAccumulators,
                ref contentIndex);
        }
    }

    private static void ApplyMistralMetadata(string json, ref AssistantMessage partial)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && partial.ResponseId is null)
        {
            partial = partial with { ResponseId = id.GetString() };
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            var input = GetInt(usage, "prompt_tokens") ?? 0;
            var output = GetInt(usage, "completion_tokens") ?? 0;
            partial = partial with { Usage = new Usage(input, output) };
        }
    }

    private static Dictionary<string, object> BuildRequestBody(Model model, LlmContext context, StreamOptions options)
    {
        var normalizer = new MistralToolCallIdNormalizer();
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["messages"] = ConvertMessages(model, context, normalizer)
        };

        var tools = ConvertTools(context.Tools);
        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        if (options.Temperature.HasValue)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.MaxTokens.HasValue)
        {
            body["max_tokens"] = options.MaxTokens.Value;
        }

        if (options.TopP.HasValue)
        {
            body["top_p"] = options.TopP.Value;
        }

        if (options is MistralOptions mistralOptions)
        {
            if (!string.IsNullOrWhiteSpace(mistralOptions.ToolChoice))
            {
                body["tool_choice"] = mistralOptions.ToolChoice!;
            }

            if (!string.IsNullOrWhiteSpace(mistralOptions.PromptMode))
            {
                body["prompt_mode"] = mistralOptions.PromptMode!;
            }

            if (!string.IsNullOrWhiteSpace(mistralOptions.ReasoningEffort))
            {
                body["reasoning_effort"] = mistralOptions.ReasoningEffort!;
            }
        }

        return body;
    }

    private static List<object> ConvertMessages(Model model, LlmContext context, MistralToolCallIdNormalizer normalizer)
    {
        var messages = new List<object>();
        var toolCallIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(context.SystemPrompt))
        {
            messages.Add(new Dictionary<string, object>
            {
                ["role"] = "system",
                ["content"] = context.SystemPrompt!
            });
        }

        foreach (var message in context.Messages)
        {
            switch (message)
            {
                case UserMessage user:
                    messages.Add(ConvertUserMessage(user, model));
                    break;
                case AssistantMessage assistant:
                    var assistantMessage = ConvertAssistantMessage(assistant, normalizer, toolCallIdMap);
                    if (assistantMessage.Count > 1)
                    {
                        messages.Add(assistantMessage);
                    }
                    break;
                case ToolResultMessage toolResult:
                    var toolCallId = toolCallIdMap.TryGetValue(toolResult.ToolCallId, out var mappedId)
                        ? mappedId
                        : normalizer.Normalize(toolResult.ToolCallId);
                    messages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolCallId,
                        ["content"] = GetText(toolResult.Content, toolResult.IsError)
                    });
                    break;
            }
        }

        return messages;
    }

    private static Dictionary<string, object> ConvertUserMessage(UserMessage message, Model model)
    {
        var text = string.Join(string.Empty, message.Content.OfType<TextContent>().Select(block => block.Text));
        var hasImages = message.Content.Any(block => block is ImageContent);
        if (hasImages && !model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase))
        {
            text = string.IsNullOrWhiteSpace(text)
                ? "(image omitted: model does not support images)"
                : $"{text}\n(image omitted: model does not support images)";
        }

        return new Dictionary<string, object>
        {
            ["role"] = "user",
            ["content"] = text
        };
    }

    private static Dictionary<string, object> ConvertAssistantMessage(
        AssistantMessage message,
        MistralToolCallIdNormalizer normalizer,
        IDictionary<string, string> toolCallIdMap)
    {
        var result = new Dictionary<string, object> { ["role"] = "assistant" };
        var content = new StringBuilder();
        var toolCalls = new List<object>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    content.Append(text.Text);
                    break;
                case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                    content.Append(thinking.Thinking);
                    break;
                case ToolCallContent toolCall:
                    var normalizedId = normalizer.Normalize(toolCall.Id);
                    toolCallIdMap[toolCall.Id] = normalizedId;
                    toolCalls.Add(new Dictionary<string, object>
                    {
                        ["id"] = normalizedId,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object>
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = toolCall.Arguments
                        }
                    });
                    break;
            }
        }

        if (content.Length > 0)
        {
            result["content"] = content.ToString();
        }

        if (toolCalls.Count > 0)
        {
            result["tool_calls"] = toolCalls;
        }

        return result;
    }

    private static List<object> ConvertTools(IReadOnlyList<Tool>? tools)
    {
        var result = new List<object>();
        if (tools is null)
        {
            return result;
        }

        foreach (var tool in tools)
        {
            result.Add(new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ParameterSchema,
                    ["strict"] = false
                }
            });
        }

        return result;
    }

    private static string GetText(IReadOnlyList<ContentBlock> content, bool isError)
    {
        var text = string.Join("\n", content.OfType<TextContent>().Select(block => block.Text)).Trim();
        if (text.Length == 0)
        {
            text = "(no tool output)";
        }

        return isError ? $"[tool error] {text}" : text;
    }

    private static void ApplyAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || Auth.EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static bool UsesReasoningEffort(Model model) =>
        model.Id.Equals("mistral-small-2603", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Equals("mistral-small-latest", StringComparison.OrdinalIgnoreCase);

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private sealed class MistralToolCallIdNormalizer
    {
        private readonly Dictionary<string, string> _idMap = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _reverseMap = new(StringComparer.Ordinal);

        public string Normalize(string id)
        {
            if (_idMap.TryGetValue(id, out var existing))
            {
                return existing;
            }

            var attempt = 0;
            while (true)
            {
                var candidate = Derive(id, attempt);
                if (!_reverseMap.TryGetValue(candidate, out var owner) || owner == id)
                {
                    _idMap[id] = candidate;
                    _reverseMap[candidate] = id;
                    return candidate;
                }

                attempt++;
            }
        }

        private static string Derive(string id, int attempt)
        {
            var normalized = new string(id.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (attempt == 0 && normalized.Length == MistralToolCallIdLength)
            {
                return normalized;
            }

            var seed = attempt == 0 ? (normalized.Length == 0 ? id : normalized) : $"{id}:{attempt}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return new string(Convert.ToHexString(hash)
                .Where(char.IsAsciiLetterOrDigit)
                .Take(MistralToolCallIdLength)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }
    }
}

public record MistralOptions : StreamOptions
{
    public string? ToolChoice { get; init; }
    public string? PromptMode { get; init; }
    public string? ReasoningEffort { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(float?))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(decimal?))]
internal partial class MistralJsonContext : JsonSerializerContext;
