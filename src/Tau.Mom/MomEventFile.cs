namespace Tau.Mom;

public sealed record MomEventFile(
    string Type,
    string ChannelId,
    string Text,
    string? At = null,
    string? Schedule = null,
    string? Timezone = null,
    string? Provider = null,
    string? Model = null,
    string? Title = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<string>? Attachments = null);
