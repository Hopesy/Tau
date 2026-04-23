namespace Tau.Agent;

/// <summary>
/// Agent events forming a three-level lifecycle:
///   agent_start → (turn_start → message/tool events → turn_end)* → agent_end
/// </summary>
public abstract record AgentEvent(string Type);

public sealed record AgentStartEvent : AgentEvent
{
    public AgentStartEvent() : base("agent_start") { }
}

public sealed record AgentEndEvent(string? ErrorMessage = null) : AgentEvent("agent_end");

public sealed record TurnStartEvent(int TurnIndex) : AgentEvent("turn_start");
public sealed record TurnEndEvent(int TurnIndex) : AgentEvent("turn_end");

public sealed record MessageStartEvent(Ai.AssistantMessage Partial) : AgentEvent("message_start");
public sealed record MessageUpdateEvent(Ai.StreamEvent StreamEvent) : AgentEvent("message_update");
public sealed record MessageEndEvent(Ai.AssistantMessage Message) : AgentEvent("message_end");

public sealed record ToolExecutionStartEvent(string ToolCallId, string ToolName) : AgentEvent("tool_execution_start");
public sealed record ToolExecutionUpdateEvent(string ToolCallId, ToolUpdate Update) : AgentEvent("tool_execution_update");
public sealed record ToolExecutionEndEvent(string ToolCallId, ToolResult Result) : AgentEvent("tool_execution_end");
