using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Bedrock;

internal sealed class BedrockStreamParser
{
    private static readonly HashSet<string> KnownEventNames = new(StringComparer.Ordinal)
    {
        "messageStart",
        "contentBlockStart",
        "contentBlockDelta",
        "contentBlockStop",
        "messageStop",
        "metadata",
        "internalServerException",
        "modelStreamErrorException",
        "validationException",
        "throttlingException",
        "serviceUnavailableException"
    };

    private AssistantMessage _partial;
    private readonly AssistantMessageStream _stream;
    private readonly Dictionary<int, int> _contentIndexByBedrockIndex = new();
    private readonly Dictionary<int, string> _toolUseInputsByLocalIndex = new();
    private bool _started;

    public BedrockStreamParser(AssistantMessage initial, AssistantMessageStream stream)
    {
        _partial = initial;
        _stream = stream;
    }

    public bool Completed { get; private set; }

    public void ParseMessage(BedrockEventStreamMessage message)
    {
        if (Completed || message.Payload.Length == 0)
        {
            return;
        }

        if (string.Equals(message.MessageType, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.MessageType, "exception", StringComparison.OrdinalIgnoreCase))
        {
            PushError(FormatError(message.EventType, message.Payload));
            return;
        }

        using var doc = JsonDocument.Parse(message.Payload);
        var (eventType, eventRoot) = UnwrapEvent(message.EventType, doc.RootElement);
        if (eventType is null)
        {
            return;
        }

        switch (eventType)
        {
            case "messageStart":
                HandleMessageStart(eventRoot);
                break;
            case "contentBlockStart":
                HandleContentBlockStart(eventRoot);
                break;
            case "contentBlockDelta":
                HandleContentBlockDelta(eventRoot);
                break;
            case "contentBlockStop":
                HandleContentBlockStop(eventRoot);
                break;
            case "messageStop":
                HandleMessageStop(eventRoot);
                break;
            case "metadata":
                HandleMetadata(eventRoot);
                break;
            case "internalServerException":
            case "modelStreamErrorException":
            case "validationException":
            case "throttlingException":
            case "serviceUnavailableException":
                PushError(FormatError(eventType, eventRoot));
                break;
        }
    }

    public void EmitDoneIfNeeded()
    {
        if (Completed)
        {
            return;
        }

        EnsureStarted();
        _partial = _partial with
        {
            StopReason = _partial.StopReason ?? StopReason.EndTurn,
            Timestamp = DateTimeOffset.UtcNow
        };
        _stream.Push(new DoneEvent(_partial));
        Completed = true;
    }

    private void HandleMessageStart(JsonElement root)
    {
        if (root.TryGetProperty("role", out var role) &&
            role.ValueKind == JsonValueKind.String &&
            !string.Equals(role.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            PushError($"Unexpected Bedrock messageStart role: {role.GetString()}");
            return;
        }

        EnsureStarted();
    }

    private void HandleContentBlockStart(JsonElement root)
    {
        EnsureStarted();
        var bedrockIndex = GetInt(root, "contentBlockIndex") ?? 0;
        if (!root.TryGetProperty("start", out var start) || !start.TryGetProperty("toolUse", out var toolUse))
        {
            return;
        }

        var toolUseId = GetString(toolUse, "toolUseId") ?? string.Empty;
        var name = GetString(toolUse, "name") ?? string.Empty;
        var localIndex = AddOrReplaceContent(bedrockIndex, new ToolCallContent(toolUseId, name, "{}"));
        _toolUseInputsByLocalIndex[localIndex] = string.Empty;
        _stream.Push(new ToolCallStartEvent(localIndex, _partial));
    }

    private void HandleContentBlockDelta(JsonElement root)
    {
        EnsureStarted();
        var bedrockIndex = GetInt(root, "contentBlockIndex") ?? 0;
        if (!root.TryGetProperty("delta", out var delta))
        {
            return;
        }

        if (delta.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            var text = textElement.GetString() ?? string.Empty;
            var localIndex = EnsureTextBlock(bedrockIndex);
            if (_partial.Content[localIndex] is TextContent current)
            {
                ReplaceContent(localIndex, current with { Text = current.Text + text });
                _stream.Push(new TextDeltaEvent(localIndex, text, _partial));
            }
            return;
        }

        if (delta.TryGetProperty("toolUse", out var toolUse))
        {
            var input = GetString(toolUse, "input") ?? string.Empty;
            if (_contentIndexByBedrockIndex.TryGetValue(bedrockIndex, out var localIndex) &&
                _partial.Content[localIndex] is ToolCallContent current)
            {
                var rawInput = _toolUseInputsByLocalIndex.TryGetValue(localIndex, out var existing)
                    ? existing + input
                    : input;
                _toolUseInputsByLocalIndex[localIndex] = rawInput;
                ReplaceContent(localIndex, current with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(rawInput) });
                _stream.Push(new ToolCallDeltaEvent(localIndex, input, _partial));
            }
            return;
        }

        if (delta.TryGetProperty("reasoningContent", out var reasoningContent))
        {
            var (thinking, signature) = GetReasoningDelta(reasoningContent);
            var localIndex = EnsureThinkingBlock(bedrockIndex);
            if (_partial.Content[localIndex] is ThinkingContent current)
            {
                var updated = current;
                if (!string.IsNullOrEmpty(thinking))
                {
                    updated = updated with { Thinking = updated.Thinking + thinking };
                }

                if (!string.IsNullOrEmpty(signature))
                {
                    updated = updated with { ThinkingSignature = (updated.ThinkingSignature ?? string.Empty) + signature };
                }

                ReplaceContent(localIndex, updated);
                if (!string.IsNullOrEmpty(thinking))
                {
                    _stream.Push(new ThinkingDeltaEvent(localIndex, thinking, _partial));
                }
            }
        }
    }

    private void HandleContentBlockStop(JsonElement root)
    {
        var bedrockIndex = GetInt(root, "contentBlockIndex") ?? 0;
        if (!_contentIndexByBedrockIndex.TryGetValue(bedrockIndex, out var localIndex) || localIndex >= _partial.Content.Count)
        {
            return;
        }

        switch (_partial.Content[localIndex])
        {
            case TextContent:
                _stream.Push(new TextEndEvent(localIndex, _partial));
                break;
            case ThinkingContent:
                _stream.Push(new ThinkingEndEvent(localIndex, _partial));
                break;
            case ToolCallContent:
                if (_partial.Content[localIndex] is ToolCallContent toolCall)
                {
                    var rawInput = _toolUseInputsByLocalIndex.TryGetValue(localIndex, out var input)
                        ? input
                        : toolCall.Arguments;
                    ReplaceContent(localIndex, toolCall with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(rawInput) });
                }
                _stream.Push(new ToolCallEndEvent(localIndex, _partial));
                break;
        }
    }

    private void HandleMessageStop(JsonElement root)
    {
        _partial = _partial with
        {
            StopReason = MapStopReason(GetString(root, "stopReason"))
        };
    }

    private void HandleMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return;
        }

        _partial = _partial with
        {
            Usage = new Usage(
                GetInt(usage, "inputTokens") ?? 0,
                GetInt(usage, "outputTokens") ?? 0,
                GetInt(usage, "cacheReadInputTokens"),
                GetInt(usage, "cacheWriteInputTokens"))
        };
    }

    private int EnsureTextBlock(int bedrockIndex)
    {
        if (_contentIndexByBedrockIndex.TryGetValue(bedrockIndex, out var localIndex))
        {
            return localIndex;
        }

        localIndex = AddOrReplaceContent(bedrockIndex, new TextContent(string.Empty));
        _stream.Push(new TextStartEvent(localIndex, _partial));
        return localIndex;
    }

    private int EnsureThinkingBlock(int bedrockIndex)
    {
        if (_contentIndexByBedrockIndex.TryGetValue(bedrockIndex, out var localIndex))
        {
            return localIndex;
        }

        localIndex = AddOrReplaceContent(bedrockIndex, new ThinkingContent(string.Empty));
        _stream.Push(new ThinkingStartEvent(localIndex, _partial));
        return localIndex;
    }

    private int AddOrReplaceContent(int bedrockIndex, ContentBlock block)
    {
        var list = _partial.Content.ToList();
        if (_contentIndexByBedrockIndex.TryGetValue(bedrockIndex, out var existingIndex))
        {
            list[existingIndex] = block;
            _partial = _partial with { Content = list };
            return existingIndex;
        }

        var localIndex = list.Count;
        list.Add(block);
        _contentIndexByBedrockIndex[bedrockIndex] = localIndex;
        _partial = _partial with { Content = list };
        return localIndex;
    }

    private void ReplaceContent(int index, ContentBlock block)
    {
        var list = _partial.Content.ToList();
        list[index] = block;
        _partial = _partial with { Content = list };
    }

    private void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        _stream.Push(new StartEvent(_partial));
        _started = true;
    }

    private void PushError(string message)
    {
        _partial = _partial with
        {
            ErrorMessage = message,
            StopReason = StopReason.Error,
            Timestamp = DateTimeOffset.UtcNow
        };
        _stream.Push(new ErrorEvent(message, _partial));
        Completed = true;
    }

    private static (string? EventType, JsonElement EventRoot) UnwrapEvent(string? headerEventType, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (KnownEventNames.Contains(property.Name))
                {
                    return (property.Name, property.Value);
                }
            }
        }

        return (headerEventType, root);
    }

    private static (string? Thinking, string? Signature) GetReasoningDelta(JsonElement reasoningContent)
    {
        var thinking = GetString(reasoningContent, "text");
        var signature = GetString(reasoningContent, "signature");
        if (reasoningContent.TryGetProperty("reasoningText", out var reasoningText))
        {
            thinking ??= GetString(reasoningText, "text");
            signature ??= GetString(reasoningText, "signature");
        }

        return (thinking, signature);
    }

    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => StopReason.EndTurn,
        "max_tokens" or "model_context_window_exceeded" => StopReason.MaxTokens,
        "tool_use" => StopReason.ToolUse,
        "guardrail_intervened" or "content_filtered" => StopReason.ContentFilter,
        _ => StopReason.EndTurn
    };

    private static string FormatError(string? eventType, byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return FormatError(eventType, doc.RootElement);
        }
        catch (JsonException)
        {
            return $"{FormatEventName(eventType)}: {Encoding.UTF8.GetString(payload)}";
        }
    }

    private static string FormatError(string? eventType, JsonElement root)
    {
        var message = GetString(root, "message") ?? root.GetRawText();
        return $"{FormatEventName(eventType)}: {message}";
    }

    private static string FormatEventName(string? eventType) => eventType switch
    {
        "internalServerException" => "Internal server error",
        "modelStreamErrorException" => "Model stream error",
        "validationException" => "Validation error",
        "throttlingException" => "Throttling error",
        "serviceUnavailableException" => "Service unavailable",
        null or "" => "Amazon Bedrock stream error",
        _ => eventType
    };

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
