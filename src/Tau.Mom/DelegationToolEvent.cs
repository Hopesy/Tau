namespace Tau.Mom;

public sealed record DelegationToolEvent(
    string Phase,
    string ToolName,
    string ToolCallId,
    bool? IsError = null,
    long? DurationMs = null);
