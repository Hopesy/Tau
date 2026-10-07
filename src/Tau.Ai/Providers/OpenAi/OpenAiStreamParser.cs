using System.Text.Json;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>
/// Parses OpenAI streaming chat completion chunks into StreamEvents.
/// </summary>
internal static class OpenAiStreamParser
{
    /// <summary>【AI】【Chat Completions】读取完整 SSE，保留尾部用量并在异常时返回已接收内容。</summary>
    /// <param name="input">HTTP 响应流。</param>
    /// <param name="stream">输出事件流。</param>
    /// <param name="model">请求模型。</param>
    /// <param name="api">实际协议名称。</param>
    /// <param name="grammarInputs">语法工具到输入属性的映射。</param>
    /// <param name="supportsFinishReason">是否要求供应商发送结束原因。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="onProviderStreamEvent">归一化之前的原始 JSON 事件观察器。</param>
    /// <returns>读取及完成消息的异步任务。</returns>
    public static async Task ProcessStreamAsync(Stream input, AssistantMessageStream stream, Model model, string api,
        IReadOnlyDictionary<string, string> grammarInputs, bool supportsFinishReason, CancellationToken cancellationToken,
        Func<JsonElement, Model, ValueTask>? onProviderStreamEvent = null)
    {
        var partial = new AssistantMessage { Api = api, Provider = model.Provider, Model = model.Id, Content = [] };
        var accumulators = new Dictionary<int, ToolCallAccumulator>();
        var contentIndex = 0;
        stream.Push(new StartEvent(partial));
        try
        {
            // 1. 【AI】【语法工具】finish_reason 后仍可有独立 usage 块，统一在传输结束时完成
            await foreach (var sse in SseParser.ParseAsync(input, cancellationToken))
            {
                if (sse.Data == "[DONE]") break;
                if (onProviderStreamEvent is not null)
                {
                    using var document = JsonDocument.Parse(sse.Data);
                    await onProviderStreamEvent(document.RootElement.Clone(), model).ConfigureAwait(false);
                }
                ParseChunk(sse.Data, stream, ref partial, ref accumulators, ref contentIndex, grammarInputs, deferCompletion: true);
            }
            Complete(stream, ref partial, accumulators, supportsFinishReason);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            partial = partial with { StopReason = StopReason.Error, ErrorMessage = error.Message, EndTurn = false, Timestamp = DateTimeOffset.UtcNow };
            stream.Push(new ErrorEvent(error.Message, partial, partial));
            throw;
        }
    }

    /// <summary>【AI】【Chat Completions】解析单块响应，按流索引或调用标识累积工具参数。</summary>
    /// <param name="json">供应商 JSON 块。</param>
    /// <param name="stream">事件输出流。</param>
    /// <param name="partial">当前消息，解析后替换为新快照。</param>
    /// <param name="toolCallAccumulators">按内容位置存储的工具累计状态。</param>
    /// <param name="contentIndex">当前内容位置。</param>
    /// <param name="grammarInputs">自定义工具的输入属性映射。</param>
    /// <param name="deferCompletion">是否等尾部用量读取完成后再发布终态。</param>
    /// <returns>是否收到结束原因。</returns>
    public static bool ParseChunk(
        string json,
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        ref Dictionary<int, ToolCallAccumulator> toolCallAccumulators,
        ref int contentIndex,
        IReadOnlyDictionary<string, string>? grammarInputs = null,
        bool deferCompletion = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException(TryGetString(error, "message") ?? error.GetRawText());
        ApplyChunkMetadata(root, null, ref partial);

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return false;
        }

        var choice = choices[0];
        ApplyChunkMetadata(root, choice, ref partial);
        if (partial.StopReason is not null) return true;
        var hasDelta = choice.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.Object;
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null
            ? fr.GetString() : null;
        if (root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
        {
            var responseModel = modelElement.GetString();
            if (!string.IsNullOrWhiteSpace(responseModel) && !responseModel.Equals(partial.Model, StringComparison.Ordinal))
            {
                partial = partial with { ResponseModel = responseModel };
            }
        }

        // 1. 【AI】【增量顺序】同一分片的正文先于思考，空字符串不创建内容块
        if (hasDelta && TryGetString(delta, "content") is { Length: > 0 } text)
            AppendTextDelta(stream, ref partial, text, ref contentIndex);
        if (hasDelta)
        {
            // 2. 【AI】【思考去重】多个兼容字段同时出现时，只采用第一个非空字段并保存其回放名称
            foreach (var field in new[] { "reasoning_content", "reasoning", "reasoning_text" })
            {
                if (TryGetString(delta, field) is not { Length: > 0 } reasoning) continue;
                var signature = partial.Provider == "opencode-go" && field == "reasoning" ? "reasoning_content" : field;
                AppendThinkingDelta(stream, ref partial, reasoning, signature, ref contentIndex);
                break;
            }
        }
        // 3. 【AI】【工具与签名】工具增量先更新对应调用，结构化明细随后附到全局思考块
        if (hasDelta && delta.TryGetProperty("tool_calls", out var toolCallsArr))
            foreach (var call in toolCallsArr.EnumerateArray())
                AppendToolCall(call, stream, ref partial, toolCallAccumulators, grammarInputs);
        if (hasDelta && delta.TryGetProperty("reasoning_details", out var reasoningDetails) &&
            reasoningDetails.ValueKind == JsonValueKind.Array)
            foreach (var detail in reasoningDetails.EnumerateArray())
                AppendReasoningDetail(stream, ref partial, detail, ref contentIndex);

        // 4. 【AI】【响应解析】记录结束原因，允许继续收取尾部用量
        if (finishReason is not null)
        {
            var (stopReason, errorMessage) = MapFinishReason(finishReason);
            partial = partial with
            {
                StopReason = stopReason,
                ErrorMessage = errorMessage,
                RawStopReason = finishReason,
                EndTurn = stopReason == StopReason.EndTurn,
                Timestamp = DateTimeOffset.UtcNow
            };

            if (!deferCompletion) Complete(stream, ref partial, toolCallAccumulators, supportsFinishReason: true);
            return true;
        }

        contentIndex = partial.Content.Count;
        return false;
    }

    /// <summary>
    /// 【AI】【响应完成】按内容顺序闭合消息，并依据结束原因约定生成最终成功或错误事件。
    /// </summary>
    /// <param name="stream">事件输出流。</param>
    /// <param name="partial">当前部分消息。</param>
    /// <param name="toolCallAccumulators">工具调用累计状态。</param>
    /// <param name="supportsFinishReason">provider 是否声明会发送 finish_reason。</param>
    /// <returns>是否已经发出终止事件。</returns>
    public static bool Complete(
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        Dictionary<int, ToolCallAccumulator> toolCallAccumulators,
        bool supportsFinishReason)
    {
        // 1. 【AI】【语法工具】内容交错不代表工具结束，所有块在消息结束时各完成一次
        for (var index = 0; index < partial.Content.Count; index++)
        {
            if (partial.Content[index] is TextContent) stream.Push(new TextEndEvent(index, partial));
            if (partial.Content[index] is ThinkingContent thinking)
                stream.Push(new ThinkingEndEvent(index, partial, thinking.Thinking, thinking.ThinkingSignature));
            if (toolCallAccumulators.TryGetValue(index, out var tool))
                CompleteToolCall(stream, ref partial, tool, toolCallAccumulators);
        }
        if (partial.StopReason is null && supportsFinishReason)
        {
            partial = partial with
            {
                StopReason = StopReason.Error,
                ErrorMessage = "Stream ended without finish_reason",
                RawStopReason = null,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        else if (partial.StopReason is null)
        {
            partial = partial with
            {
                StopReason = toolCallAccumulators.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
                EndTurn = toolCallAccumulators.Count == 0,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        if (partial.StopReason == StopReason.Error)
            stream.Push(new ErrorEvent(partial.ErrorMessage ?? "Provider returned an error stop reason", partial, partial));
        else
            stream.Push(new DoneEvent(partial));
        return true;
    }

    /// <summary>【AI】【响应解析】追加普通推理文本并发出 thinking 生命周期事件。</summary>
    /// <param name="stream">事件输出流。</param>
    /// <param name="partial">当前消息快照。</param>
    /// <param name="delta">推理文本增量。</param>
    /// <param name="signature">用于下次请求回放的原始思考字段名。</param>
    /// <param name="contentIndex">新建内容块的位置。</param>
    private static void AppendThinkingDelta(
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        string delta,
        string signature,
        ref int contentIndex)
    {
        var index = EnsureThinkingBlock(stream, ref partial, signature, ref contentIndex);
        var existing = (ThinkingContent)partial.Content[index];
        var updated = existing with { Thinking = existing.Thinking + delta };
        var next = partial.Content.ToList();
        next[index] = updated;
        partial = partial with { Content = next };
        stream.Push(new ThinkingDeltaEvent(index, delta, partial));
    }

    /// <summary>【AI】【响应解析】保存结构化 reasoning_details，并合并连续的同类文本项。</summary>
    /// <param name="stream">事件输出流。</param>
    /// <param name="partial">当前消息快照。</param>
    /// <param name="detail">供应商推理明细。</param>
    /// <param name="contentIndex">新建内容块的位置。</param>
    private static void AppendReasoningDetail(
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        JsonElement detail,
        ref int contentIndex)
    {
        if (!OpenAiReasoningDetails.IsValid(detail))
        {
            return;
        }

        var index = EnsureThinkingBlock(stream, ref partial, "", ref contentIndex);
        var current = (ThinkingContent)partial.Content[index];
        var details = new List<JsonElement>();
        if (!string.IsNullOrWhiteSpace(current.ThinkingSignature))
        {
            try
            {
                using var existing = JsonDocument.Parse(current.ThinkingSignature);
                if (existing.RootElement.ValueKind == JsonValueKind.Array)
                {
                    details.AddRange(existing.RootElement.EnumerateArray().Select(static item => item.Clone()));
                }
            }
            catch (JsonException)
            {
                details.Clear();
            }
        }

        var incoming = detail.Clone();
        var type = TryGetString(incoming, "type");
        var textField = type == "reasoning.summary" ? "summary" : "text";
        if (details.Count > 0 && type is "reasoning.text" or "reasoning.summary" &&
            TryGetString(details[^1], "type") == type)
        {
            var merged = new Dictionary<string, object?>();
            foreach (var property in details[^1].EnumerateObject())
                merged[property.Name] = property.Value.Clone();
            merged[textField] = TryGetString(details[^1], textField) + TryGetString(incoming, textField);
            // 1. 【AI】【明细合并】保持首个非空签名和格式、首个非 null ID 与索引，密文项始终独立
            foreach (var field in new[] { "id", "format", "index", "signature" })
            {
                if (field == "signature" && type != "reasoning.text") continue;
                var needsValue = !details[^1].TryGetProperty(field, out var previous) ||
                    previous.ValueKind == JsonValueKind.Null ||
                    field is "format" or "signature" && previous.ValueKind == JsonValueKind.String && previous.GetString()!.Length == 0;
                if (needsValue && incoming.TryGetProperty(field, out var value)) merged[field] = value.Clone();
            }
            details[^1] = JsonSerializer.SerializeToElement(merged, OpenAiJsonContext.Default.DictionaryStringObject);
        }
        else
        {
            details.Add(incoming);
        }

        var updated = current with
        {
            ThinkingSignature = JsonSerializer.Serialize(details, OpenAiJsonContext.Default.ListJsonElement)
        };
        var next = partial.Content.ToList();
        next[index] = updated;
        partial = partial with { Content = next };
    }

    /// <summary>【AI】【正文累积】正文和思考交错时仍复用第一个正文块，稳定事件内容索引。</summary>
    /// <param name="stream">事件流。</param><param name="partial">当前消息。</param><param name="delta">非空正文。</param>
    /// <param name="contentIndex">新块索引。</param>
    private static void AppendTextDelta(AssistantMessageStream stream, ref AssistantMessage partial, string delta, ref int contentIndex)
    {
        var index = FindContentIndex<TextContent>(partial.Content);
        if (index < 0)
        {
            var blocks = partial.Content.ToList();
            index = contentIndex = blocks.Count;
            blocks.Add(new TextContent(""));
            partial = partial with { Content = blocks };
            stream.Push(new TextStartEvent(index, partial));
        }
        var existing = (TextContent)partial.Content[index];
        var updated = partial.Content.ToList();
        updated[index] = existing with { Text = existing.Text + delta };
        partial = partial with { Content = updated };
        stream.Push(new TextDeltaEvent(index, delta, partial));
    }

    /// <summary>【AI】【思考累积】整个响应只创建一个思考块，首个字段决定初始签名。</summary>
    /// <param name="stream">事件流。</param><param name="partial">当前消息。</param><param name="signature">初始签名。</param>
    /// <param name="contentIndex">新块索引。</param><returns>思考块索引。</returns>
    private static int EnsureThinkingBlock(AssistantMessageStream stream, ref AssistantMessage partial, string signature, ref int contentIndex)
    {
        var index = FindContentIndex<ThinkingContent>(partial.Content);
        if (index >= 0) return index;
        var blocks = partial.Content.ToList();
        index = contentIndex = blocks.Count;
        blocks.Add(new ThinkingContent("") { ThinkingSignature = signature });
        partial = partial with { Content = blocks };
        stream.Push(new ThinkingStartEvent(index, partial));
        return index;
    }

    /// <summary>【AI】【块定位】查找指定内容类型的首个稳定索引。</summary>
    /// <typeparam name="T">内容块类型。</typeparam><param name="content">当前内容集合。</param><returns>索引，缺失时为 -1。</returns>
    private static int FindContentIndex<T>(IReadOnlyList<ContentBlock> content) where T : ContentBlock
    {
        for (var index = 0; index < content.Count; index++)
            if (content[index] is T) return index;
        return -1;
    }

    /// <summary>【AI】【响应解析】安全读取可选对象的字符串属性。</summary>
    /// <param name="element">可能缺失的对象。</param>
    /// <param name="propertyName">属性名称。</param>
    /// <returns>字符串值；缺失或类型不符时为空。</returns>
    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void ApplyChunkMetadata(JsonElement root, JsonElement? choice, ref AssistantMessage partial)
    {
        if (partial.ResponseId is null &&
            root.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            partial = partial with { ResponseId = id.GetString() };
        }

        if (TryGetUsage(root, out var usage) ||
            (choice.HasValue && TryGetUsage(choice.Value, out usage)))
        {
            partial = partial with { Usage = usage };
        }
    }

    private static bool TryGetUsage(JsonElement element, out Usage usage)
    {
        usage = default;
        if (!element.TryGetProperty("usage", out var usageElement) ||
            usageElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        var promptTokens = GetInt(usageElement, "prompt_tokens") ?? GetInt(usageElement, "input_tokens") ?? 0;
        var completionTokens = GetInt(usageElement, "completion_tokens") ?? GetInt(usageElement, "output_tokens") ?? 0;
        var hasPromptDetails = usageElement.TryGetProperty("prompt_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object;
        var promptDetails = hasPromptDetails ? details : default;
        var hasCompletionDetails = usageElement.TryGetProperty("completion_tokens_details", out var outputDetails) &&
            outputDetails.ValueKind == JsonValueKind.Object;
        var completionDetails = hasCompletionDetails ? outputDetails : default;
        // 1. 不同 OpenAI-compatible provider 会把缓存命中数放在不同层级
        var reportedCacheRead = GetInt(promptDetails, "cached_tokens") ??
            GetInt(usageElement, "prompt_cache_hit_tokens") ??
            GetInt(usageElement, "cached_tokens") ??
            0;
        var cacheWrite = GetInt(promptDetails, "cache_write_tokens") ?? 0;
        // 2. cache_write_tokens 是独立的写入量，不能从 cacheRead 中再次扣除
        var cacheRead = reportedCacheRead;
        var reasoningTokens = GetInt(completionDetails, "reasoning_tokens");
        var input = Math.Max(0, promptTokens - cacheRead - cacheWrite);
        // OpenAI completion_tokens 已包含 reasoning_tokens，不能重复累加
        var output = completionTokens;

        usage = hasPromptDetails || cacheRead > 0 || cacheWrite > 0
            ? new Usage(input, output, cacheRead, cacheWrite) { ReasoningTokens = reasoningTokens, TotalTokens = GetInt(usageElement, "total_tokens") }
            : new Usage(input, output) { ReasoningTokens = reasoningTokens, TotalTokens = GetInt(usageElement, "total_tokens") };
        return true;
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    /// <summary>【AI】【语法工具】定位交错调用并把原始输入转换为 JSON 参数增量。</summary>
    /// <param name="delta">单个工具调用增量。</param>
    /// <param name="stream">事件输出流。</param>
    /// <param name="partial">当前消息快照。</param>
    /// <param name="accumulators">按内容位置存储的工具状态。</param>
    /// <param name="grammarInputs">语法工具输入属性映射。</param>
    private static void AppendToolCall(JsonElement delta, AssistantMessageStream stream, ref AssistantMessage partial,
        Dictionary<int, ToolCallAccumulator> accumulators, IReadOnlyDictionary<string, string>? grammarInputs)
    {
        // 1. 【AI】【语法工具】优先使用流索引，其次使用调用标识；缺少二者时建立独立块
        var index = GetInt(delta, "index");
        var id = TryGetString(delta, "id");
        var hasFunction = delta.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object;
        var hasCustom = delta.TryGetProperty("custom", out var custom) && custom.ValueKind == JsonValueKind.Object;
        var name = TryGetString(function, "name") ?? TryGetString(custom, "name") ?? "";
        var accumulator = index.HasValue ? accumulators.Values.FirstOrDefault(item => item.StreamIndex == index) : null;
        accumulator ??= !string.IsNullOrEmpty(id) ? accumulators.Values.FirstOrDefault(item => item.Id == id) : null;
        var isNew = accumulator is null;
        var acc = accumulator ?? new ToolCallAccumulator(id ?? "", name, "", partial.Content.Count);
        acc = acc with
        {
            Id = string.IsNullOrEmpty(acc.Id) ? id ?? "" : acc.Id,
            Name = string.IsNullOrEmpty(acc.Name) ? name : acc.Name,
            StreamIndex = acc.StreamIndex ?? index
        };

        // 2. 【AI】【语法工具】允许先收到调用标识，再收到 custom 声明和名称
        if (hasCustom && !hasFunction && acc.CustomInput is null)
            acc = acc with { CustomInput = new CustomToolInput(grammarInputs?.GetValueOrDefault(acc.Name) ?? "input") };
        if (isNew)
        {
            var content = partial.Content.ToList();
            content.Add(new ToolCallContent(acc.Id, acc.Name, ToolArguments(acc)));
            partial = partial with { Content = content };
            stream.Push(new ToolCallStartEvent(acc.ContentIndex, partial, acc.Id, acc.Name));
        }

        var text = "";
        if (hasFunction && function.TryGetProperty("arguments", out var arguments))
        {
            text = arguments.ValueKind == JsonValueKind.String ? arguments.GetString() ?? "" : arguments.GetRawText();
            acc = acc with { Arguments = acc.Arguments + text };
        }
        else if (acc.CustomInput is { } input && TryGetString(custom, "input") is { Length: > 0 } raw)
        {
            input.Input += raw;
            text = ConstrainedSampling.AppendGrammarToolInputJsonDelta(input.Buffer, input.Property, input.Input, false) ?? "";
        }
        accumulators[acc.ContentIndex] = acc;
        var next = partial.Content.ToList();
        next[acc.ContentIndex] = new ToolCallContent(acc.Id, acc.Name, ToolArguments(acc));
        partial = partial with { Content = next };
        stream.Push(new ToolCallDeltaEvent(acc.ContentIndex, text, partial));
    }

    /// <summary>【AI】【语法工具】生成当前参数快照，保持执行接口接收 JSON 对象。</summary>
    /// <param name="acc">工具调用累计状态。</param>
    /// <returns>可读取的完整 JSON 参数对象。</returns>
    private static string ToolArguments(ToolCallAccumulator acc) => acc.CustomInput is { } input
        ? JsonSerializer.Serialize(new Dictionary<string, string> { [input.Property] = input.Input }, OpenAiJsonContext.Default.DictionaryStringString)
        : StreamingJsonParser.ParseStreamingJsonObjectRawText(acc.Arguments);

    /// <summary>【AI】【语法工具】闭合自定义 JSON 增量并发布包含完整调用的结束事件。</summary>
    /// <param name="stream">输出事件流。</param>
    /// <param name="partial">当前消息快照。</param>
    /// <param name="acc">当前内容位置对应的工具状态。</param>
    /// <param name="toolCallAccumulators">待完成的工具调用状态。</param>
    private static void CompleteToolCall(
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        ToolCallAccumulator acc,
        Dictionary<int, ToolCallAccumulator> toolCallAccumulators)
    {
        if (acc.IsClosed) return;

        var content = partial.Content.ToList();
        var toolCall = new ToolCallContent(acc.Id, acc.Name, ToolArguments(acc));
        content[acc.ContentIndex] = toolCall;
        partial = partial with { Content = content };
        if (acc.CustomInput is { } input)
        {
            var delta = ConstrainedSampling.AppendGrammarToolInputJsonDelta(input.Buffer, input.Property, input.Input, true);
            if (delta is not null) stream.Push(new ToolCallDeltaEvent(acc.ContentIndex, delta, partial));
        }
        stream.Push(new ToolCallEndEvent(acc.ContentIndex, partial, toolCall));
        toolCallAccumulators[acc.ContentIndex] = acc with { IsClosed = true };
    }

    private static (StopReason StopReason, string? ErrorMessage) MapFinishReason(string finishReason) =>
        finishReason switch
        {
            "stop" or "end" => (StopReason.EndTurn, null),
            "length" => (StopReason.MaxTokens, null),
            "function_call" or "tool_calls" => (StopReason.ToolUse, null),
            "content_filter" => (StopReason.Error, "Provider finish_reason: content_filter"),
            "network_error" => (StopReason.Error, "Provider finish_reason: network_error"),
            _ => (StopReason.Error, $"Provider finish_reason: {finishReason}")
        };

    /// <summary>【AI】【工具累积】保存普通参数片段与自定义输入状态。</summary>
    /// <param name="Id">调用标识。</param>
    /// <param name="Name">工具名称。</param>
    /// <param name="Arguments">普通函数参数 JSON 片段。</param>
    /// <param name="ContentIndex">输出内容位置。</param>
    /// <param name="IsClosed">是否已发布结束事件。</param>
    internal record ToolCallAccumulator(
        string Id,
        string Name,
        string Arguments,
        int ContentIndex,
        bool IsClosed = false)
    {
        public int? StreamIndex { get; init; }
        public CustomToolInput? CustomInput { get; init; }
    }

    /// <summary>【AI】【语法工具】保存原始输入和 JSON 字符串增量的闭合状态。</summary>
    /// <param name="property">JSON 参数中的输入属性名。</param>
    internal sealed class CustomToolInput(string property)
    {
        public string Property { get; } = property;
        public string Input { get; set; } = "";
        public ConstrainedSampling.GrammarToolInputBuffer Buffer { get; } = new();
    }
}
