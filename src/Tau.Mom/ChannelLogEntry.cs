namespace Tau.Mom;

public sealed record ChannelLogAttachment(string Local, string? Original = null);

public sealed record ChannelLogEntry(
    string Date,
    string Ts,
    string User,
    string Text,
    IReadOnlyList<ChannelLogAttachment> Attachments,
    bool IsBot,
    string? UserName = null,
    string? DisplayName = null);
