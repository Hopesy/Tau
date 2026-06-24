using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentExtensionLifecycleEventModule(
    string FilePath,
    string Scope,
    string Runtime,
    IReadOnlyList<string> EventTypes);

public sealed record CodingAgentExtensionLifecycleEventError(
    string FilePath,
    string Scope,
    string Runtime,
    string EventType,
    string Error);

public sealed class CodingAgentExtensionLifecycleEventSink
{
    private static readonly HashSet<string> SupportedEventTypes = new(StringComparer.Ordinal)
    {
        "agent_start",
        "agent_end",
        "turn_start",
        "turn_end",
        "message_start",
        "message_update",
        "message_end",
        "tool_execution_start",
        "tool_execution_update",
        "tool_execution_end"
    };

    private readonly IReadOnlyList<CodingAgentExtensionLifecycleEventModule> _modules;
    private readonly CodingAgentJavaScriptExtensionRuntime _runtime;

    public CodingAgentExtensionLifecycleEventSink(
        IReadOnlyList<CodingAgentExtensionLifecycleEventModule> modules,
        CodingAgentJavaScriptExtensionRuntime runtime)
    {
        _modules = modules;
        _runtime = runtime;
    }

    public static bool IsSupportedEventType(string eventType) => SupportedEventTypes.Contains(eventType);

    public Task<IReadOnlyList<CodingAgentExtensionLifecycleEventError>> PublishAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        if (!IsSupportedEventType(agentEvent.Type))
        {
            return Task.FromResult<IReadOnlyList<CodingAgentExtensionLifecycleEventError>>([]);
        }

        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        using var payload = CodingAgentExtensionLifecycleEventJson.CreateDocument(agentEvent);
        foreach (var module in _modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!module.EventTypes.Any(type => type.Equals(agentEvent.Type, StringComparison.Ordinal)))
            {
                continue;
            }

            var result = _runtime.EmitEvent(module.FilePath, payload.RootElement);
            if (!result.Success)
            {
                errors.Add(new CodingAgentExtensionLifecycleEventError(
                    module.FilePath,
                    module.Scope,
                    module.Runtime,
                    agentEvent.Type,
                    result.Error ?? $"javascript extension event emit failed for '{agentEvent.Type}'"));
                continue;
            }

            foreach (var handlerError in result.HandlerErrors)
            {
                errors.Add(new CodingAgentExtensionLifecycleEventError(
                    module.FilePath,
                    module.Scope,
                    module.Runtime,
                    agentEvent.Type,
                    handlerError));
            }
        }

        return Task.FromResult<IReadOnlyList<CodingAgentExtensionLifecycleEventError>>(errors);
    }
}

file static class CodingAgentExtensionLifecycleEventJson
{
    public static JsonDocument CreateDocument(AgentEvent agentEvent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", agentEvent.Type);
            switch (agentEvent)
            {
                case AgentStartEvent:
                    break;
                case AgentEndEvent end:
                    writer.WritePropertyName("messages");
                    WriteMessages(writer, end.Messages);
                    if (!string.IsNullOrWhiteSpace(end.ErrorMessage))
                    {
                        writer.WriteString("errorMessage", end.ErrorMessage);
                    }
                    break;
                case TurnStartEvent turnStart:
                    writer.WriteNumber("turnIndex", turnStart.TurnIndex);
                    writer.WriteNumber("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    break;
                case TurnEndEvent turnEnd:
                    writer.WriteNumber("turnIndex", turnEnd.TurnIndex);
                    WriteMessageProperty(writer, "message", turnEnd.Message);
                    writer.WritePropertyName("toolResults");
                    WriteMessages(writer, turnEnd.ToolResults);
                    break;
                case MessageStartEvent messageStart:
                    WriteMessageProperty(writer, "message", messageStart.Message);
                    break;
                case MessageUpdateEvent messageUpdate:
                    WriteMessageProperty(writer, "message", messageUpdate.Message);
                    writer.WritePropertyName("assistantMessageEvent");
                    WriteStreamEvent(writer, messageUpdate.StreamEvent);
                    break;
                case MessageEndEvent messageEnd:
                    WriteMessageProperty(writer, "message", messageEnd.Message);
                    break;
                case ToolExecutionStartEvent toolStart:
                    writer.WriteString("toolCallId", toolStart.ToolCallId);
                    writer.WriteString("toolName", toolStart.ToolName);
                    writer.WritePropertyName("args");
                    WriteJsonOrStringValue(writer, toolStart.Args);
                    break;
                case ToolExecutionUpdateEvent toolUpdate:
                    writer.WriteString("toolCallId", toolUpdate.ToolCallId);
                    writer.WriteString("toolName", toolUpdate.ToolName ?? string.Empty);
                    writer.WritePropertyName("args");
                    WriteJsonOrStringValue(writer, toolUpdate.Args);
                    writer.WritePropertyName("partialResult");
                    if (toolUpdate.PartialResult is null)
                    {
                        WriteToolUpdate(writer, toolUpdate.Update);
                    }
                    else
                    {
                        WriteToolResult(writer, toolUpdate.PartialResult);
                    }
                    break;
                case ToolExecutionEndEvent toolEnd:
                    writer.WriteString("toolCallId", toolEnd.ToolCallId);
                    writer.WriteString("toolName", toolEnd.ToolName ?? string.Empty);
                    writer.WritePropertyName("result");
                    WriteToolResult(writer, toolEnd.Result);
                    writer.WriteBoolean("isError", toolEnd.IsError);
                    break;
            }

            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<ChatMessage> messages)
    {
        writer.WriteStartArray();
        foreach (var message in messages)
        {
            WriteMessage(writer, message);
        }

        writer.WriteEndArray();
    }

    private static void WriteMessageProperty(Utf8JsonWriter writer, string propertyName, ChatMessage? message)
    {
        writer.WritePropertyName(propertyName);
        if (message is null)
        {
            writer.WriteNullValue();
            return;
        }

        WriteMessage(writer, message);
    }

    private static void WriteMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role);
        switch (message)
        {
            case UserMessage user:
                writer.WritePropertyName("content");
                WriteContentBlocks(writer, user.Content);
                break;
            case AssistantMessage assistant:
                writer.WritePropertyName("content");
                WriteContentBlocks(writer, assistant.Content);
                if (assistant.StopReason is not null)
                {
                    writer.WriteString("stopReason", MapStopReason(assistant.StopReason));
                }
                if (!string.IsNullOrWhiteSpace(assistant.ErrorMessage))
                {
                    writer.WriteString("errorMessage", assistant.ErrorMessage);
                }
                break;
            case ToolResultMessage toolResult:
                writer.WriteString("toolCallId", toolResult.ToolCallId);
                writer.WritePropertyName("content");
                WriteContentBlocks(writer, toolResult.Content);
                writer.WriteBoolean("isError", toolResult.IsError);
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteContentBlocks(Utf8JsonWriter writer, IReadOnlyList<ContentBlock> content)
    {
        writer.WriteStartArray();
        foreach (var block in content)
        {
            writer.WriteStartObject();
            switch (block)
            {
                case TextContent text:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    break;
                case ThinkingContent thinking:
                    writer.WriteString("type", "thinking");
                    writer.WriteString("thinking", thinking.Thinking);
                    if (!string.IsNullOrWhiteSpace(thinking.ThinkingSignature))
                    {
                        writer.WriteString("thinkingSignature", thinking.ThinkingSignature);
                    }
                    if (thinking.Redacted)
                    {
                        writer.WriteBoolean("redacted", true);
                    }
                    break;
                case ImageContent image:
                    writer.WriteString("type", "image");
                    writer.WriteString("data", image.Data);
                    writer.WriteString("mimeType", image.MimeType);
                    break;
                case ToolCallContent toolCall:
                    writer.WriteString("type", "toolCall");
                    writer.WriteString("id", toolCall.Id);
                    writer.WriteString("name", toolCall.Name);
                    writer.WritePropertyName("arguments");
                    WriteJsonOrStringValue(writer, toolCall.Arguments);
                    if (!string.IsNullOrWhiteSpace(toolCall.ThoughtSignature))
                    {
                        writer.WriteString("thoughtSignature", toolCall.ThoughtSignature);
                    }
                    break;
                default:
                    writer.WriteString("type", block.Type);
                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteStreamEvent(Utf8JsonWriter writer, StreamEvent streamEvent)
    {
        writer.WriteStartObject();
        writer.WriteString("type", streamEvent.Type);
        switch (streamEvent)
        {
            case StartEvent start:
                WriteMessageProperty(writer, "partial", start.Partial);
                break;
            case TextStartEvent textStart:
                writer.WriteNumber("contentIndex", textStart.ContentIndex);
                WriteMessageProperty(writer, "partial", textStart.Partial);
                break;
            case TextDeltaEvent textDelta:
                writer.WriteNumber("contentIndex", textDelta.ContentIndex);
                writer.WriteString("delta", textDelta.Delta);
                WriteMessageProperty(writer, "partial", textDelta.Partial);
                break;
            case TextEndEvent textEnd:
                writer.WriteNumber("contentIndex", textEnd.ContentIndex);
                writer.WriteString("content", GetTextContent(textEnd.Partial, textEnd.ContentIndex));
                WriteMessageProperty(writer, "partial", textEnd.Partial);
                break;
            case ThinkingStartEvent thinkingStart:
                writer.WriteNumber("contentIndex", thinkingStart.ContentIndex);
                WriteMessageProperty(writer, "partial", thinkingStart.Partial);
                break;
            case ThinkingDeltaEvent thinkingDelta:
                writer.WriteNumber("contentIndex", thinkingDelta.ContentIndex);
                writer.WriteString("delta", thinkingDelta.Delta);
                WriteMessageProperty(writer, "partial", thinkingDelta.Partial);
                break;
            case ThinkingEndEvent thinkingEnd:
                writer.WriteNumber("contentIndex", thinkingEnd.ContentIndex);
                writer.WriteString("content", GetThinkingContent(thinkingEnd.Partial, thinkingEnd.ContentIndex));
                WriteMessageProperty(writer, "partial", thinkingEnd.Partial);
                break;
            case ToolCallStartEvent toolCallStart:
                writer.WriteNumber("contentIndex", toolCallStart.ContentIndex);
                WriteMessageProperty(writer, "partial", toolCallStart.Partial);
                break;
            case ToolCallDeltaEvent toolCallDelta:
                writer.WriteNumber("contentIndex", toolCallDelta.ContentIndex);
                writer.WriteString("delta", toolCallDelta.Delta);
                WriteMessageProperty(writer, "partial", toolCallDelta.Partial);
                break;
            case ToolCallEndEvent toolCallEnd:
                writer.WriteNumber("contentIndex", toolCallEnd.ContentIndex);
                writer.WritePropertyName("toolCall");
                WriteToolCallFromPartial(writer, toolCallEnd.Partial, toolCallEnd.ContentIndex);
                WriteMessageProperty(writer, "partial", toolCallEnd.Partial);
                break;
            case DoneEvent done:
                writer.WriteString("reason", MapStopReason(done.Message.StopReason));
                WriteMessageProperty(writer, "message", done.Message);
                break;
            case ErrorEvent error:
                writer.WriteString("reason", MapStopReason(error.Message?.StopReason ?? error.Partial?.StopReason ?? StopReason.Error));
                writer.WritePropertyName("error");
                if (error.Message is not null)
                {
                    WriteMessage(writer, error.Message);
                }
                else if (error.Partial is not null)
                {
                    WriteMessage(writer, error.Partial);
                }
                else
                {
                    WriteErrorAssistantMessage(writer, error.Error);
                }
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteToolCallFromPartial(Utf8JsonWriter writer, AssistantMessage partial, int contentIndex)
    {
        var toolCall = contentIndex >= 0 && contentIndex < partial.Content.Count
            ? partial.Content[contentIndex] as ToolCallContent
            : null;
        if (toolCall is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("id", toolCall.Id);
        writer.WriteString("name", toolCall.Name);
        writer.WritePropertyName("arguments");
        WriteJsonOrStringValue(writer, toolCall.Arguments);
        if (!string.IsNullOrWhiteSpace(toolCall.ThoughtSignature))
        {
            writer.WriteString("thoughtSignature", toolCall.ThoughtSignature);
        }
        writer.WriteEndObject();
    }

    private static string GetTextContent(AssistantMessage partial, int contentIndex)
    {
        return contentIndex >= 0 && contentIndex < partial.Content.Count &&
               partial.Content[contentIndex] is TextContent text
            ? text.Text
            : string.Empty;
    }

    private static string GetThinkingContent(AssistantMessage partial, int contentIndex)
    {
        return contentIndex >= 0 && contentIndex < partial.Content.Count &&
               partial.Content[contentIndex] is ThinkingContent thinking
            ? thinking.Thinking
            : string.Empty;
    }

    private static void WriteToolResult(Utf8JsonWriter writer, ToolResult result)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("content");
        WriteContentBlocks(writer, result.Content);
        writer.WriteBoolean("isError", result.IsError);
        if (result.Details is not null)
        {
            writer.WritePropertyName("details");
            WriteObjectValue(writer, result.Details);
        }
        writer.WriteEndObject();
    }

    private static void WriteToolUpdate(Utf8JsonWriter writer, ToolUpdate update)
    {
        writer.WriteStartObject();
        writer.WriteString("text", update.Text);
        writer.WritePropertyName("content");
        WriteContentBlocks(writer, update.Content ?? [new TextContent(update.Text)]);
        if (update.IsError is not null)
        {
            writer.WriteBoolean("isError", update.IsError.Value);
        }
        if (update.Details is not null)
        {
            writer.WritePropertyName("details");
            WriteObjectValue(writer, update.Details);
        }
        writer.WriteEndObject();
    }

    private static void WriteJsonOrStringValue(Utf8JsonWriter writer, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(value);
        }
    }

    private static void WriteObjectValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case JsonDocument document:
                document.RootElement.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int integer:
                writer.WriteNumberValue(integer);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteErrorAssistantMessage(Utf8JsonWriter writer, string error)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteString("stopReason", "error");
        writer.WriteString("errorMessage", error);
        writer.WriteEndObject();
    }

    private static string MapStopReason(StopReason? stopReason)
    {
        return stopReason switch
        {
            StopReason.MaxTokens => "length",
            StopReason.ToolUse => "toolUse",
            StopReason.Error => "error",
            StopReason.Aborted => "aborted",
            _ => "stop"
        };
    }
}
