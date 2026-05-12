using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tau.Mom;

internal static class ChannelLogStore
{
    private const int MaxChannelHistoryMessages = 20;

    public static IReadOnlySet<string> ReadTimestamps(string? workingDirectory, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var logPath = Path.Combine(Path.GetFullPath(workingDirectory), "log.jsonl");
        var timestamps = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(logPath))
        {
            return timestamps;
        }

        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = ParseJsonLine(line);
                if (document is null ||
                    document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var ts = TryGetString(document.RootElement, "ts");
                if (!string.IsNullOrWhiteSpace(ts))
                {
                    timestamps.Add(ts.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to read channel log timestamps for working directory {WorkingDirectory}", workingDirectory);
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return timestamps;
    }

    public static async Task<bool> AppendMessageAsync(
        string? workingDirectory,
        ChannelLogEntry entry,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return false;
        }

        try
        {
            var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
            Directory.CreateDirectory(fullWorkingDirectory);
            var logPath = Path.Combine(fullWorkingDirectory, "log.jsonl");
            if (!string.IsNullOrWhiteSpace(entry.Ts) && HasLogEntry(logPath, entry.Ts))
            {
                return false;
            }

            var builder = new StringBuilder();
            AppendJsonLine(builder, entry);
            if (NeedsLeadingNewLine(logPath))
            {
                builder.Insert(0, Environment.NewLine);
            }

            await File.AppendAllTextAsync(logPath, builder.ToString(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to append channel log message for working directory {WorkingDirectory}", workingDirectory);
            return false;
        }
    }

    public static async Task AppendDelegationAsync(
        string? workingDirectory,
        DelegationRequest request,
        DelegationExecution execution,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        try
        {
            var logPath = Path.Combine(workingDirectory, "log.jsonl");
            var requestTs = GetMetadataValue(request.Metadata, "ts", "slackTs", "slack_ts", "messageTs", "message_ts")
                ?? startedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var requestDate = GetMetadataValue(request.Metadata, "date")
                ?? startedAt.ToString("O", CultureInfo.InvariantCulture);
            var user = GetMetadataValue(request.Metadata, "user", "userId", "user_id") ?? "local";
            var userName = GetMetadataValue(request.Metadata, "userName", "username");
            var displayName = GetMetadataValue(request.Metadata, "displayName", "display_name");
            var attachments = ChannelAttachmentStore.BuildLogAttachments(workingDirectory, request.Attachments);

            var builder = new StringBuilder();
            if (!HasLogEntry(logPath, requestTs, isBot: false))
            {
                AppendJsonLine(builder, new ChannelLogEntry(
                    requestDate,
                    requestTs,
                    user,
                    request.Prompt,
                    attachments,
                    false,
                    userName,
                    displayName));
            }

            var botText = !string.IsNullOrWhiteSpace(execution.Response)
                ? execution.Response
                : string.IsNullOrWhiteSpace(execution.Error) ? null : $"Error: {execution.Error}";
            if (!string.IsNullOrWhiteSpace(botText))
            {
                var completedAt = DateTimeOffset.UtcNow;
                var botTs = $"{completedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}-bot";
                AppendJsonLine(builder, new ChannelLogEntry(
                    completedAt.ToString("O", CultureInfo.InvariantCulture),
                    botTs,
                    "bot",
                    botText,
                    [],
                    true));
            }

            if (builder.Length > 0)
            {
                if (NeedsLeadingNewLine(logPath))
                {
                    builder.Insert(0, Environment.NewLine);
                }

                await File.AppendAllTextAsync(logPath, builder.ToString(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to append channel log for working directory {WorkingDirectory}", workingDirectory);
        }
    }

    public static string? BuildHistory(string? workingDirectory, IReadOnlyDictionary<string, string>? metadata)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var logPath = Path.Combine(Path.GetFullPath(workingDirectory), "log.jsonl");
        if (!File.Exists(logPath))
        {
            return null;
        }

        var excludeTs = GetCurrentMessageTimestamp(metadata);
        var messages = new List<ChannelLogMessage>();
        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                var message = TryParseChannelLogMessage(line, excludeTs);
                if (message is not null)
                {
                    messages.Add(message);
                }
            }
        }
        catch
        {
            return null;
        }

        if (messages.Count == 0)
        {
            return null;
        }

        var selected = messages.Count > MaxChannelHistoryMessages
            ? messages.Skip(messages.Count - MaxChannelHistoryMessages)
            : messages;

        var builder = new StringBuilder();
        foreach (var message in selected)
        {
            builder.Append("- ");
            var timestamp = string.IsNullOrWhiteSpace(message.Date) ? message.Ts : message.Date;
            if (!string.IsNullOrWhiteSpace(timestamp))
            {
                builder.Append('[').Append(timestamp).Append("] ");
            }

            builder.Append('[').Append(message.User).Append("]: ").AppendLine(message.Text);
        }

        return builder.ToString();
    }

    private static void AppendJsonLine(StringBuilder builder, ChannelLogEntry entry)
    {
        builder.Append(JsonSerializer.Serialize(entry, MomCompactJsonContext.Default.ChannelLogEntry));
        builder.AppendLine();
    }

    private static bool HasLogEntry(string logPath, string ts, bool isBot)
    {
        if (!File.Exists(logPath))
        {
            return false;
        }

        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = ParseJsonLine(line);
                if (document is null)
                {
                    continue;
                }

                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!StringEquals(root, "ts", ts))
                {
                    continue;
                }

                if (TryGetBoolean(root, "isBot") == isBot)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool HasLogEntry(string logPath, string ts)
    {
        if (!File.Exists(logPath))
        {
            return false;
        }

        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = ParseJsonLine(line);
                if (document is null)
                {
                    continue;
                }

                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && StringEquals(root, "ts", ts))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool NeedsLeadingNewLine(string logPath)
    {
        try
        {
            var file = new FileInfo(logPath);
            if (!file.Exists || file.Length == 0)
            {
                return false;
            }

            using var stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                return false;
            }

            stream.Seek(-1, SeekOrigin.End);
            var lastByte = stream.ReadByte();
            return lastByte is not '\n' and not '\r';
        }
        catch
        {
            return false;
        }
    }

    private static JsonDocument? ParseJsonLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static bool StringEquals(JsonElement element, string propertyName, string expected)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        var value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
        return string.Equals(value, expected, StringComparison.Ordinal);
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static ChannelLogMessage? TryParseChannelLogMessage(string line, string? excludeTs)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetBoolean(root, "isBot"))
            {
                return null;
            }

            var ts = TryGetString(root, "ts");
            if (!string.IsNullOrWhiteSpace(excludeTs) && string.Equals(ts, excludeTs, StringComparison.Ordinal))
            {
                return null;
            }

            var text = NormalizeChannelText(TryGetString(root, "text"));
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var user = TryGetString(root, "userName");
            if (string.IsNullOrWhiteSpace(user))
            {
                user = TryGetString(root, "user");
            }

            return new ChannelLogMessage(
                TryGetString(root, "date"),
                ts,
                string.IsNullOrWhiteSpace(user) ? "unknown" : user.Trim(),
                text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetCurrentMessageTimestamp(IReadOnlyDictionary<string, string>? metadata)
    {
        return GetMetadataValue(metadata, "ts", "slackTs", "slack_ts", "messageTs", "message_ts");
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static string? NormalizeChannelText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
        var attachmentsIndex = normalized.IndexOf("\n\n<slack_attachments>\n", StringComparison.Ordinal);
        if (attachmentsIndex >= 0)
        {
            normalized = normalized[..attachmentsIndex].Trim();
        }

        return string.Join(" ", normalized
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record ChannelLogMessage(string? Date, string? Ts, string User, string Text);
}
