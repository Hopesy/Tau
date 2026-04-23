using System.Text.Json;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>
/// Parses OpenAI streaming chat completion chunks into StreamEvents.
/// </summary>
internal static class OpenAiStreamParser
{
    public static void ParseChunk(
        string json,
        AssistantMessageStream stream,
        ref AssistantMessage partial,
        ref Dictionary<int, ToolCallAccumulator> toolCallAccumulators,
        ref int contentIndex)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            if (root.TryGetProperty("usage", out var usage))
            {
                partial = partial with
                {
                    Usage = new Usage(
                        usage.GetProperty("prompt_tokens").GetInt32(),
                        usage.GetProperty("completion_tokens").GetInt32())
                };
            }
            return;
        }

        var choice = choices[0];
        var delta = choice.GetProperty("delta");
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null
            ? fr.GetString() : null;

        // Text content
        if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
        {
            var text = contentProp.GetString()!;
            if (partial.Content.Count == 0 || partial.Content[^1] is not TextContent)
            {
                var content = partial.Content.ToList();
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
        if (delta.TryGetProperty("tool_calls", out var toolCallsArr))
        {
            foreach (var tc in toolCallsArr.EnumerateArray())
            {
                var index = tc.GetProperty("index").GetInt32();

                if (!toolCallAccumulators.TryGetValue(index, out var acc))
                {
                    var id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString()! : "";
                    var name = tc.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var n)
                        ? n.GetString()! : "";
                    acc = new ToolCallAccumulator(id, name, "");
                    toolCallAccumulators[index] = acc;

                    contentIndex++;
                    var tcContent = new ToolCallContent(acc.Id, acc.Name, "");
                    var tcList = partial.Content.ToList();
                    tcList.Add(tcContent);
                    partial = partial with { Content = tcList };
                    stream.Push(new ToolCallStartEvent(contentIndex, partial));
                }

                if (tc.TryGetProperty("function", out var func) && func.TryGetProperty("arguments", out var argDelta))
                {
                    var argChunk = argDelta.GetString() ?? "";
                    acc = acc with { Arguments = acc.Arguments + argChunk };
                    toolCallAccumulators[index] = acc;

                    var tcIdx = partial.Content.Count - toolCallAccumulators.Count + index;
                    if (tcIdx >= 0 && tcIdx < partial.Content.Count && partial.Content[tcIdx] is ToolCallContent existing)
                    {
                        var updatedTc = existing with { Arguments = acc.Arguments };
                        var newContent = partial.Content.ToList();
                        newContent[tcIdx] = updatedTc;
                        partial = partial with { Content = newContent };
                    }

                    stream.Push(new ToolCallDeltaEvent(contentIndex, argChunk, partial));
                }
            }
        }

        // Finish
        if (finishReason is not null)
        {
            // Close open text
            if (partial.Content.Count > 0 && partial.Content[^1] is TextContent)
            {
                stream.Push(new TextEndEvent(contentIndex, partial));
                contentIndex++;
            }

            // Close open tool calls
            foreach (var acc in toolCallAccumulators.Values)
            {
                stream.Push(new ToolCallEndEvent(contentIndex, partial));
            }

            var stopReason = finishReason switch
            {
                "stop" => StopReason.EndTurn,
                "length" => StopReason.MaxTokens,
                "tool_calls" => StopReason.ToolUse,
                "content_filter" => StopReason.ContentFilter,
                _ => StopReason.EndTurn
            };

            partial = partial with
            {
                StopReason = stopReason,
                Timestamp = DateTimeOffset.UtcNow
            };

            stream.Push(new DoneEvent(partial));
        }
    }

    internal record ToolCallAccumulator(string Id, string Name, string Arguments);
}
