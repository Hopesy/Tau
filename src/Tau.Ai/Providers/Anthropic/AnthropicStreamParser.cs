using System.Text;
using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Anthropic;

/// <summary>
/// Parses Anthropic Messages API SSE events into StreamEvents.
///
/// Anthropic event flow:
///   message_start → (content_block_start → content_block_delta* → content_block_stop)*
///     → message_delta → message_stop
///   (ping and error events interleave)
/// </summary>
internal sealed class AnthropicStreamParser
{
    private AssistantMessage _partial;
    private readonly AssistantMessageStream _stream;
    private readonly Dictionary<int, ToolInputAccumulator> _toolInputs = new();
    private readonly Dictionary<int, int> _contentIndexes = new();

    public AnthropicStreamParser(AssistantMessage initial, AssistantMessageStream stream)
    {
        _partial = initial;
        _stream = stream;
    }

    public AssistantMessage Partial => _partial;

    public bool ParseEvent(string? eventType, string data)
    {
        if (string.IsNullOrEmpty(data))
            return false;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            PushError($"Malformed Anthropic SSE JSON: {ex.Message}");
            return true;
        }

        using var doc = parsed;
        var root = doc.RootElement;

        var type = eventType ?? (root.TryGetProperty("type", out var t) ? t.GetString() : null);
        if (string.IsNullOrEmpty(type))
            return false;

        switch (type)
        {
            case "message_start":
                HandleMessageStart(root);
                return false;
            case "content_block_start":
                HandleContentBlockStart(root);
                return false;
            case "content_block_delta":
                HandleContentBlockDelta(root);
                return false;
            case "content_block_stop":
                HandleContentBlockStop(root);
                return false;
            case "message_delta":
                return HandleMessageDelta(root);
            case "message_stop":
                HandleMessageStop();
                return true;
            case "error":
                return HandleError(root);
            case "ping":
            default:
                return false;
        }
    }

    private void HandleMessageStart(JsonElement root)
    {
        if (root.TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                _partial = _partial with { ResponseId = id.GetString() };

            if (msg.TryGetProperty("usage", out var usage))
                _partial = _partial with { Usage = ExtractUsage(usage) };
        }
        _stream.Push(new StartEvent(_partial));
    }

    private void HandleContentBlockStart(JsonElement root)
    {
        var providerIndex = root.GetProperty("index").GetInt32();
        var block = root.GetProperty("content_block");
        var blockType = block.GetProperty("type").GetString();

        ContentBlock newBlock = blockType switch
        {
            "text" => new TextContent(""),
            "thinking" => new ThinkingContent(""),
            "redacted_thinking" => new ThinkingContent("[Reasoning redacted]")
            {
                Redacted = true,
                ThinkingSignature = block.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                    ? data.GetString()
                    : null
            },
            "tool_use" => new ToolCallContent(
                block.GetProperty("id").GetString()!,
                block.GetProperty("name").GetString()!,
                StreamingJsonParser.ParseStreamingJsonObjectRawText(
                    block.TryGetProperty("input", out var input) ? input.GetRawText() : null)),
            _ => new TextContent("")
        };

        var content = _partial.Content.ToList();
        var contentIndex = content.Count;
        content.Add(newBlock);
        _partial = _partial with { Content = content };
        _contentIndexes[providerIndex] = contentIndex;

        StreamEvent evt = newBlock switch
        {
            TextContent => new TextStartEvent(contentIndex, _partial),
            ThinkingContent => new ThinkingStartEvent(contentIndex, _partial),
            ToolCallContent => new ToolCallStartEvent(contentIndex, _partial),
            _ => new TextStartEvent(contentIndex, _partial)
        };
        _stream.Push(evt);

        if (newBlock is ToolCallContent)
            _toolInputs[providerIndex] = new ToolInputAccumulator(new StringBuilder());
    }

    private void HandleContentBlockDelta(JsonElement root)
    {
        var providerIndex = root.GetProperty("index").GetInt32();
        if (!_contentIndexes.TryGetValue(providerIndex, out var contentIndex))
            return;

        var delta = root.GetProperty("delta");
        var deltaType = delta.GetProperty("type").GetString();

        if (contentIndex >= _partial.Content.Count)
            return;

        var current = _partial.Content[contentIndex];

        switch (deltaType)
        {
            case "text_delta":
                {
                    var text = delta.GetProperty("text").GetString() ?? "";
                    if (current is TextContent tc)
                    {
                        var updated = tc with { Text = tc.Text + text };
                        ReplaceContent(contentIndex, updated);
                        _stream.Push(new TextDeltaEvent(contentIndex, text, _partial));
                    }
                    break;
                }
            case "thinking_delta":
                {
                    var text = delta.GetProperty("thinking").GetString() ?? "";
                    if (current is ThinkingContent tc)
                    {
                        var updated = tc with { Thinking = tc.Thinking + text };
                        ReplaceContent(contentIndex, updated);
                        _stream.Push(new ThinkingDeltaEvent(contentIndex, text, _partial));
                    }
                    break;
                }
            case "signature_delta":
                {
                    var sig = delta.GetProperty("signature").GetString();
                    if (current is ThinkingContent tc)
                        ReplaceContent(contentIndex, tc with { ThinkingSignature = string.Concat(tc.ThinkingSignature, sig) });
                    else if (current is TextContent txt)
                        ReplaceContent(contentIndex, txt with { TextSignature = string.Concat(txt.TextSignature, sig) });
                    break;
                }
            case "input_json_delta":
                {
                    var partialJson = delta.GetProperty("partial_json").GetString() ?? "";
                    if (_toolInputs.TryGetValue(providerIndex, out var acc) && current is ToolCallContent tcc)
                    {
                        acc.Builder.Append(partialJson);
                        var updated = tcc with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(acc.Builder.ToString()) };
                        ReplaceContent(contentIndex, updated);
                        _stream.Push(new ToolCallDeltaEvent(contentIndex, partialJson, _partial));
                    }
                    break;
                }
        }
    }

    private void HandleContentBlockStop(JsonElement root)
    {
        var providerIndex = root.GetProperty("index").GetInt32();
        if (!_contentIndexes.TryGetValue(providerIndex, out var contentIndex))
            return;

        if (contentIndex >= _partial.Content.Count)
            return;

        var block = _partial.Content[contentIndex];
        StreamEvent evt = block switch
        {
            TextContent => new TextEndEvent(contentIndex, _partial),
            ThinkingContent => new ThinkingEndEvent(contentIndex, _partial),
            ToolCallContent toolCall => FinalizeToolCall(providerIndex, contentIndex, toolCall),
            _ => new TextEndEvent(contentIndex, _partial)
        };
        _stream.Push(evt);
    }

    private bool HandleMessageDelta(JsonElement root)
    {
        if (root.TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
            {
                var (stopReason, errorMessage) = MapStopReason(sr.GetString());
                _partial = _partial with { StopReason = stopReason };
                if (stopReason == StopReason.Error)
                {
                    PushError(errorMessage ?? "Provider stop_reason mapped to error");
                    return true;
                }
            }
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            var merged = ExtractUsage(usage);
            var existing = _partial.Usage ?? new Usage(0, 0);
            _partial = _partial with
            {
                Usage = new Usage(
                    merged.InputTokens == 0 ? existing.InputTokens : merged.InputTokens,
                    merged.OutputTokens == 0 ? existing.OutputTokens : merged.OutputTokens,
                    merged.CacheReadTokens ?? existing.CacheReadTokens,
                    merged.CacheWriteTokens ?? existing.CacheWriteTokens)
            };
        }

        return false;
    }

    private void HandleMessageStop()
    {
        _partial = _partial with { Timestamp = DateTimeOffset.UtcNow };
        _stream.Push(new DoneEvent(_partial));
    }

    private bool HandleError(JsonElement root)
    {
        var message = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
            ? m.GetString() ?? "Anthropic stream error"
            : "Anthropic stream error";
        PushError(message);
        return true;
    }

    private void ReplaceContent(int index, ContentBlock block)
    {
        var list = _partial.Content.ToList();
        list[index] = block;
        _partial = _partial with { Content = list };
    }

    private ToolCallEndEvent FinalizeToolCall(int providerIndex, int contentIndex, ToolCallContent toolCall)
    {
        var rawArguments = _toolInputs.TryGetValue(providerIndex, out var acc) && acc.Builder.Length > 0
            ? acc.Builder.ToString()
            : toolCall.Arguments;
        ReplaceContent(contentIndex, toolCall with { Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(rawArguments) });
        return new ToolCallEndEvent(contentIndex, _partial);
    }

    private static Usage ExtractUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("input_tokens", out var ip) && ip.ValueKind == JsonValueKind.Number
            ? ip.GetInt32() : 0;
        var output = usage.TryGetProperty("output_tokens", out var op) && op.ValueKind == JsonValueKind.Number
            ? op.GetInt32() : 0;
        int? cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.ValueKind == JsonValueKind.Number
            ? cr.GetInt32() : null;
        int? cacheWrite = usage.TryGetProperty("cache_creation_input_tokens", out var cw) && cw.ValueKind == JsonValueKind.Number
            ? cw.GetInt32() : null;
        return new Usage(input, output, cacheRead, cacheWrite);
    }

    private void PushError(string message)
    {
        var error = _partial with
        {
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Timestamp = DateTimeOffset.UtcNow
        };
        _partial = error;
        _stream.Push(new ErrorEvent(message, error, error));
    }

    private static (StopReason StopReason, string? ErrorMessage) MapStopReason(string? reason) => reason switch
    {
        "end_turn" => (StopReason.EndTurn, null),
        "max_tokens" => (StopReason.MaxTokens, null),
        "tool_use" => (StopReason.ToolUse, null),
        "pause_turn" => (StopReason.EndTurn, null),
        "stop_sequence" => (StopReason.EndTurn, null),
        "refusal" => (StopReason.Error, "Provider stop_reason: refusal"),
        "sensitive" => (StopReason.Error, "Provider stop_reason: sensitive"),
        _ => (StopReason.Error, $"Unhandled Anthropic stop_reason: {reason}")
    };

    private sealed record ToolInputAccumulator(StringBuilder Builder);
}
