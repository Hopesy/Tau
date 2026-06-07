using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.Agent.Platform;

public sealed record AgentRunResult
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required IReadOnlyList<AgentEvent> Events { get; init; }
    public required TauRuntimeLogContext LogContext { get; init; }
    public string? SessionId { get; init; }
    public string? AssistantText { get; init; }
    public Usage? Usage { get; init; }
    public StopReason? StopReason { get; init; }
    public string? ErrorMessage { get; init; }
    public bool SavedSession { get; init; }

    public bool IsCancelled => StopReason == Ai.StopReason.Aborted;
    public bool IsError => !IsCancelled && (!string.IsNullOrWhiteSpace(ErrorMessage) || StopReason == Ai.StopReason.Error);
    public bool IsSuccess => !IsCancelled && !IsError;

    public IReadOnlyList<ToolExecutionStartEvent> ToolStarts =>
        Events.OfType<ToolExecutionStartEvent>().ToArray();

    public IReadOnlyList<ToolExecutionEndEvent> ToolEnds =>
        Events.OfType<ToolExecutionEndEvent>().ToArray();
}
