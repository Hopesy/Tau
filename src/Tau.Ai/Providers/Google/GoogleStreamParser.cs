using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Google;

/// <summary>
/// Parses Gemini streamGenerateContent SSE chunks into StreamEvents.
///
/// Each chunk is a full GenerateContentResponse with one or more candidates,
/// each containing delta parts. We re-construct a unified nested lifecycle:
///   start → (text|toolcall)_(start → delta → end)* → done
/// </summary>
internal sealed class GoogleStreamParser
{
    private AssistantMessage _partial;
    private readonly AssistantMessageStream _stream;
    private int _contentIndex = -1;
    private string? _openBlockType;

    public GoogleStreamParser(AssistantMessage initial, AssistantMessageStream stream)
    {
        _partial = initial;
        _stream = stream;
    }

    public AssistantMessage Partial => _partial;

    public void ParseChunk(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("usageMetadata", out var usage))
            _partial = _partial with { Usage = ExtractUsage(usage) };

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];

            if (candidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                    ProcessPart(part);
            }

            if (candidate.TryGetProperty("finishReason", out var finishReason) &&
                finishReason.ValueKind == JsonValueKind.String)
            {
                CloseOpenBlock();
                _partial = _partial with
                {
                    StopReason = MapStopReason(finishReason.GetString()),
                    Timestamp = DateTimeOffset.UtcNow
                };
            }
        }
    }

    public void EmitStart()
    {
        _stream.Push(new StartEvent(_partial));
    }

    public void EmitDone()
    {
        CloseOpenBlock();
        if (_partial.StopReason is null)
            _partial = _partial with { StopReason = StopReason.EndTurn };
        _partial = _partial with { Timestamp = _partial.Timestamp ?? DateTimeOffset.UtcNow };
        _stream.Push(new DoneEvent(_partial));
    }

    private void ProcessPart(JsonElement part)
    {
        if (part.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
        {
            var text = textProp.GetString() ?? "";
            EnsureOpenBlock("text");

            var current = _partial.Content[_contentIndex];
            if (current is TextContent tc)
            {
                var updated = tc with { Text = tc.Text + text };
                ReplaceContent(_contentIndex, updated);
                _stream.Push(new TextDeltaEvent(_contentIndex, text, _partial));
            }
        }
        else if (part.TryGetProperty("functionCall", out var fc))
        {
            var name = fc.GetProperty("name").GetString() ?? "";
            var args = fc.TryGetProperty("args", out var argsEl)
                ? argsEl.GetRawText()
                : "{}";

            CloseOpenBlock();
            _contentIndex++;
            var id = $"call_{_contentIndex:D4}";
            var toolCall = new ToolCallContent(id, name, args);
            AppendContent(toolCall);
            _openBlockType = "tool_use";

            _stream.Push(new ToolCallStartEvent(_contentIndex, _partial));
            _stream.Push(new ToolCallDeltaEvent(_contentIndex, args, _partial));
        }
        else if (part.TryGetProperty("thought", out var thought) &&
                 thought.ValueKind == JsonValueKind.True &&
                 part.TryGetProperty("text", out var thoughtText))
        {
            var text = thoughtText.GetString() ?? "";
            EnsureOpenBlock("thinking");

            var current = _partial.Content[_contentIndex];
            if (current is ThinkingContent tc)
            {
                var updated = tc with { Thinking = tc.Thinking + text };
                ReplaceContent(_contentIndex, updated);
                _stream.Push(new ThinkingDeltaEvent(_contentIndex, text, _partial));
            }
        }
    }

    private void EnsureOpenBlock(string type)
    {
        if (_openBlockType == type)
            return;

        CloseOpenBlock();
        _contentIndex++;

        ContentBlock newBlock = type switch
        {
            "text" => new TextContent(""),
            "thinking" => new ThinkingContent(""),
            _ => new TextContent("")
        };

        AppendContent(newBlock);
        _openBlockType = type;

        StreamEvent startEvt = newBlock switch
        {
            TextContent => new TextStartEvent(_contentIndex, _partial),
            ThinkingContent => new ThinkingStartEvent(_contentIndex, _partial),
            _ => new TextStartEvent(_contentIndex, _partial)
        };
        _stream.Push(startEvt);
    }

    private void CloseOpenBlock()
    {
        if (_openBlockType is null || _contentIndex < 0 || _contentIndex >= _partial.Content.Count)
            return;

        StreamEvent endEvt = _partial.Content[_contentIndex] switch
        {
            TextContent => new TextEndEvent(_contentIndex, _partial),
            ThinkingContent => new ThinkingEndEvent(_contentIndex, _partial),
            ToolCallContent => new ToolCallEndEvent(_contentIndex, _partial),
            _ => new TextEndEvent(_contentIndex, _partial)
        };
        _stream.Push(endEvt);
        _openBlockType = null;
    }

    private void AppendContent(ContentBlock block)
    {
        var list = _partial.Content.ToList();
        list.Add(block);
        _partial = _partial with { Content = list };
    }

    private void ReplaceContent(int index, ContentBlock block)
    {
        var list = _partial.Content.ToList();
        list[index] = block;
        _partial = _partial with { Content = list };
    }

    private static Usage ExtractUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("promptTokenCount", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 0;
        var output = usage.TryGetProperty("candidatesTokenCount", out var o) && o.ValueKind == JsonValueKind.Number
            ? o.GetInt32() : 0;
        int? cacheRead = usage.TryGetProperty("cachedContentTokenCount", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32() : null;
        return new Usage(input, output, cacheRead);
    }

    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "STOP" => StopReason.EndTurn,
        "MAX_TOKENS" => StopReason.MaxTokens,
        "SAFETY" => StopReason.ContentFilter,
        "RECITATION" => StopReason.ContentFilter,
        "OTHER" => StopReason.EndTurn,
        _ => StopReason.EndTurn
    };
}
