using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAiResponses;

public static class OpenAiResponsesShared
{
    private static readonly HashSet<string> DefaultToolCallProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        "openai-codex",
        "opencode",
        "azure-openai-responses"
    };

    public static List<object> ConvertResponsesMessages(Model model, LlmContext context)
    {
        var messages = new List<object>();
        var normalizedToolCallIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingToolCalls = new List<ToolCallContent>();
        var existingToolResults = new HashSet<string>(StringComparer.Ordinal);
        var assistantMessageIndex = 0;

        if (!string.IsNullOrWhiteSpace(context.SystemPrompt))
        {
            messages.Add(new Dictionary<string, object>
            {
                ["role"] = model.Reasoning ? "developer" : "system",
                ["content"] = SanitizeText(context.SystemPrompt!)
            });
        }

        void InsertSyntheticToolResults()
        {
            foreach (var toolCall in pendingToolCalls)
            {
                if (!existingToolResults.Contains(toolCall.Id))
                {
                    messages.Add(BuildFunctionCallOutput(toolCall.Id, "No result provided", isError: true));
                }
            }

            pendingToolCalls.Clear();
            existingToolResults.Clear();
        }

        foreach (var message in context.Messages)
        {
            switch (message)
            {
                case UserMessage user:
                    InsertSyntheticToolResults();
                    var userMessage = ConvertUserMessage(user, model);
                    if (userMessage is not null)
                    {
                        messages.Add(userMessage);
                    }
                    break;

                case AssistantMessage assistant:
                    InsertSyntheticToolResults();
                    if (assistant.StopReason is StopReason.Error)
                    {
                        break;
                    }

                    var converted = ConvertAssistantMessage(
                        assistant,
                        model,
                        normalizedToolCallIds,
                        ref assistantMessageIndex);
                    messages.AddRange(converted.Items);
                    pendingToolCalls = converted.ToolCalls;
                    existingToolResults = new HashSet<string>(StringComparer.Ordinal);
                    break;

                case ToolResultMessage toolResult:
                    var normalizedId = normalizedToolCallIds.TryGetValue(toolResult.ToolCallId, out var mappedId)
                        ? mappedId
                        : toolResult.ToolCallId;
                    existingToolResults.Add(normalizedId);
                    messages.Add(BuildFunctionCallOutput(normalizedId, BuildToolResultOutput(toolResult.Content, model), toolResult.IsError));
                    break;
            }
        }

        InsertSyntheticToolResults();
        return messages;
    }

    public static List<object> ConvertResponsesMessages(Model model, LlmContext context, bool includeSystemPrompt)
    {
        if (includeSystemPrompt)
        {
            return ConvertResponsesMessages(model, context);
        }

        return ConvertResponsesMessages(model, context with { SystemPrompt = null });
    }

    public static List<object> ConvertResponsesTools(IReadOnlyList<Tool>? tools, bool? strict = null)
    {
        var result = new List<object>();
        if (tools is null)
        {
            return result;
        }

        foreach (var tool in tools)
        {
            var entry = new Dictionary<string, object>
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.ParameterSchema
            };
            if (strict.HasValue)
            {
                entry["strict"] = strict.Value;
            }

            result.Add(entry);
        }

        return result;
    }

    public static string NormalizeToolCallId(string id, Model targetModel, AssistantMessage source)
    {
        if (!DefaultToolCallProviders.Contains(targetModel.Provider))
        {
            return NormalizeIdPart(id);
        }

        if (!id.Contains('|', StringComparison.Ordinal))
        {
            return NormalizeIdPart(id);
        }

        var parts = id.Split('|', 2);
        var callId = NormalizeIdPart(parts[0]);
        var itemId = parts[1];
        var isForeignToolCall =
            !string.Equals(source.Provider, targetModel.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Api, targetModel.Api, StringComparison.OrdinalIgnoreCase);

        var normalizedItemId = isForeignToolCall
            ? $"fc_{ShortHash(itemId)}"
            : NormalizeIdPart(itemId);
        if (!normalizedItemId.StartsWith("fc_", StringComparison.Ordinal))
        {
            normalizedItemId = NormalizeIdPart($"fc_{normalizedItemId}");
        }

        return $"{callId}|{normalizedItemId}";
    }

    public static (string CallId, string? ItemId) SplitToolCallId(string id)
    {
        var parts = id.Split('|', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (id, null);
    }

    public static string ExtractAccountIdFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Codex token is not a JWT.");
        }

        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("https://api.openai.com/auth", out var auth) ||
            !auth.TryGetProperty("chatgpt_account_id", out var accountId) ||
            accountId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accountId.GetString()))
        {
            throw new InvalidOperationException("Codex token does not contain chatgpt_account_id.");
        }

        return accountId.GetString()!;
    }

    public static string MapCodexEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return json;
        }

        var type = typeElement.GetString();
        if (type is not ("response.done" or "response.incomplete"))
        {
            return json;
        }

        var clone = JsonSerializer.Deserialize(json, OpenAiResponsesJsonContext.Default.DictionaryStringObject)
            ?? new Dictionary<string, object>();
        clone["type"] = "response.completed";
        if (clone.TryGetValue("response", out var responseValue) && responseValue is JsonElement responseElement)
        {
            var response = JsonSerializer.Deserialize(responseElement.GetRawText(), OpenAiResponsesJsonContext.Default.DictionaryStringObject)
                ?? new Dictionary<string, object>();
            response["status"] = type == "response.incomplete" ? "incomplete" : "completed";
            clone["response"] = response;
        }

        return JsonSerializer.Serialize(clone, OpenAiResponsesJsonContext.Default.DictionaryStringObject);
    }

    public static async Task ProcessResponsesStreamAsync(
        Stream responseStream,
        AssistantMessage partial,
        AssistantMessageStream stream,
        Func<string, string>? eventMapper = null,
        string? requestedServiceTier = null,
        Func<string?, string?, string?>? resolveServiceTier = null,
        CancellationToken cancellationToken = default)
    {
        var state = new ResponsesStreamState(
            partial,
            stream,
            requestedServiceTier: requestedServiceTier,
            resolveServiceTier: resolveServiceTier);
        await foreach (var sse in SseParser.ParseAsync(responseStream, cancellationToken).ConfigureAwait(false))
        {
            if (sse.Data == "[DONE]")
            {
                break;
            }

            state.ProcessJson(eventMapper is null ? sse.Data : eventMapper(sse.Data));
            if (state.IsComplete)
            {
                break;
            }
        }
    }

    public static async Task<bool> ProcessResponsesJsonEventsAsync(
        IAsyncEnumerable<string> jsonEvents,
        AssistantMessage partial,
        AssistantMessageStream stream,
        Func<string, string>? eventMapper = null,
        Action? beforeDone = null,
        string? requestedServiceTier = null,
        Func<string?, string?, string?>? resolveServiceTier = null,
        CancellationToken cancellationToken = default)
    {
        var state = new ResponsesStreamState(
            partial,
            stream,
            beforeDone,
            requestedServiceTier,
            resolveServiceTier);
        await foreach (var json in jsonEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            state.ProcessJson(eventMapper is null ? json : eventMapper(json));
            if (state.IsComplete)
            {
                return true;
            }
        }

        return state.IsComplete;
    }

    public static void AddBaseParameters(Dictionary<string, object> body, Model model, StreamOptions options)
    {
        if (options.MaxTokens.HasValue)
        {
            body["max_output_tokens"] = options.MaxTokens.Value;
        }

        if (options.Temperature.HasValue)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.TopP.HasValue)
        {
            body["top_p"] = options.TopP.Value;
        }

        if (options.CacheRetention != CacheRetention.None && !string.IsNullOrWhiteSpace(options.SessionId))
        {
            body["prompt_cache_key"] = options.SessionId!;
        }

        if (options.CacheRetention == CacheRetention.Long &&
            (string.IsNullOrWhiteSpace(model.BaseUrl) ||
             model.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase)))
        {
            body["prompt_cache_retention"] = "24h";
        }
    }

    public static string? MapReasoningEffort(ThinkingLevel? level, Model model)
    {
        if (level is null)
        {
            return null;
        }

        if (level == ThinkingLevel.ExtraHigh && !Tau.Ai.Registry.ModelCatalog.SupportsXhigh(model))
        {
            return "high";
        }

        return level.Value switch
        {
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.ExtraHigh => "xhigh",
            _ => "medium"
        };
    }

    public static bool IsRetryableError(int statusCode, string errorText) =>
        statusCode is 429 or 500 or 502 or 503 or 504 ||
        System.Text.RegularExpressions.Regex.IsMatch(
            errorText,
            "rate.?limit|overloaded|service.?unavailable|upstream.?connect|connection.?refused",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

    private static object? ConvertUserMessage(UserMessage message, Model model)
    {
        var content = new List<object>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "input_text",
                        ["text"] = SanitizeText(text.Text)
                    });
                    break;
                case ImageContent image when model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase):
                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "input_image",
                        ["detail"] = "auto",
                        ["image_url"] = $"data:{image.MimeType};base64,{image.Data}"
                    });
                    break;
            }
        }

        return content.Count == 0
            ? null
            : new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = content
            };
    }

    private static AssistantConversion ConvertAssistantMessage(
        AssistantMessage message,
        Model model,
        IDictionary<string, string> normalizedToolCallIds,
        ref int assistantMessageIndex)
    {
        var items = new List<object>();
        var toolCalls = new List<ToolCallContent>();

        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.ThinkingSignature):
                    if (TryParseObject(thinking.ThinkingSignature!, out var reasoningItem))
                    {
                        items.Add(reasoningItem);
                    }
                    break;
                case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                    items.Add(BuildAssistantTextItem($"msg_{assistantMessageIndex++}", SanitizeText(thinking.Thinking), null));
                    break;
                case TextContent text:
                    var signature = ParseTextSignature(text.TextSignature);
                    var messageId = signature.Id ?? $"msg_{assistantMessageIndex++}";
                    if (messageId.Length > 64)
                    {
                        messageId = $"msg_{ShortHash(messageId)}";
                    }

                    items.Add(BuildAssistantTextItem(messageId, SanitizeText(text.Text), signature.Phase));
                    break;
                case ToolCallContent toolCall:
                    var normalizedId = NormalizeToolCallId(toolCall.Id, model, message);
                    normalizedToolCallIds[toolCall.Id] = normalizedId;
                    var (callId, itemId) = SplitToolCallId(normalizedId);
                    var functionCall = new Dictionary<string, object>
                    {
                        ["type"] = "function_call",
                        ["call_id"] = callId,
                        ["name"] = toolCall.Name,
                        ["arguments"] = toolCall.Arguments
                    };
                    if (!string.IsNullOrWhiteSpace(itemId))
                    {
                        functionCall["id"] = itemId!;
                    }

                    items.Add(functionCall);
                    toolCalls.Add(toolCall with { Id = normalizedId });
                    break;
            }
        }

        return new AssistantConversion(items, toolCalls);
    }

    private static Dictionary<string, object> BuildAssistantTextItem(string id, string text, string? phase)
    {
        var item = new Dictionary<string, object>
        {
            ["type"] = "message",
            ["role"] = "assistant",
            ["status"] = "completed",
            ["id"] = id,
            ["content"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "output_text",
                    ["text"] = text,
                    ["annotations"] = new List<object>()
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(phase))
        {
            item["phase"] = phase!;
        }

        return item;
    }

    private static Dictionary<string, object> BuildFunctionCallOutput(string toolCallId, object output, bool isError)
    {
        var (callId, _) = SplitToolCallId(toolCallId);
        var result = new Dictionary<string, object>
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output
        };
        if (isError)
        {
            result["status"] = "failed";
        }

        return result;
    }

    private static object BuildToolResultOutput(IReadOnlyList<ContentBlock> content, Model model)
    {
        var text = string.Join("\n", content.OfType<TextContent>().Select(block => block.Text));
        text = SanitizeText(text);
        var images = content.OfType<ImageContent>().ToList();
        if (images.Count > 0 && model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase))
        {
            var output = new List<object>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                output.Add(new Dictionary<string, object>
                {
                    ["type"] = "input_text",
                    ["text"] = text
                });
            }

            foreach (var image in images)
            {
                output.Add(new Dictionary<string, object>
                {
                    ["type"] = "input_image",
                    ["detail"] = "auto",
                    ["image_url"] = $"data:{image.MimeType};base64,{image.Data}"
                });
            }

            return output;
        }

        if (images.Count > 0)
        {
            return !string.IsNullOrWhiteSpace(text) ? text : "(see attached image)";
        }

        return text;
    }

    private static string SanitizeText(string text) =>
        UnicodeTextSanitizer.RemoveUnpairedSurrogates(text);

    private static bool TryParseObject(string json, out object value)
    {
        try
        {
            value = JsonSerializer.Deserialize(json, OpenAiResponsesJsonContext.Default.DictionaryStringObject)
                ?? new Dictionary<string, object>();
            return true;
        }
        catch (JsonException)
        {
            value = new Dictionary<string, object>();
            return false;
        }
    }

    private static (string? Id, string? Phase) ParseTextSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return (null, null);
        }

        if (signature.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(signature);
                var root = doc.RootElement;
                if (root.TryGetProperty("v", out var version) && version.GetInt32() == 1 &&
                    root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var phase = root.TryGetProperty("phase", out var phaseElement) && phaseElement.ValueKind == JsonValueKind.String
                        ? phaseElement.GetString()
                        : null;
                    return (id.GetString(), phase);
                }
            }
            catch (JsonException)
            {
                return (signature, null);
            }
        }

        return (signature, null);
    }

    private static string NormalizeIdPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        var normalized = builder.ToString();
        if (normalized.Length > 64)
        {
            normalized = normalized[..64];
        }

        return normalized.TrimEnd('_');
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string EncodeTextSignature(string? id, string? phase)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var payload = new Dictionary<string, object>
        {
            ["v"] = 1,
            ["id"] = id!
        };
        if (!string.IsNullOrWhiteSpace(phase))
        {
            payload["phase"] = phase!;
        }

        return JsonSerializer.Serialize(payload, OpenAiResponsesJsonContext.Default.DictionaryStringObject);
    }

    private sealed record AssistantConversion(List<object> Items, List<ToolCallContent> ToolCalls);

    private sealed class ResponsesStreamState
    {
        private readonly AssistantMessageStream _stream;
        private readonly Dictionary<string, int> _itemIndexes = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _toolCallArguments = new();
        private readonly Action? _beforeDone;
        private readonly string? _requestedServiceTier;
        private readonly Func<string?, string?, string?>? _resolveServiceTier;
        private AssistantMessage _partial;
        public bool IsComplete { get; private set; }

        public ResponsesStreamState(
            AssistantMessage partial,
            AssistantMessageStream stream,
            Action? beforeDone = null,
            string? requestedServiceTier = null,
            Func<string?, string?, string?>? resolveServiceTier = null)
        {
            _partial = partial;
            _stream = stream;
            _beforeDone = beforeDone;
            _requestedServiceTier = requestedServiceTier;
            _resolveServiceTier = resolveServiceTier;
        }

        public void ProcessJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (GetString(root, "type"))
            {
                case "response.created":
                    ApplyResponse(root);
                    break;
                case "response.output_item.added":
                    AddOutputItem(root);
                    break;
                case "response.content_part.added":
                    AddContentPart(root);
                    break;
                case "response.reasoning_summary_part.added":
                case "response.reasoning_summary_text.delta":
                case "response.reasoning.delta":
                    AppendThinkingDelta(root);
                    break;
                case "response.output_text.delta":
                case "response.refusal.delta":
                    AppendTextDelta(root);
                    break;
                case "response.function_call_arguments.delta":
                    AppendToolCallDelta(root);
                    break;
                case "response.function_call_arguments.done":
                    CompleteToolCallArguments(root);
                    break;
                case "response.output_item.done":
                    CompleteOutputItem(root);
                    break;
                case "response.completed":
                    CompleteResponse(root);
                    break;
                case "response.failed":
                case "error":
                    _stream.Push(new ErrorEvent(ExtractError(root), _partial));
                    IsComplete = true;
                    break;
            }
        }

        private void ApplyResponse(JsonElement root)
        {
            if (!root.TryGetProperty("response", out var response))
            {
                return;
            }

            if (GetString(response, "id") is { } id)
            {
                _partial = _partial with { ResponseId = id };
            }

            if (response.TryGetProperty("usage", out var usage))
            {
                _partial = _partial with { Usage = ExtractUsage(usage, ResolveServiceTier(response)) };
            }
        }

        private void AddOutputItem(JsonElement root)
        {
            if (!root.TryGetProperty("item", out var item))
            {
                return;
            }

            var itemId = GetString(item, "id") ??
                         GetString(root, "item_id") ??
                         Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            switch (GetString(item, "type"))
            {
                case "reasoning":
                    AddBlock(itemId, new ThinkingContent(""));
                    _stream.Push(new ThinkingStartEvent(_itemIndexes[itemId], _partial));
                    break;
                case "message":
                    AddBlock(itemId, new TextContent(""));
                    _stream.Push(new TextStartEvent(_itemIndexes[itemId], _partial));
                    break;
                case "function_call":
                    var callId = GetString(item, "call_id") ?? GetString(item, "id") ?? itemId;
                    var name = GetString(item, "name") ?? string.Empty;
                    var arguments = GetString(item, "arguments") ?? string.Empty;
                    AddBlock(itemId, new ToolCallContent(
                        $"{callId}|{itemId}",
                        name,
                        StreamingJsonParser.ParseStreamingJsonObjectRawText(arguments)));
                    var index = _itemIndexes[itemId];
                    _toolCallArguments[index] = arguments;
                    _stream.Push(new ToolCallStartEvent(index, _partial));
                    break;
            }
        }

        private void AddContentPart(JsonElement root)
        {
            var itemId = GetString(root, "item_id");
            if (itemId is null || _itemIndexes.ContainsKey(itemId))
            {
                return;
            }

            if (root.TryGetProperty("part", out var part) &&
                GetString(part, "type") is "output_text" or "refusal")
            {
                AddBlock(itemId, new TextContent(""));
                _stream.Push(new TextStartEvent(_itemIndexes[itemId], _partial));
            }
        }

        private void AppendTextDelta(JsonElement root)
        {
            var delta = GetString(root, "delta") ?? string.Empty;
            var index = FindIndex(root, static block => block is TextContent);
            if (index is null)
            {
                var itemId = GetString(root, "item_id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                AddBlock(itemId, new TextContent(""));
                index = _itemIndexes[itemId];
                _stream.Push(new TextStartEvent(index.Value, _partial));
            }

            UpdateBlock(index.Value, block => block is TextContent text
                ? text with { Text = text.Text + delta }
                : block);
            _stream.Push(new TextDeltaEvent(index.Value, delta, _partial));
        }

        private void AppendThinkingDelta(JsonElement root)
        {
            var delta = GetString(root, "delta") ?? GetString(root, "text") ?? string.Empty;
            var index = FindIndex(root, static block => block is ThinkingContent);
            if (index is null)
            {
                var itemId = GetString(root, "item_id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                AddBlock(itemId, new ThinkingContent(""));
                index = _itemIndexes[itemId];
                _stream.Push(new ThinkingStartEvent(index.Value, _partial));
            }

            UpdateBlock(index.Value, block => block is ThinkingContent thinking
                ? thinking with { Thinking = thinking.Thinking + delta }
                : block);
            _stream.Push(new ThinkingDeltaEvent(index.Value, delta, _partial));
        }

        private void AppendToolCallDelta(JsonElement root)
        {
            var delta = GetString(root, "delta") ?? string.Empty;
            var index = FindIndex(root, static block => block is ToolCallContent);
            if (index is null)
            {
                return;
            }

            var accumulatedArguments = AccumulateToolCallArguments(index.Value, delta);
            UpdateBlock(index.Value, block => block is ToolCallContent toolCall
                ? toolCall with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(accumulatedArguments) }
                : block);
            _stream.Push(new ToolCallDeltaEvent(index.Value, delta, _partial));
        }

        private void CompleteToolCallArguments(JsonElement root)
        {
            var index = FindIndex(root, static block => block is ToolCallContent);
            var arguments = GetString(root, "arguments");
            if (index is not null && arguments is not null)
            {
                _toolCallArguments[index.Value] = arguments;
                UpdateBlock(index.Value, block => block is ToolCallContent toolCall
                    ? toolCall with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(arguments) }
                    : block);
            }
        }

        private void CompleteOutputItem(JsonElement root)
        {
            var item = root.TryGetProperty("item", out var itemElement) ? itemElement : default;
            var itemId = item.ValueKind == JsonValueKind.Object
                ? GetString(item, "id") ?? GetString(root, "item_id")
                : GetString(root, "item_id");
            var index = itemId is not null && _itemIndexes.TryGetValue(itemId, out var mapped)
                ? mapped
                : null as int?;
            if (index is null || index.Value < 0 || index.Value >= _partial.Content.Count)
            {
                return;
            }

            var block = _partial.Content[index.Value];
            if (item.ValueKind == JsonValueKind.Object)
            {
                ApplyDoneItem(index.Value, item, block);
            }

            switch (_partial.Content[index.Value])
            {
                case TextContent:
                    _stream.Push(new TextEndEvent(index.Value, _partial));
                    break;
                case ThinkingContent:
                    _stream.Push(new ThinkingEndEvent(index.Value, _partial));
                    break;
                case ToolCallContent:
                    _stream.Push(new ToolCallEndEvent(index.Value, _partial));
                    break;
            }
        }

        private void CompleteResponse(JsonElement root)
        {
            if (root.TryGetProperty("response", out var response))
            {
                if (GetString(response, "id") is { } id)
                {
                    _partial = _partial with { ResponseId = id };
                }

                if (response.TryGetProperty("usage", out var usage))
                {
                    _partial = _partial with { Usage = ExtractUsage(usage, ResolveServiceTier(response)) };
                }

                _partial = _partial with
                {
                    StopReason = MapStopReason(GetString(response, "status"), _partial),
                    Timestamp = DateTimeOffset.UtcNow
                };
            }
            else
            {
                _partial = _partial with
                {
                    StopReason = MapStopReason(null, _partial),
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            _beforeDone?.Invoke();
            _stream.Push(new DoneEvent(_partial));
            IsComplete = true;
        }

        private void ApplyDoneItem(int index, JsonElement item, ContentBlock block)
        {
            switch (block)
            {
                case TextContent text:
                    var textFromItem = ExtractOutputText(item);
                    if (textFromItem is not null)
                    {
                        UpdateBlock(index, _ => text with
                        {
                            Text = textFromItem,
                            TextSignature = EncodeTextSignature(GetString(item, "id"), GetString(item, "phase"))
                        });
                    }
                    else if (GetString(item, "id") is { } id)
                    {
                        UpdateBlock(index, _ => text with { TextSignature = EncodeTextSignature(id, GetString(item, "phase")) });
                    }
                    break;
                case ThinkingContent thinking:
                    UpdateBlock(index, _ => thinking with { ThinkingSignature = item.GetRawText() });
                    break;
                case ToolCallContent toolCall:
                    var callId = GetString(item, "call_id");
                    var itemId = GetString(item, "id");
                    var name = GetString(item, "name");
                    var args = GetString(item, "arguments");
                    var finalArgs = args ?? (_toolCallArguments.TryGetValue(index, out var accumulated)
                        ? accumulated
                        : toolCall.Arguments);
                    UpdateBlock(index, _ => toolCall with
                    {
                        Id = callId is not null && itemId is not null ? $"{callId}|{itemId}" : toolCall.Id,
                        Name = name ?? toolCall.Name,
                        Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(finalArgs)
                    });
                    break;
            }
        }

        private string AccumulateToolCallArguments(int index, string delta)
        {
            var current = _toolCallArguments.TryGetValue(index, out var existing)
                ? existing
                : string.Empty;
            var accumulated = current + delta;
            _toolCallArguments[index] = accumulated;
            return accumulated;
        }

        private int? FindIndex(JsonElement root, Func<ContentBlock, bool> predicate)
        {
            var itemId = GetString(root, "item_id");
            if (itemId is not null && _itemIndexes.TryGetValue(itemId, out var mapped))
            {
                return mapped;
            }

            for (var i = _partial.Content.Count - 1; i >= 0; i--)
            {
                if (predicate(_partial.Content[i]))
                {
                    return i;
                }
            }

            return null;
        }

        private void AddBlock(string itemId, ContentBlock block)
        {
            if (_itemIndexes.ContainsKey(itemId))
            {
                return;
            }

            var content = _partial.Content.ToList();
            content.Add(block);
            _partial = _partial with { Content = content };
            _itemIndexes[itemId] = content.Count - 1;
        }

        private void UpdateBlock(int index, Func<ContentBlock, ContentBlock> update)
        {
            var content = _partial.Content.ToList();
            content[index] = update(content[index]);
            _partial = _partial with { Content = content };
        }

        private static string? ExtractOutputText(JsonElement item)
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if ((GetString(part, "type") is "output_text" or "refusal") && GetString(part, "text") is { } text)
                {
                    builder.Append(text);
                }
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private string? ResolveServiceTier(JsonElement response)
        {
            var responseServiceTier = GetString(response, "service_tier");
            return _resolveServiceTier is null
                ? responseServiceTier ?? _requestedServiceTier
                : _resolveServiceTier(responseServiceTier, _requestedServiceTier);
        }

        private static Usage ExtractUsage(JsonElement usage, string? serviceTier)
        {
            var input = GetInt(usage, "input_tokens") ?? GetInt(usage, "prompt_tokens") ?? 0;
            var output = GetInt(usage, "output_tokens") ?? GetInt(usage, "completion_tokens") ?? 0;
            int? cacheRead = null;
            if (usage.TryGetProperty("input_tokens_details", out var inputDetails))
            {
                cacheRead = GetInt(inputDetails, "cached_tokens");
            }

            cacheRead ??= GetInt(usage, "prompt_cache_hit_tokens") ?? GetInt(usage, "cache_read_input_tokens");
            var cacheWrite = GetInt(usage, "prompt_cache_miss_tokens") ?? GetInt(usage, "cache_creation_input_tokens");
            return new Usage(input, output, cacheRead, cacheWrite, serviceTier);
        }

        private static StopReason MapStopReason(string? status, AssistantMessage partial)
        {
            if (partial.Content.OfType<ToolCallContent>().Any())
            {
                return StopReason.ToolUse;
            }

            return status switch
            {
                "incomplete" => StopReason.MaxTokens,
                "failed" => StopReason.Error,
                "cancelled" or "aborted" => StopReason.Aborted,
                _ => StopReason.EndTurn
            };
        }

        private static string ExtractError(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? "OpenAI Responses error";
                }

                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? "OpenAI Responses error";
                }
            }

            if (root.TryGetProperty("response", out var response) &&
                response.TryGetProperty("error", out var responseError) &&
                responseError.TryGetProperty("message", out var responseMessage) &&
                responseMessage.ValueKind == JsonValueKind.String)
            {
                return responseMessage.GetString() ?? "OpenAI Responses error";
            }

            return "OpenAI Responses error";
        }
    }
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
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(TimeSpan?))]
internal partial class OpenAiResponsesJsonContext : JsonSerializerContext;
