using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Google;

/// <summary>
/// Parses Gemini streamGenerateContent SSE chunks into StreamEvents.
///
/// Each chunk is a full GenerateContentResponse with one or more candidates,
/// each containing delta parts. We re-construct a unified nested lifecycle:
///   start → (text|thinking|toolcall)_(start → delta → end)* → done|error
/// </summary>
internal sealed class GoogleStreamParser
{
    private AssistantMessage _partial;
    private readonly AssistantMessageStream _stream;
    private int _contentIndex = -1;
    private string? _openBlockType;
    private bool _terminalErrorEmitted;

    public GoogleStreamParser(AssistantMessage initial, AssistantMessageStream stream)
    {
        _partial = initial;
        _stream = stream;
    }

    public AssistantMessage Partial => _partial;

    public bool ParseChunk(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            PushError($"Malformed Google stream JSON: {ex.Message}");
            return true;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (_partial.ResponseId is null &&
                root.TryGetProperty("responseId", out var responseId) &&
                responseId.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(responseId.GetString()))
            {
                _partial = _partial with { ResponseId = responseId.GetString() };
            }

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
                    var rawReason = finishReason.GetString();
                    var stopReason = HasToolCall() ? StopReason.ToolUse : MapStopReason(rawReason);
                    var errorMessage = stopReason == StopReason.Error
                        ? $"Provider finishReason: {rawReason}"
                        : null;
                    _partial = _partial with
                    {
                        StopReason = stopReason,
                        ErrorMessage = errorMessage,
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    if (stopReason == StopReason.Error)
                    {
                        _stream.Push(new ErrorEvent(errorMessage!, _partial, _partial));
                        _terminalErrorEmitted = true;
                        return true;
                    }
                }
            }

            return false;
        }
    }

    public void EmitStart()
    {
        _stream.Push(new StartEvent(_partial));
    }

    public void EmitDone()
    {
        if (_terminalErrorEmitted)
            return;

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
            var isThinking = IsThinkingPart(part);
            EnsureOpenBlock(isThinking ? "thinking" : "text");

            var current = _partial.Content[_contentIndex];
            var signature = GetString(part, "thoughtSignature");
            if (current is ThinkingContent thinking)
            {
                var updated = thinking with
                {
                    Thinking = thinking.Thinking + text,
                    ThinkingSignature = RetainSignature(thinking.ThinkingSignature, signature)
                };
                ReplaceContent(_contentIndex, updated);
                _stream.Push(new ThinkingDeltaEvent(_contentIndex, text, _partial));
            }
            else if (current is TextContent tc)
            {
                var updated = tc with
                {
                    Text = tc.Text + text,
                    TextSignature = RetainSignature(tc.TextSignature, signature)
                };
                ReplaceContent(_contentIndex, updated);
                _stream.Push(new TextDeltaEvent(_contentIndex, text, _partial));
            }
        }
        if (part.TryGetProperty("functionCall", out var fc))
        {
            var name = GetString(fc, "name") ?? "";
            var args = fc.TryGetProperty("args", out var argsEl)
                ? argsEl.GetRawText()
                : "{}";

            CloseOpenBlock();
            _contentIndex++;
            var providedId = GetString(fc, "id");
            var id = string.IsNullOrWhiteSpace(providedId) || HasToolCallId(providedId!)
                ? $"call_{_contentIndex:D4}"
                : providedId!;
            var toolCall = new ToolCallContent(id, name, args)
            {
                ThoughtSignature = GetString(part, "thoughtSignature")
            };
            AppendContent(toolCall);
            _openBlockType = "tool_use";

            _stream.Push(new ToolCallStartEvent(_contentIndex, _partial));
            _stream.Push(new ToolCallDeltaEvent(_contentIndex, args, _partial));
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
        var cacheRead = usage.TryGetProperty("cachedContentTokenCount", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32() : (int?)null;
        var thoughts = usage.TryGetProperty("thoughtsTokenCount", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : 0;
        var output = usage.TryGetProperty("candidatesTokenCount", out var o) && o.ValueKind == JsonValueKind.Number
            ? o.GetInt32() : 0;
        return new Usage(input - (cacheRead ?? 0), output + thoughts, cacheRead);
    }

    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "STOP" => StopReason.EndTurn,
        "MAX_TOKENS" => StopReason.MaxTokens,
        _ => StopReason.Error
    };

    private void PushError(string message)
    {
        CloseOpenBlock();
        var error = _partial with
        {
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Timestamp = DateTimeOffset.UtcNow
        };
        _partial = error;
        _stream.Push(new ErrorEvent(message, error, error));
        _terminalErrorEmitted = true;
    }

    private bool HasToolCall() => _partial.Content.Any(static block => block is ToolCallContent);

    private bool HasToolCallId(string id) =>
        _partial.Content.OfType<ToolCallContent>().Any(toolCall => toolCall.Id.Equals(id, StringComparison.Ordinal));

    private static bool IsThinkingPart(JsonElement part) =>
        part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? RetainSignature(string? current, string? next) =>
        string.IsNullOrEmpty(current) ? next : current;
}
