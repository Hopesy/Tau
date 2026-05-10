using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tau.Mom;

public sealed class MomEventProcessor
{
    private readonly MomOptions _options;
    private readonly ILogger<MomEventProcessor> _logger;
    private readonly Dictionary<string, string> _lastPeriodicDueKeys = new(StringComparer.OrdinalIgnoreCase);

    public MomEventProcessor(MomOptions options, ILogger<MomEventProcessor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> ProcessDueEventsAsync(CancellationToken cancellationToken = default)
    {
        return await ProcessDueEventsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ProcessDueEventsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.EventsPath);
        Directory.CreateDirectory(_options.InboxPath);

        var queued = 0;
        foreach (var file in Directory.EnumerateFiles(_options.EventsPath, "*.json").OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var loaded = await TryLoadEventAsync(file, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                await ArchiveInvalidEventAsync(file, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var (eventFile, deleteAfterEnqueue) = loaded.Value;
            if (!TryCreateRequest(file, eventFile, now, out var request, out var dueKey, out var reason))
            {
                if (reason is not null)
                {
                    _logger.LogDebug("Skipping mom event {File}: {Reason}", file, reason);
                }
                continue;
            }

            if (IsPeriodic(eventFile) && dueKey is not null)
            {
                if (_lastPeriodicDueKeys.TryGetValue(file, out var previousDueKey) &&
                    string.Equals(previousDueKey, dueKey, StringComparison.Ordinal))
                {
                    continue;
                }

                _lastPeriodicDueKeys[file] = dueKey;
            }

            await QueueRequestAsync(file, request, now, cancellationToken).ConfigureAwait(false);
            queued++;

            if (deleteAfterEnqueue)
            {
                TryDelete(file);
            }
        }

        return queued;
    }

    private async Task<(MomEventFile EventFile, bool DeleteAfterEnqueue)?> TryLoadEventAsync(
        string file,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var eventFile = JsonSerializer.Deserialize(json, MomJsonContext.Default.MomEventFile);
            if (eventFile is null ||
                string.IsNullOrWhiteSpace(eventFile.Type) ||
                string.IsNullOrWhiteSpace(eventFile.ChannelId) ||
                string.IsNullOrWhiteSpace(eventFile.Text))
            {
                _logger.LogWarning("Invalid mom event file {File}: missing type, channelId, or text.", file);
                return null;
            }

            var type = NormalizeType(eventFile.Type);
            return type switch
            {
                "immediate" => (eventFile, true),
                "one-shot" => (eventFile, true),
                "periodic" => (eventFile, false),
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Invalid mom event file {File}.", file);
            return null;
        }
    }

    private bool TryCreateRequest(
        string file,
        MomEventFile eventFile,
        DateTimeOffset now,
        out DelegationRequest request,
        out string? dueKey,
        out string? reason)
    {
        request = default!;
        dueKey = null;
        reason = null;

        var type = NormalizeType(eventFile.Type);
        var scheduleInfo = type;
        if (type == "one-shot")
        {
            if (!DateTimeOffset.TryParse(eventFile.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                reason = "invalid one-shot timestamp";
                return false;
            }

            scheduleInfo = at.ToString("O", CultureInfo.InvariantCulture);
            if (at > now)
            {
                reason = "one-shot event is not due";
                return false;
            }
        }
        else if (type == "periodic")
        {
            if (!TryMatchCron(eventFile.Schedule, eventFile.Timezone, now, out var periodicDueKey, out reason))
            {
                return false;
            }

            dueKey = periodicDueKey;
            scheduleInfo = eventFile.Schedule!.Trim();
        }

        var fileName = Path.GetFileName(file);
        var prompt = $"[EVENT:{fileName}:{type}:{scheduleInfo}] {eventFile.Text.Trim()}";
        var workingDirectory = ResolveChannelWorkingDirectory(eventFile.ChannelId);
        Directory.CreateDirectory(workingDirectory);

        var metadata = BuildMetadata(eventFile, fileName, type, now);
        request = new MomChannelMessage(
                eventFile.ChannelId.Trim(),
                prompt,
                metadata["ts"],
                metadata["user"],
                BuildChannelAttachments(eventFile.Attachments),
                metadata.TryGetValue("userName", out var userName) ? userName : null,
                metadata.TryGetValue("displayName", out var displayName) ? displayName : null,
                metadata.TryGetValue("threadTs", out var threadTs) ? threadTs : null,
                string.IsNullOrWhiteSpace(eventFile.Provider) ? _options.DefaultProvider : eventFile.Provider.Trim(),
                string.IsNullOrWhiteSpace(eventFile.Model) ? _options.DefaultModel : eventFile.Model.Trim(),
                string.IsNullOrWhiteSpace(eventFile.Title) ? $"event:{fileName}" : eventFile.Title.Trim(),
                metadata)
            .ToDelegationRequest(workingDirectory);
        return true;
    }

    private async Task QueueRequestAsync(
        string eventFile,
        DelegationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var baseName = Path.GetFileNameWithoutExtension(eventFile);
        var safeBaseName = MakeSafeFileName(baseName);
        var requestFile = Path.Combine(
            _options.InboxPath,
            $"event_{safeBaseName}_{now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}.json");
        var json = JsonSerializer.Serialize(request, MomJsonContext.Default.DelegationRequest);
        await File.WriteAllTextAsync(requestFile, json, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Queued mom event {EventFile} as delegation request {RequestFile}.", eventFile, requestFile);
    }

    private async Task ArchiveInvalidEventAsync(string file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
        {
            return;
        }

        var invalidDir = Path.Combine(_options.ArchivePath, "invalid-events");
        Directory.CreateDirectory(invalidDir);
        var archivePath = Path.Combine(
            invalidDir,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{Path.GetFileName(file)}");

        try
        {
            await Task.Run(() => File.Move(file, archivePath, overwrite: true), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to archive invalid mom event {File}.", file);
        }
    }

    private string ResolveChannelWorkingDirectory(string channelId)
    {
        var safeChannelId = MakeSafeFileName(channelId.Trim());
        return Path.GetFullPath(safeChannelId, Path.GetFullPath(_options.DefaultWorkingDirectory));
    }

    private static Dictionary<string, string> BuildMetadata(
        MomEventFile eventFile,
        string fileName,
        string type,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (eventFile.Metadata is not null)
        {
            foreach (var pair in eventFile.Metadata)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    metadata[pair.Key.Trim()] = pair.Value.Trim();
                }
            }
        }

        AddDefault(metadata, "event", "true");
        AddDefault(metadata, "eventType", type);
        AddDefault(metadata, "eventFile", fileName);
        AddDefault(metadata, "channel", eventFile.ChannelId.Trim());
        AddDefault(metadata, "user", "EVENT");
        AddDefault(metadata, "userName", "event");
        AddDefault(metadata, "ts", now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        AddDefault(metadata, "date", now.ToString("O", CultureInfo.InvariantCulture));
        return metadata;
    }

    private static IReadOnlyList<MomChannelAttachment>? BuildChannelAttachments(IReadOnlyList<string>? attachments)
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
        if (!metadata.ContainsKey(key))
        {
            metadata[key] = value;
        }
    }

    private static string NormalizeType(string type)
    {
        return type.Trim().ToLowerInvariant();
    }

    private static bool IsPeriodic(MomEventFile eventFile)
    {
        return string.Equals(NormalizeType(eventFile.Type), "periodic", StringComparison.Ordinal);
    }

    private static bool TryMatchCron(
        string? schedule,
        string? timezone,
        DateTimeOffset now,
        out string? dueKey,
        out string? reason)
    {
        dueKey = null;
        reason = null;
        if (string.IsNullOrWhiteSpace(schedule))
        {
            reason = "periodic event is missing schedule";
            return false;
        }

        if (string.IsNullOrWhiteSpace(timezone))
        {
            reason = "periodic event is missing timezone";
            return false;
        }

        var fields = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            reason = "periodic event schedule must have five cron fields";
            return false;
        }

        var zone = FindTimeZone(timezone);
        if (zone is null)
        {
            reason = "periodic event timezone is invalid";
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var dayOfWeek = (int)local.DayOfWeek;
        var matches =
            FieldMatches(fields[0], local.Minute, 0, 59) &&
            FieldMatches(fields[1], local.Hour, 0, 23) &&
            FieldMatches(fields[2], local.Day, 1, 31) &&
            FieldMatches(fields[3], local.Month, 1, 12) &&
            FieldMatches(fields[4], dayOfWeek, 0, 6, allowSevenAsSunday: true);

        if (!matches)
        {
            reason = "periodic event is not due";
            return false;
        }

        dueKey = local.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        return true;
    }

    private static TimeZoneInfo? FindTimeZone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static bool FieldMatches(string field, int value, int min, int max, bool allowSevenAsSunday = false)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (PartMatches(part, value, min, max, allowSevenAsSunday))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PartMatches(string part, int value, int min, int max, bool allowSevenAsSunday)
    {
        var step = 1;
        var rangePart = part;
        var slashIndex = part.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex >= 0)
        {
            rangePart = part[..slashIndex];
            if (!int.TryParse(part[(slashIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
            {
                return false;
            }
        }

        int start;
        int end;
        if (rangePart == "*")
        {
            start = min;
            end = max;
        }
        else
        {
            var dashIndex = rangePart.IndexOf('-', StringComparison.Ordinal);
            if (dashIndex >= 0)
            {
                if (!TryParseCronValue(rangePart[..dashIndex], min, max, allowSevenAsSunday, out start) ||
                    !TryParseCronValue(rangePart[(dashIndex + 1)..], min, max, allowSevenAsSunday, out end))
                {
                    return false;
                }
            }
            else if (!TryParseCronValue(rangePart, min, max, allowSevenAsSunday, out start))
            {
                return false;
            }
            else
            {
                end = start;
            }
        }

        if (start > end || value < start || value > end)
        {
            return false;
        }

        return (value - start) % step == 0;
    }

    private static bool TryParseCronValue(string raw, int min, int max, bool allowSevenAsSunday, out int value)
    {
        value = default;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (allowSevenAsSunday && parsed == 7)
        {
            parsed = 0;
        }

        if (parsed < min || parsed > max)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string MakeSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "event";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_');
        }

        var safe = builder.ToString().Trim('_');
        return safe.Length == 0 ? "event" : safe;
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete queued mom event {File}.", file);
        }
    }
}
