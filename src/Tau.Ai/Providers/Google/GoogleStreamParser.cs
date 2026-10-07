using System.Text.Json;
using Tau.Ai.Registry;
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
    private readonly Model? _model;
    private static long _toolCallCounter;
    private int _contentIndex = -1;
    private string? _openBlockType;
    private bool _terminalErrorEmitted;

    /// <summary>【Google】【流状态】初始化完整用量与模型身份，供正常和错误终态共用。</summary>
    /// <param name="initial">初始助手消息。</param><param name="stream">事件流。</param><param name="model">可选计费模型。</param>
    public GoogleStreamParser(AssistantMessage initial, AssistantMessageStream stream, Model? model = null)
    {
        _partial = initial with { Usage = new Usage(0, 0, 0, 0, Cost: default(UsageCost)) { TotalTokens = 0 }, Timestamp = initial.Timestamp ?? DateTimeOffset.UtcNow };
        _stream = stream;
        _model = model;
    }

    public AssistantMessage Partial => _partial;

    /// <summary>【Google】【请求状态】在准备请求前建立状态，保证认证和报文错误也保留身份。</summary>
    /// <param name="model">目标模型。</param><param name="api">实际传输协议。</param><param name="stream">输出事件流。</param>
    /// <returns>本次请求专用的解析状态。</returns>
    internal static GoogleStreamParser Create(Model model, string api, AssistantMessageStream stream) =>
        new(new AssistantMessage { Api = api, Provider = model.Provider, Model = model.Id, Content = [] }, stream, model);

    /// <summary>【Google】【分片解析】累积内容与用量，结束标记后仍允许接收尾部用量。</summary>
    /// <param name="json">原生 JSON 分片。</param><returns>是否已经因为损坏 JSON 结束流。</returns>
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
                !string.IsNullOrEmpty(responseId.GetString()))
            {
                _partial = _partial with { ResponseId = responseId.GetString() };
            }

            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];

                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                        ProcessPart(part);
                }

                if (candidate.TryGetProperty("finishReason", out var finishReason) &&
                    finishReason.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(finishReason.GetString()))
                {
                    var rawReason = finishReason.GetString();
                    var stopReason = MapStopReason(rawReason);
                    if (stopReason == StopReason.EndTurn && HasToolCall()) stopReason = StopReason.ToolUse;
                    _partial = _partial with
                    {
                        StopReason = stopReason,
                        RawStopReason = rawReason,
                        EndTurn = stopReason == StopReason.EndTurn
                    };
                }
            }
            if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
                _partial = _partial with { Usage = ExtractUsage(usage) };

            return false;
        }
    }

    /// <summary>【Google】【流开始】发出包含模型身份和零用量的开始快照。</summary>
    public void EmitStart()
    {
        _stream.Push(new StartEvent(_partial));
    }

    /// <summary>【Google】【流收尾】缺少或失败的结束原因返回错误，成功时发出完整终态。</summary>
    public void EmitDone()
    {
        if (_terminalErrorEmitted)
            return;

        CloseOpenBlock();
        if (_partial.StopReason is null)
        {
            PushError(_partial.Api == "google-vertex" ? "Google Vertex stream ended without a finish reason" : "Google stream ended without a finish reason");
            return;
        }
        if (_partial.StopReason == StopReason.Error)
        {
            PushError($"Provider stopped with: {_partial.RawStopReason}");
            return;
        }
        _partial = _partial with { Timestamp = _partial.Timestamp ?? DateTimeOffset.UtcNow };
        _stream.Push(new DoneEvent(_partial));
    }

    /// <summary>【Google】【内容增量】保持思考和正文边界，完整函数调用立即结束其生命周期。</summary>
    /// <param name="part">协议内容块。</param>
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
            var args = fc.TryGetProperty("args", out var argsEl) && argsEl.ValueKind != JsonValueKind.Null
                ? argsEl.GetRawText()
                : "{}";

            CloseOpenBlock();
            _contentIndex++;
            var providedId = GetString(fc, "id");
            var id = string.IsNullOrEmpty(providedId) || HasToolCallId(providedId!)
                ? $"{name}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _toolCallCounter)}"
                : providedId!;
            var toolCall = new ToolCallContent(id, name, args)
            {
                ThoughtSignature = GetString(part, "thoughtSignature") is { Length: > 0 } signature ? signature : null
            };
            AppendContent(toolCall);
            _openBlockType = "tool_use";

            _stream.Push(new ToolCallStartEvent(_contentIndex, _partial, id, name));
            _stream.Push(new ToolCallDeltaEvent(_contentIndex, args, _partial));
            CloseOpenBlock();
        }
    }

    /// <summary>【Google】【内容切换】同类型连续增量复用当前块，其余类型先关闭旧块。</summary>
    /// <param name="type">正文或思考类型。</param>
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

    /// <summary>【Google】【块终态】发出包含完整文字、签名或调用的结束事件。</summary>
    private void CloseOpenBlock()
    {
        if (_openBlockType is null || _contentIndex < 0 || _contentIndex >= _partial.Content.Count)
            return;

        StreamEvent endEvt = _partial.Content[_contentIndex] switch
        {
            TextContent text => new TextEndEvent(_contentIndex, _partial, text.Text, text.TextSignature),
            ThinkingContent thinking => new ThinkingEndEvent(_contentIndex, _partial, thinking.Thinking, thinking.ThinkingSignature),
            ToolCallContent call => new ToolCallEndEvent(_contentIndex, _partial, call),
            _ => new TextEndEvent(_contentIndex, _partial)
        };
        _stream.Push(endEvt);
        _openBlockType = null;
    }

    /// <summary>【Google】【内容快照】追加内容时复制集合，保持已发布快照稳定。</summary>
    /// <param name="block">新增内容。</param>
    private void AppendContent(ContentBlock block)
    {
        var list = _partial.Content.ToList();
        list.Add(block);
        _partial = _partial with { Content = list };
    }

    /// <summary>【Google】【内容快照】替换指定内容，保留旧事件快照。</summary>
    /// <param name="index">块索引。</param><param name="block">更新内容。</param>
    private void ReplaceContent(int index, ContentBlock block)
    {
        var list = _partial.Content.ToList();
        list[index] = block;
        _partial = _partial with { Content = list };
    }

    /// <summary>【Google】【用量计算】扣除缓存输入、合并思考输出，并保留服务端总量和模型费用。</summary>
    /// <param name="usage">原生用量对象。</param><returns>统一用量。</returns>
    private Usage ExtractUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("promptTokenCount", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 0;
        var cacheRead = usage.TryGetProperty("cachedContentTokenCount", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32() : 0;
        var thoughts = usage.TryGetProperty("thoughtsTokenCount", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : 0;
        var output = usage.TryGetProperty("candidatesTokenCount", out var o) && o.ValueKind == JsonValueKind.Number
            ? o.GetInt32() : 0;
        var normalizedInput = input - cacheRead;
        var normalizedOutput = output + thoughts;
        var value = new Usage(normalizedInput, normalizedOutput, cacheRead, 0, Cost: default(UsageCost))
        {
            ReasoningTokens = thoughts,
            TotalTokens = usage.TryGetProperty("totalTokenCount", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : 0
        };
        return _model is null ? value : value with { Cost = ModelCatalog.CalculateCost(_model, value) };
    }

    /// <summary>【Google】【结束原因】区分正常结束、长度上限及提供方拒绝。</summary>
    /// <param name="reason">原生结束原因。</param><returns>统一结束原因。</returns>
    private static StopReason MapStopReason(string? reason) => reason switch
    {
        "STOP" => StopReason.EndTurn,
        "MAX_TOKENS" => StopReason.MaxTokens,
        _ => StopReason.Error
    };

    /// <summary>【Google】【失败快照】保留已接收内容和用量，确保只发布一个失败终态。</summary>
    /// <param name="message">错误说明。</param><param name="reason">错误或取消原因。</param>
    public void PushError(string message, StopReason reason = StopReason.Error)
    {
        if (_terminalErrorEmitted) return;
        CloseOpenBlock();
        var error = _partial with
        {
            StopReason = reason,
            ErrorMessage = message,
            EndTurn = false
        };
        _partial = error;
        _stream.Push(new ErrorEvent(message, error, error));
        _terminalErrorEmitted = true;
    }

    /// <summary>【Google】【工具终态】检查本轮是否已有函数调用。</summary>
    /// <returns>是否包含工具调用。</returns>
    private bool HasToolCall() => _partial.Content.Any(static block => block is ToolCallContent);

    /// <summary>【Google】【调用去重】检查提供方返回的 ID 是否在本轮重复。</summary>
    /// <param name="id">调用 ID。</param><returns>是否已经出现。</returns>
    private bool HasToolCallId(string id) =>
        _partial.Content.OfType<ToolCallContent>().Any(toolCall => toolCall.Id.Equals(id, StringComparison.Ordinal));

    /// <summary>【Google】【思考标记】签名不决定内容类型，只有 thought=true 表示思考。</summary>
    /// <param name="part">内容块。</param><returns>是否为思考。</returns>
    private static bool IsThinkingPart(JsonElement part) =>
        part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;

    /// <summary>【Google】【协议字段】读取可选字符串字段。</summary>
    /// <param name="element">JSON 对象。</param><param name="propertyName">字段名。</param><returns>字符串或 null。</returns>
    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>【Google】【签名更新】当前块采用最近非空签名，省略或空增量不清除原签名。</summary>
    /// <param name="current">原签名。</param><param name="next">增量签名。</param><returns>本块最新签名。</returns>
    private static string? RetainSignature(string? current, string? next) =>
        string.IsNullOrEmpty(next) ? current : next;
}
