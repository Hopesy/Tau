namespace Tau.Mom;

public sealed record DelegationResult(
    string RequestFile,
    string Prompt,
    string Response,
    IReadOnlyList<string> ToolEvents,
    string? Error,
    string Provider,
    string Model,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Metadata,
    DateTimeOffset ProcessedAt,
    TimeSpan Duration);
