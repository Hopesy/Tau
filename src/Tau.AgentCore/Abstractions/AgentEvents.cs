namespace Tau.AgentCore;

/// <summary>
/// Agent events forming a three-level lifecycle:
///   agent_start → (turn_start → message/tool events → turn_end)* → agent_end
/// </summary>
public abstract record AgentEvent(string Type);

public sealed record AgentStartEvent : AgentEvent
{
    public AgentStartEvent() : base("agent_start") { }
}

public sealed record AgentEndEvent : AgentEvent
{
    public AgentEndEvent(
        string? errorMessage = null,
        IReadOnlyList<Ai.ChatMessage>? messages = null)
        : base("agent_end")
    {
        ErrorMessage = errorMessage;
        Messages = messages ?? [];
    }

    public string? ErrorMessage { get; init; }
    public IReadOnlyList<Ai.ChatMessage> Messages { get; init; }
}

public sealed record TurnStartEvent(int TurnIndex) : AgentEvent("turn_start");
public sealed record TurnEndEvent : AgentEvent
{
    public TurnEndEvent(
        int turnIndex,
        Ai.ChatMessage? message = null,
        IReadOnlyList<Ai.ToolResultMessage>? toolResults = null)
        : base("turn_end")
    {
        TurnIndex = turnIndex;
        Message = message;
        ToolResults = toolResults ?? [];
    }

    public int TurnIndex { get; init; }
    public Ai.ChatMessage? Message { get; init; }
    public IReadOnlyList<Ai.ToolResultMessage> ToolResults { get; init; }
}

public sealed record MessageStartEvent(Ai.ChatMessage Message) : AgentEvent("message_start")
{
    public Ai.ChatMessage Partial => Message;
}

public sealed record MessageUpdateEvent(Ai.StreamEvent StreamEvent, Ai.ChatMessage? Message = null) : AgentEvent("message_update");
public sealed record MessageEndEvent(Ai.ChatMessage Message) : AgentEvent("message_end");

public sealed record ToolExecutionStartEvent(
    string ToolCallId,
    string ToolName,
    string? Args = null) : AgentEvent("tool_execution_start");

public sealed record ToolExecutionUpdateEvent(
    string ToolCallId,
    ToolUpdate Update,
    string? ToolName = null,
    string? Args = null,
    ToolResult? PartialResult = null) : AgentEvent("tool_execution_update");

public sealed record ToolExecutionEndEvent : AgentEvent
{
    public ToolExecutionEndEvent(
        string toolCallId,
        ToolResult result,
        string? toolName = null,
        bool? isError = null)
        : base("tool_execution_end")
    {
        ToolCallId = toolCallId;
        Result = result;
        ToolName = toolName;
        IsError = isError ?? result.IsError;
    }

    public string ToolCallId { get; init; }
    public ToolResult Result { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
}
