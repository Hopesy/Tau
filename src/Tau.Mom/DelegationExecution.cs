namespace Tau.Mom;

public sealed record DelegationExecution(
    string Response,
    IReadOnlyList<DelegationToolEvent> ToolEvents,
    string? Error,
    string Provider,
    string Model,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? StopReason = null,
    DelegationUsage? Usage = null,
    IReadOnlyList<string>? Attachments = null);
