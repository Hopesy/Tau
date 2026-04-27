namespace Tau.Mom;

public sealed record DelegationExecution(
    string Response,
    IReadOnlyList<string> ToolEvents,
    string? Error,
    string Provider,
    string Model,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Metadata = null);
