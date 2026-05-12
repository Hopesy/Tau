using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tau.Mom;

public sealed record SlackUserSnapshot(string Id, string? UserName = null, string? DisplayName = null);

public static class SlackEventMapper
{
    public static MomChannelMessage? MapSocketModeEvent(
        string json,
        string? botUserId = null,
        IReadOnlyDictionary<string, SlackUserSnapshot>? users = null,
        string? provider = null,
        string? model = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return MapSocketModeEvent(document.RootElement, botUserId, users, provider, model);
    }

    public static MomChannelMessage? MapSocketModeEvent(
        JsonElement payload,
        string? botUserId = null,
        IReadOnlyDictionary<string, SlackUserSnapshot>? users = null,
        string? provider = null,
        string? model = null)
    {
        var eventElement = TryGetObject(payload, "event", out var nestedEvent)
            ? nestedEvent
            : TryGetObject(payload, "payload", out var nestedPayload) && TryGetObject(nestedPayload, "event", out var payloadEvent)
                ? payloadEvent
                : payload;
        var eventType = GetString(eventElement, "type");
        return eventType switch
        {
            "app_mention" => MapAppMention(eventElement, users, provider, model),
            "message" => MapMessage(eventElement, botUserId, users, provider, model),
            _ => null
        };
    }

    public static string StripSlackMentions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return Regex.Replace(text, @"<@[A-Z0-9]+>", string.Empty, RegexOptions.IgnoreCase).Trim();
    }

    private static MomChannelMessage? MapAppMention(
        JsonElement eventElement,
        IReadOnlyDictionary<string, SlackUserSnapshot>? users,
        string? provider,
        string? model)
    {
        var channel = GetString(eventElement, "channel");
        var user = GetString(eventElement, "user");
        var ts = GetString(eventElement, "ts");
        if (string.IsNullOrWhiteSpace(channel) ||
            string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(ts) ||
            channel.StartsWith("D", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = StripSlackMentions(GetString(eventElement, "text"));
        var files = BuildAttachments(eventElement);
        if (string.IsNullOrWhiteSpace(text) && files is null)
        {
            return null;
        }

        return BuildMessage(
            channel,
            text,
            ts,
            user,
            files,
            users,
            GetString(eventElement, "thread_ts"),
            provider,
            model,
            "mention");
    }

    private static MomChannelMessage? MapMessage(
        JsonElement eventElement,
        string? botUserId,
        IReadOnlyDictionary<string, SlackUserSnapshot>? users,
        string? provider,
        string? model)
    {
        var channel = GetString(eventElement, "channel");
        var user = GetString(eventElement, "user");
        var ts = GetString(eventElement, "ts");
        if (string.IsNullOrWhiteSpace(channel) ||
            string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(ts) ||
            HasNonEmptyString(eventElement, "bot_id") ||
            IsSameUser(user, botUserId))
        {
            return null;
        }

        var subtype = GetString(eventElement, "subtype");
        if (!string.IsNullOrWhiteSpace(subtype) && !string.Equals(subtype, "file_share", StringComparison.Ordinal))
        {
            return null;
        }

        var rawText = GetString(eventElement, "text") ?? string.Empty;
        var files = BuildAttachments(eventElement);
        if (string.IsNullOrWhiteSpace(rawText) && files is null)
        {
            return null;
        }

        var isDm = string.Equals(GetString(eventElement, "channel_type"), "im", StringComparison.Ordinal);
        var mentionsBot = !string.IsNullOrWhiteSpace(botUserId) &&
            rawText.Contains($"<@{botUserId.Trim()}>", StringComparison.OrdinalIgnoreCase);
        if (!isDm)
        {
            return null;
        }

        var text = StripSlackMentions(rawText);
        return BuildMessage(
            channel,
            text,
            ts,
            user,
            files,
            users,
            GetString(eventElement, "thread_ts"),
            provider,
            model,
            mentionsBot ? "dm_mention" : "dm");
    }

    private static MomChannelMessage BuildMessage(
        string channel,
        string text,
        string ts,
        string user,
        IReadOnlyList<MomChannelAttachment>? attachments,
        IReadOnlyDictionary<string, SlackUserSnapshot>? users,
        string? threadTs,
        string? provider,
        string? model,
        string slackEventType)
    {
        SlackUserSnapshot? userSnapshot = null;
        if (users is not null)
        {
            users.TryGetValue(user, out userSnapshot);
        }
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "slack",
            ["slackEventType"] = slackEventType
        };

        if (attachments is not null && attachments.Count > 0)
        {
            metadata["attachmentCount"] = attachments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new MomChannelMessage(
            channel.Trim(),
            text.Trim(),
            ts.Trim(),
            user.Trim(),
            attachments,
            Normalize(userSnapshot?.UserName),
            Normalize(userSnapshot?.DisplayName),
            Normalize(threadTs),
            Normalize(provider),
            Normalize(model),
            Title: null,
            metadata);
    }

    private static IReadOnlyList<MomChannelAttachment>? BuildAttachments(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var attachments = new List<MomChannelAttachment>();
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(file, "name") ?? GetString(file, "title") ?? GetString(file, "id") ?? "slack-file";
            var url = GetString(file, "url_private_download") ?? GetString(file, "url_private");
            attachments.Add(new MomChannelAttachment(name.Trim(), Local: null, Url: Normalize(url)));
        }

        return attachments.Count == 0 ? null : attachments;
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? Normalize(property.GetString()) : null;
    }

    private static bool HasNonEmptyString(JsonElement element, string propertyName)
    {
        return !string.IsNullOrWhiteSpace(GetString(element, propertyName));
    }

    private static bool IsSameUser(string user, string? otherUser)
    {
        return !string.IsNullOrWhiteSpace(otherUser) &&
            string.Equals(user.Trim(), otherUser.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
