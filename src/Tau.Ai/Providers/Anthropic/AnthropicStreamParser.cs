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

    public AnthropicStreamParser(AssistantMessage initial, AssistantMessageStream stream)
    {
        _partial = initial;
        _stream = stream;
    }

    public AssistantMessage Partial => _partial;

    public void ParseEvent(string? eventType, string data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        var type = eventType ?? (root.TryGetProperty("type", out var t) ? t.GetString() : null);
        if (string.IsNullOrEmpty(type))
            return;

        switch (type)
        {
            case "message_start":
                HandleMessageStart(root);
                break;
            case "content_block_start":
                HandleContentBlockStart(root);
                break;
            case "content_block_delta":
                HandleContentBlockDelta(root);
                break;
            case "content_block_stop":
                HandleContentBlockStop(root);
                break;
            case "message_delta":
                HandleMessageDelta(root);
                break;
            case "message_stop":
                HandleMessageStop();
                break;
            case "error":
                HandleError(root);
                break;
            case "ping":
            default:
                break;
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
        var index = root.GetProperty("index").GetInt32();
        var block = root.GetProperty("content_block");
        var blockType = block.GetProperty("type").GetString();

        ContentBlock newBlock = blockType switch
        {
            "text" => new TextContent(""),
            "thinking" => new ThinkingContent(""),
            "redacted_thinking" => new ThinkingContent("") { Redacted = true },
            "tool_use" => new ToolCallContent(
                block.GetProperty("id").GetString()!,
                block.GetProperty("name").GetString()!,
                ""),
            _ => new TextContent("")
        };

        EnsureContentSlot(index);
        var content = _partial.Content.ToList();
        content[index] = newBlock;
        _partial = _partial with { Content = content };

        StreamEvent evt = newBlock switch
        {
            TextContent => new TextStartEvent(index, _partial),
            ThinkingContent => new ThinkingStartEvent(index, _partial),
            ToolCallContent => new ToolCallStartEvent(index, _partial),
            _ => new TextStartEvent(index, _partial)
        };
        _stream.Push(evt);

        if (newBlock is ToolCallContent)
            _toolInputs[index] = new ToolInputAccumulator(new StringBuilder());
    }

    private void HandleContentBlockDelta(JsonElement root)
    {
        var index = root.GetProperty("index").GetInt32();
        var delta = root.GetProperty("delta");
        var deltaType = delta.GetProperty("type").GetString();

        if (index >= _partial.Content.Count)
            return;

        var current = _partial.Content[index];

        switch (deltaType)
        {
            case "text_delta":
                {
                    var text = delta.GetProperty("text").GetString() ?? "";
                    if (current is TextContent tc)
                    {
                        var updated = tc with { Text = tc.Text + text };
                        ReplaceContent(index, updated);
                        _stream.Push(new TextDeltaEvent(index, text, _partial));
                    }
                    break;
                }
            case "thinking_delta":
                {
                    var text = delta.GetProperty("thinking").GetString() ?? "";
                    if (current is ThinkingContent tc)
                    {
                        var updated = tc with { Thinking = tc.Thinking + text };
                        ReplaceContent(index, updated);
                        _stream.Push(new ThinkingDeltaEvent(index, text, _partial));
                    }
                    break;
                }
            case "signature_delta":
                {
                    var sig = delta.GetProperty("signature").GetString();
                    if (current is ThinkingContent tc)
                        ReplaceContent(index, tc with { ThinkingSignature = sig });
                    else if (current is TextContent txt)
                        ReplaceContent(index, txt with { TextSignature = sig });
                    break;
                }
            case "input_json_delta":
                {
                    var partialJson = delta.GetProperty("partial_json").GetString() ?? "";
                    if (_toolInputs.TryGetValue(index, out var acc) && current is ToolCallContent tcc)
                    {
                        acc.Builder.Append(partialJson);
                        var updated = tcc with { Arguments = acc.Builder.ToString() };
                        ReplaceContent(index, updated);
                        _stream.Push(new ToolCallDeltaEvent(index, partialJson, _partial));
                    }
                    break;
                }
        }
    }

    private void HandleContentBlockStop(JsonElement root)
    {
        var index = root.GetProperty("index").GetInt32();
        if (index >= _partial.Content.Count)
            return;

        var block = _partial.Content[index];
        StreamEvent evt = block switch
        {
            TextContent => new TextEndEvent(index, _partial),
            ThinkingContent => new ThinkingEndEvent(index, _partial),
            ToolCallContent => new ToolCallEndEvent(index, _partial),
            _ => new TextEndEvent(index, _partial)
        };
        _stream.Push(evt);
    }

    private void HandleMessageDelta(JsonElement root)
    {
        if (root.TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                _partial = _partial with { StopReason = MapStopReason(sr.GetString()) };
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
    }

    private void HandleMessageStop()
    {
        _partial = _partial with { Timestamp = DateTimeOffset.UtcNow };
        _stream.Push(new DoneEvent(_partial));
    }

    private void HandleError(JsonElement root)
    {
        var message = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
            ? m.GetString() ?? "Anthropic stream error"
            : "Anthropic stream error";
        _stream.Push(new ErrorEvent(message, _partial));
    }

    private void ReplaceContent(int index, ContentBlock block)
    {
        var list = _partial.Content.ToList();
        list[index] = block;
        _partial = _partial with { Content = list };
    }

    private void EnsureContentSlot(int index)
    {
        var list = _partial.Content.ToList();
        while (list.Count <= index)
            list.Add(new TextContent(""));
        _partial = _partial with { Content = list };
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

    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" => StopReason.EndTurn,
        "max_tokens" => StopReason.MaxTokens,
        "tool_use" => StopReason.ToolUse,
        "stop_sequence" => StopReason.EndTurn,
        _ => StopReason.EndTurn
    };

    private sealed record ToolInputAccumulator(StringBuilder Builder);
}
