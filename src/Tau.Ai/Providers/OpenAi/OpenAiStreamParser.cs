using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>
/// Parses OpenAI streaming chat completion chunks into StreamEvents.
/// </summary>
internal static class OpenAiStreamParser
{
    public static bool ParseChunk(
        string json,
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        ref Dictionary<int, ToolCallAccumulator> toolCallAccumulators,
        ref int contentIndex)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        ApplyChunkMetadata(root, null, ref partial);

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return false;
        }

        var choice = choices[0];
        ApplyChunkMetadata(root, choice, ref partial);
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

        // 推理文本和结构化 reasoning_details 必须先于普通文本处理,避免跨块顺序丢失
        if (hasDelta && delta.TryGetProperty("reasoning_content", out var reasoningContent) &&
            reasoningContent.ValueKind == JsonValueKind.String)
        {
            AppendThinkingDelta(stream, ref partial, reasoningContent.GetString() ?? "", ref contentIndex);
        }

        if (hasDelta && delta.TryGetProperty("reasoning", out var reasoning) &&
            reasoning.ValueKind == JsonValueKind.String)
        {
            AppendThinkingDelta(stream, ref partial, reasoning.GetString() ?? "", ref contentIndex);
        }

        if (hasDelta && delta.TryGetProperty("reasoning_details", out var reasoningDetails) &&
            reasoningDetails.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in reasoningDetails.EnumerateArray())
            {
                AppendReasoningDetail(stream, ref partial, detail, ref contentIndex);
            }
        }

        // Text content
        if (hasDelta &&
            delta.TryGetProperty("content", out var contentProp) &&
            contentProp.ValueKind == JsonValueKind.String)
        {
            CloseOpenThinking(stream, partial);
            var text = contentProp.GetString()!;
            if (partial.Content.Count == 0 || partial.Content[^1] is not TextContent)
            {
                CloseOpenToolCalls(stream, partial, toolCallAccumulators);
                var content = partial.Content.ToList();
                contentIndex = content.Count;
                content.Add(new TextContent(""));
                partial = partial with { Content = content };
                stream.Push(new TextStartEvent(contentIndex, partial));
            }

            var existing = (TextContent)partial.Content[^1];
            var updated = existing with { Text = existing.Text + text };
            var newContent = partial.Content.ToList();
            newContent[^1] = updated;
            partial = partial with { Content = newContent };
            stream.Push(new TextDeltaEvent(contentIndex, text, partial));
        }

        // Tool calls
        if (hasDelta && delta.TryGetProperty("tool_calls", out var toolCallsArr))
        {
            foreach (var tc in toolCallsArr.EnumerateArray())
            {
                var index = tc.GetProperty("index").GetInt32();

                if (!toolCallAccumulators.TryGetValue(index, out var acc))
                {
                    CloseOpenThinking(stream, partial);
                    CloseOpenText(stream, partial);

                    var id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString()! : "";
                    var name = tc.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var n)
                        ? n.GetString()! : "";
                    contentIndex = partial.Content.Count;
                    acc = new ToolCallAccumulator(id, name, "", contentIndex);
                    toolCallAccumulators[index] = acc;

                    var tcContent = new ToolCallContent(acc.Id, acc.Name, "{}");
                    var tcList = partial.Content.ToList();
                    tcList.Add(tcContent);
                    partial = partial with { Content = tcList };
                    stream.Push(new ToolCallStartEvent(contentIndex, partial, acc.Id, acc.Name));
                }

                if (tc.TryGetProperty("id", out var idUpdate) &&
                    idUpdate.ValueKind == JsonValueKind.String &&
                    string.IsNullOrWhiteSpace(acc.Id))
                {
                    acc = acc with { Id = idUpdate.GetString() ?? acc.Id };
                    toolCallAccumulators[index] = acc;
                }

                if (tc.TryGetProperty("function", out var func))
                {
                    if (func.TryGetProperty("name", out var nameDelta) &&
                        nameDelta.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(nameDelta.GetString()) &&
                        string.IsNullOrWhiteSpace(acc.Name))
                    {
                        acc = acc with { Name = nameDelta.GetString()! };
                    }

                    if (!func.TryGetProperty("arguments", out var argDelta))
                    {
                        toolCallAccumulators[index] = acc;
                        continue;
                    }

                    var argChunk = argDelta.ValueKind == JsonValueKind.String
                        ? argDelta.GetString() ?? ""
                        : argDelta.GetRawText();
                    acc = acc with { Arguments = acc.Arguments + argChunk };
                    toolCallAccumulators[index] = acc;

                    var tcIdx = acc.ContentIndex;
                    if (tcIdx >= 0 && tcIdx < partial.Content.Count && partial.Content[tcIdx] is ToolCallContent existing)
                    {
                        var updatedTc = existing with
                        {
                            Name = string.IsNullOrWhiteSpace(existing.Name) ? acc.Name : existing.Name,
                            Arguments = StreamingJsonParser.ParseStreamingJsonObjectRawText(acc.Arguments)
                        };
                        var newContent = partial.Content.ToList();
                        newContent[tcIdx] = updatedTc;
                        partial = partial with { Content = newContent };
                    }

                    stream.Push(new ToolCallDeltaEvent(acc.ContentIndex, argChunk, partial));
                }
            }
        }

        // Finish
        if (finishReason is not null)
        {
            // Close open text
            CloseOpenText(stream, partial);

            // Close open tool calls
            CloseOpenToolCalls(stream, partial, toolCallAccumulators);

            var (stopReason, errorMessage) = MapFinishReason(finishReason);
            partial = partial with
            {
                StopReason = stopReason,
                ErrorMessage = errorMessage,
                RawStopReason = finishReason,
                EndTurn = stopReason == StopReason.EndTurn,
                Timestamp = DateTimeOffset.UtcNow
            };

            if (stopReason == StopReason.Error)
            {
                stream.Push(new ErrorEvent(errorMessage ?? "Provider returned an error stop reason", partial, partial));
                return true;
            }

            stream.Push(new DoneEvent(partial));
            return true;
        }

        contentIndex = partial.Content.Count;
        return false;
    }

    /// <summary>
    /// 在 SSE 流结束但没有 finish_reason 时完成消息。
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
        if (partial.StopReason is not null)
        {
            return true;
        }

        CloseOpenThinking(stream, partial);
        CloseOpenText(stream, partial);
        CloseOpenToolCalls(stream, partial, toolCallAccumulators);
        if (supportsFinishReason)
        {
            partial = partial with
            {
                StopReason = StopReason.Error,
                ErrorMessage = "Stream ended without finish_reason",
                RawStopReason = null,
                Timestamp = DateTimeOffset.UtcNow
            };
            stream.Push(new ErrorEvent(partial.ErrorMessage, partial, partial));
            return true;
        }

        partial = partial with
        {
            StopReason = toolCallAccumulators.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
            EndTurn = toolCallAccumulators.Count == 0,
            Timestamp = DateTimeOffset.UtcNow
        };
        stream.Push(new DoneEvent(partial));
        return true;
    }

    /// <summary>追加普通推理文本并发出 thinking 生命周期事件。</summary>
    private static void AppendThinkingDelta(
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        string delta,
        ref int contentIndex)
    {
        if (partial.Content.Count == 0 || partial.Content[^1] is not ThinkingContent)
        {
            CloseOpenText(stream, partial);
            var content = partial.Content.ToList();
            contentIndex = content.Count;
            content.Add(new ThinkingContent(string.Empty));
            partial = partial with { Content = content };
            stream.Push(new ThinkingStartEvent(contentIndex, partial));
        }

        var existing = (ThinkingContent)partial.Content[^1];
        var updated = existing with { Thinking = existing.Thinking + delta };
        var next = partial.Content.ToList();
        next[^1] = updated;
        partial = partial with { Content = next };
        stream.Push(new ThinkingDeltaEvent(contentIndex, delta, partial));
    }

    /// <summary>保存结构化 reasoning_details,并合并连续的同类文本项。</summary>
    private static void AppendReasoningDetail(
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        JsonElement detail,
        ref int contentIndex)
    {
        if (detail.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (partial.Content.Count == 0 || partial.Content[^1] is not ThinkingContent)
        {
            CloseOpenText(stream, partial);
            var content = partial.Content.ToList();
            contentIndex = content.Count;
            content.Add(new ThinkingContent(string.Empty));
            partial = partial with { Content = content };
            stream.Push(new ThinkingStartEvent(contentIndex, partial));
        }

        var current = (ThinkingContent)partial.Content[^1];
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
        if (details.Count > 0 && details[^1].ValueKind == JsonValueKind.Object &&
            TryGetString(details[^1], "type") == TryGetString(incoming, "type") &&
            TryGetString(details[^1], "text") is { } previousText &&
            TryGetString(incoming, "text") is { } incomingText)
        {
            var merged = new Dictionary<string, object?>();
            foreach (var property in details[^1].EnumerateObject())
            {
                merged[property.Name] = property.NameEquals("text") ? previousText + incomingText : property.Value.Clone();
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
        next[^1] = updated;
        partial = partial with { Content = next };
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
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

    private static void CloseOpenText(AssistantMessageStream stream, AssistantMessage partial)
    {
        if (partial.Content.Count > 0 && partial.Content[^1] is TextContent)
        {
            stream.Push(new TextEndEvent(partial.Content.Count - 1, partial));
        }
    }

    private static void CloseOpenThinking(AssistantMessageStream stream, AssistantMessage partial)
    {
        if (partial.Content.Count > 0 && partial.Content[^1] is ThinkingContent thinking)
        {
            stream.Push(new ThinkingEndEvent(partial.Content.Count - 1, partial, thinking.Thinking, thinking.ThinkingSignature));
        }
    }

    private static void CloseOpenToolCalls(
        AssistantMessageStream stream,
        AssistantMessage partial,
        Dictionary<int, ToolCallAccumulator> toolCallAccumulators)
    {
        foreach (var (index, acc) in toolCallAccumulators.OrderBy(item => item.Value.ContentIndex))
        {
            if (acc.IsClosed)
            {
                continue;
            }

            stream.Push(new ToolCallEndEvent(acc.ContentIndex, partial));
            toolCallAccumulators[index] = acc with { IsClosed = true };
        }
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

    internal record ToolCallAccumulator(
        string Id,
        string Name,
        string Arguments,
        int ContentIndex,
        bool IsClosed = false);
}
