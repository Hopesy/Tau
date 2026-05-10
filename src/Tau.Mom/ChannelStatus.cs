namespace Tau.Mom;

public sealed record ChannelStatus(
    string State,
    string RequestFile,
    string Provider,
    string Model,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? Title = null,
    string? PromptPreview = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<string>? Attachments = null,
    DateTimeOffset? CompletedAt = null,
    long? DurationMs = null,
    string? StopReason = null,
    string? Error = null,
    string? ResponsePreview = null);
