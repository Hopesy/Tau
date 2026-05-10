namespace Tau.Mom;

public sealed record DelegationResult(
    string RequestFile,
    string Prompt,
    string Response,
    IReadOnlyList<DelegationToolEvent> ToolEvents,
    string? Error,
    string Provider,
    string Model,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Metadata,
    DateTimeOffset ProcessedAt,
    TimeSpan Duration,
    string? StopReason = null,
    DelegationUsage? Usage = null,
    string? Title = null,
    IReadOnlyList<string>? Attachments = null);
