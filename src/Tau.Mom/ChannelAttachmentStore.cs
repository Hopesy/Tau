using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tau.Mom;

internal static class ChannelAttachmentStore
{
    private const string AttachmentsDirectoryName = "attachments";
    private const string ManifestFileName = "attachments.jsonl";

    public static IReadOnlyList<string>? StageRequestAttachments(
        string workingDirectory,
        IReadOnlyList<string>? attachments,
        IReadOnlyDictionary<string, string>? metadata,
        DateTimeOffset now,
        ILogger? logger = null)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return null;
        }

        var workingDirectoryFullPath = Path.GetFullPath(workingDirectory);
        var timestamp = ResolveTimestamp(metadata, now);
        var staged = new List<string>();

        foreach (var attachment in attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment))
            {
                continue;
            }

            var trimmed = attachment.Trim();
            if (!TryResolvePath(trimmed, workingDirectoryFullPath, out var sourcePath))
            {
                staged.Add(trimmed);
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                staged.Add(NormalizeAttachmentPathForRunner(trimmed, sourcePath, workingDirectoryFullPath));
                continue;
            }

            if (IsInsideAttachmentsDirectory(sourcePath, workingDirectoryFullPath))
            {
                var local = ToLocalPath(Path.GetRelativePath(workingDirectoryFullPath, sourcePath));
                if (!ReadManifest(workingDirectoryFullPath).ContainsKey(local))
                {
                    AppendManifestEntry(
                        workingDirectoryFullPath,
                        new ChannelAttachmentEntry(
                            now.ToString("O", CultureInfo.InvariantCulture),
                            Path.GetFileName(sourcePath),
                            local,
                            sourcePath),
                        logger);
                }

                staged.Add(local);
                continue;
            }

            try
            {
                var local = StageExistingFile(workingDirectoryFullPath, sourcePath, timestamp, now, logger);
                staged.Add(local);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Failed to stage mom attachment {Attachment}.", sourcePath);
                staged.Add(sourcePath);
            }
        }

        return staged.Count == 0 ? null : staged;
    }


    public static void RecordAttachment(
        string workingDirectory,
        string original,
        string local,
        string? source,
        DateTimeOffset now,
        ILogger? logger = null)
    {
        AppendManifestEntry(
            Path.GetFullPath(workingDirectory),
            new ChannelAttachmentEntry(
                now.ToString("O", CultureInfo.InvariantCulture),
                original,
                ToLocalPath(local),
                source),
            logger);
    }
    public static IReadOnlyList<ChannelLogAttachment> BuildLogAttachments(
        string workingDirectory,
        IReadOnlyList<string>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return [];
        }

        var workingDirectoryFullPath = Path.GetFullPath(workingDirectory);
        var manifest = ReadManifest(workingDirectoryFullPath);
        var result = new List<ChannelLogAttachment>();

        foreach (var attachment in attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment))
            {
                continue;
            }

            var local = NormalizeForLog(attachment.Trim(), workingDirectoryFullPath);
            manifest.TryGetValue(local, out var original);
            result.Add(new ChannelLogAttachment(local, original ?? Path.GetFileName(local)));
        }

        return result.Count == 0 ? [] : result;
    }

    private static string StageExistingFile(
        string workingDirectory,
        string sourcePath,
        long timestamp,
        DateTimeOffset now,
        ILogger? logger)
    {
        var attachmentsDirectory = Path.Combine(workingDirectory, AttachmentsDirectoryName);
        Directory.CreateDirectory(attachmentsDirectory);

        var original = Path.GetFileName(sourcePath);
        var safeOriginal = MakeSafeFileName(original);
        var destinationPath = GetAvailableAttachmentPath(attachmentsDirectory, timestamp, safeOriginal);
        File.Copy(sourcePath, destinationPath);

        var local = ToLocalPath(Path.GetRelativePath(workingDirectory, destinationPath));
        AppendManifestEntry(
            workingDirectory,
            new ChannelAttachmentEntry(
                now.ToString("O", CultureInfo.InvariantCulture),
                original,
                local,
                sourcePath),
            logger);
        return local;
    }

    private static string GetAvailableAttachmentPath(string attachmentsDirectory, long timestamp, string safeOriginal)
    {
        var candidate = Path.Combine(attachmentsDirectory, $"{timestamp.ToString(CultureInfo.InvariantCulture)}_{safeOriginal}");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var name = Path.GetFileNameWithoutExtension(safeOriginal);
        var extension = Path.GetExtension(safeOriginal);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(
                attachmentsDirectory,
                $"{timestamp.ToString(CultureInfo.InvariantCulture)}_{name}_{index.ToString(CultureInfo.InvariantCulture)}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void AppendManifestEntry(
        string workingDirectory,
        ChannelAttachmentEntry entry,
        ILogger? logger)
    {
        try
        {
            var attachmentsDirectory = Path.Combine(workingDirectory, AttachmentsDirectoryName);
            Directory.CreateDirectory(attachmentsDirectory);
            var manifestPath = Path.Combine(attachmentsDirectory, ManifestFileName);
            var json = JsonSerializer.Serialize(entry, MomCompactJsonContext.Default.ChannelAttachmentEntry);
            File.AppendAllText(manifestPath, json + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to append mom attachment manifest entry for {Local}.", entry.Local);
        }
    }

    private static Dictionary<string, string> ReadManifest(string workingDirectory)
    {
        var manifestPath = Path.Combine(workingDirectory, AttachmentsDirectoryName, ManifestFileName);
        var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath))
        {
            return manifest;
        }

        try
        {
            foreach (var line in File.ReadLines(manifestPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ChannelAttachmentEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize(line, MomCompactJsonContext.Default.ChannelAttachmentEntry);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (entry is null ||
                    string.IsNullOrWhiteSpace(entry.Local) ||
                    string.IsNullOrWhiteSpace(entry.Original))
                {
                    continue;
                }

                manifest[ToLocalPath(entry.Local.Trim())] = entry.Original.Trim();
            }
        }
        catch
        {
            return [];
        }

        return manifest;
    }

    private static long ResolveTimestamp(IReadOnlyDictionary<string, string>? metadata, DateTimeOffset now)
    {
        var raw = GetMetadataValue(metadata, "ts", "slackTs", "slack_ts", "messageTs", "message_ts");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (raw.Contains('.', StringComparison.Ordinal) &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var slackSeconds))
            {
                return (long)Math.Floor(slackSeconds * 1000);
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMilliseconds))
            {
                return epochMilliseconds;
            }
        }

        return now.ToUnixTimeMilliseconds();
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

    private static bool TryResolvePath(string value, string workingDirectory, out string fullPath)
    {
        try
        {
            fullPath = Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(value, workingDirectory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = value;
            return false;
        }
    }

    private static string NormalizeAttachmentPathForRunner(string original, string fullPath, string workingDirectory)
    {
        if (!Path.IsPathRooted(original))
        {
            return ToLocalPath(original);
        }

        return IsInsideDirectory(fullPath, workingDirectory)
            ? ToLocalPath(Path.GetRelativePath(workingDirectory, fullPath))
            : fullPath;
    }

    private static string NormalizeForLog(string value, string workingDirectory)
    {
        if (TryResolvePath(value, workingDirectory, out var fullPath) && IsInsideDirectory(fullPath, workingDirectory))
        {
            return ToLocalPath(Path.GetRelativePath(workingDirectory, fullPath));
        }

        return ToLocalPath(value);
    }

    private static bool IsInsideAttachmentsDirectory(string path, string workingDirectory)
    {
        return IsInsideDirectory(path, Path.Combine(workingDirectory, AttachmentsDirectoryName));
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = EnsureTrailingSeparator(Path.GetFullPath(directory));
        return fullPath.StartsWith(fullDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string EnsureTrailingSeparator(string value)
    {
        return value.EndsWith(Path.DirectorySeparatorChar) || value.EndsWith(Path.AltDirectorySeparatorChar)
            ? value
            : value + Path.DirectorySeparatorChar;
    }

    private static string MakeSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "attachment";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_');
        }

        var safe = builder.ToString().Trim('_');
        return safe.Length == 0 ? "attachment" : safe;
    }

    private static string ToLocalPath(string value)
    {
        return value.Replace('\\', '/');
    }
}
