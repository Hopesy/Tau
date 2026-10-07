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
    /// <summary>【CodingAgent】【回合恢复】会话层是否计划重试此失败请求，核心 Agent 默认不重试。</summary>
    public bool WillRetry { get; init; }
    /// <summary>本次运行新增的消息，包括输入、工具声明、助手及工具结果；完整历史从 Agent.State.Messages 读取。</summary>
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
    string? Args = null) : AgentEvent("tool_execution_start")
{
    /// <summary>【AgentCore】【嵌套调用】直接父工具调用标识。</summary>
    public string? ParentToolCallId { get; init; }
}

public sealed record ToolExecutionUpdateEvent(
    string ToolCallId,
    ToolUpdate Update,
    string? ToolName = null,
    string? Args = null,
    ToolResult? PartialResult = null) : AgentEvent("tool_execution_update")
{
    /// <summary>【AgentCore】【嵌套调用】直接父工具调用标识。</summary>
    public string? ParentToolCallId { get; init; }
}

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
    /// <summary>【AgentCore】【嵌套调用】直接父工具调用标识。</summary>
    public string? ParentToolCallId { get; init; }
}
