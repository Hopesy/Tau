using System.Globalization;

namespace Tau.Mom;

public sealed record MomChannelAttachment(
    string Original,
    string? Local = null,
    string? Url = null)
{
    public string ToRequestAttachment()
    {
        return string.IsNullOrWhiteSpace(Local) ? Original.Trim() : Local.Trim();
    }
}

public sealed record MomChannelMessage(
    string ChannelId,
    string Text,
    string Ts,
    string User,
    IReadOnlyList<MomChannelAttachment>? Attachments = null,
    string? UserName = null,
    string? DisplayName = null,
    string? ThreadTs = null,
    string? Provider = null,
    string? Model = null,
    string? Title = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public DelegationRequest ToDelegationRequest(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new InvalidOperationException("Channel message text is required.");
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new InvalidOperationException("Channel message working directory is required.");
        }

        var metadata = BuildMetadata();
        var attachments = BuildRequestAttachments();

        return new DelegationRequest(
            Text.Trim(),
            NormalizeOptional(Provider),
            NormalizeOptional(Model),
            Path.GetFullPath(workingDirectory),
            NormalizeOptional(Title),
            metadata,
            attachments);
    }

    public static MomChannelMessage FromDelegationRequest(
        DelegationRequest request,
        DateTimeOffset fallbackTimestamp,
        string defaultChannelId = "local")
    {
        var metadata = request.Metadata;
        var ts = GetMetadataValue(metadata, "ts", "slackTs", "slack_ts", "messageTs", "message_ts")
            ?? fallbackTimestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        return new MomChannelMessage(
            GetMetadataValue(metadata, "channel", "channelId", "channel_id") ?? defaultChannelId,
            request.Prompt,
            ts,
            GetMetadataValue(metadata, "user", "userId", "user_id") ?? "local",
            BuildAttachments(request.Attachments),
            GetMetadataValue(metadata, "userName", "username"),
            GetMetadataValue(metadata, "displayName", "display_name"),
            GetMetadataValue(metadata, "threadTs", "thread_ts"),
            request.Provider,
            request.Model,
            request.Title,
            metadata);
    }

    private Dictionary<string, string> BuildMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Metadata is not null)
        {
            foreach (var pair in Metadata)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    metadata[pair.Key.Trim()] = pair.Value.Trim();
                }
            }
        }

        AddDefault(metadata, "channel", ChannelId.Trim());
        AddDefault(metadata, "user", User.Trim());
        AddDefault(metadata, "ts", Ts.Trim());
        AddDefault(metadata, "date", ResolveDate(Ts));

        if (!string.IsNullOrWhiteSpace(UserName))
        {
            AddDefault(metadata, "userName", UserName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            AddDefault(metadata, "displayName", DisplayName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(ThreadTs))
        {
            AddDefault(metadata, "threadTs", ThreadTs.Trim());
        }

        return metadata;
    }

    private IReadOnlyList<string>? BuildRequestAttachments()
    {
        if (Attachments is null || Attachments.Count == 0)
        {
            return null;
        }

        var attachments = Attachments
            .Select(static attachment => attachment.ToRequestAttachment())
            .Where(static attachment => !string.IsNullOrWhiteSpace(attachment))
            .ToArray();

        return attachments.Length == 0 ? null : attachments;
    }

    private static IReadOnlyList<MomChannelAttachment>? BuildAttachments(IReadOnlyList<string>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return null;
        }

        var result = new List<MomChannelAttachment>();
        foreach (var attachment in attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment))
            {
                continue;
            }

            var trimmed = attachment.Trim();
            result.Add(new MomChannelAttachment(Path.GetFileName(trimmed), trimmed));
        }

        return result.Count == 0 ? null : result;
    }

    private static void AddDefault(IDictionary<string, string> metadata, string key, string value)
    {
        if (!metadata.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value;
        }
    }

    private static string ResolveDate(string ts)
    {
        var trimmed = ts.Trim();
        if (trimmed.Contains('.', StringComparison.Ordinal) &&
            double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var slackSeconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(slackSeconds * 1000))
                .ToString("O", CultureInfo.InvariantCulture);
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMilliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds)
                .ToString("O", CultureInfo.InvariantCulture);
        }

        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var exactValue) && !string.IsNullOrWhiteSpace(exactValue))
            {
                return exactValue.Trim();
            }
        }

        foreach (var key in keys)
        {
            foreach (var pair in metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value.Trim();
                }
            }
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
