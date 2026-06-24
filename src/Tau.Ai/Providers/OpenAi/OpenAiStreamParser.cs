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

        // Text content
        if (hasDelta &&
            delta.TryGetProperty("content", out var contentProp) &&
            contentProp.ValueKind == JsonValueKind.String)
        {
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
                    stream.Push(new ToolCallStartEvent(contentIndex, partial));
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
        var reportedCacheRead = GetInt(promptDetails, "cached_tokens") ?? 0;
        var cacheWrite = GetInt(promptDetails, "cache_write_tokens") ?? 0;
        var cacheRead = cacheWrite > 0 ? Math.Max(0, reportedCacheRead - cacheWrite) : reportedCacheRead;
        var reasoningTokens = GetInt(completionDetails, "reasoning_tokens") ?? 0;
        var input = Math.Max(0, promptTokens - cacheRead - cacheWrite);
        var output = completionTokens + reasoningTokens;

        usage = hasPromptDetails
            ? new Usage(input, output, cacheRead, cacheWrite)
            : new Usage(input, output);
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
