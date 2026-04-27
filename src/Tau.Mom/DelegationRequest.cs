namespace Tau.Mom;

public sealed record DelegationRequest(
    string Prompt,
    string? Provider = null,
    string? Model = null,
    string? WorkingDirectory = null,
    string? Title = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
