using System.Net.Http.Headers;

namespace Tau.Mom;

public sealed class SlackAttachmentDownloader
{
    private readonly MomOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SlackAttachmentDownloader> _logger;

    public SlackAttachmentDownloader(
        MomOptions options,
        ILogger<SlackAttachmentDownloader> logger,
        HttpClient? httpClient = null)
    {
        _options = options;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<MomChannelMessage> DownloadAttachmentsAsync(
        MomChannelMessage message,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (message.Attachments is null || message.Attachments.Count == 0)
        {
            return message;
        }

        var downloaded = new List<MomChannelAttachment>();
        foreach (var attachment in message.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.Url))
            {
                downloaded.Add(attachment);
                continue;
            }

            if (string.IsNullOrWhiteSpace(_options.SlackBotToken))
            {
                _logger.LogWarning("Skipping Slack attachment download for {Original}; Slack bot token is not configured.", attachment.Original);
                downloaded.Add(attachment);
                continue;
            }

            try
            {
                var local = await DownloadAttachmentAsync(
                        attachment,
                        workingDirectory,
                        message.Ts,
                        cancellationToken)
                    .ConfigureAwait(false);
                downloaded.Add(attachment with { Local = local });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Failed to download Slack attachment {Original}.", attachment.Original);
                downloaded.Add(attachment);
            }
        }

        return message with { Attachments = downloaded };
    }

    private async Task<string> DownloadAttachmentAsync(
        MomChannelAttachment attachment,
        string workingDirectory,
        string ts,
        CancellationToken cancellationToken)
    {
        var workingDirectoryFullPath = Path.GetFullPath(workingDirectory);
        var attachmentsDirectory = Path.Combine(workingDirectoryFullPath, "attachments");
        Directory.CreateDirectory(attachmentsDirectory);

        var timestamp = ResolveTimestamp(ts);
        var safeOriginal = MakeSafeFileName(attachment.Original);
        var destinationPath = GetAvailableAttachmentPath(attachmentsDirectory, timestamp, safeOriginal);

        using var request = new HttpRequestMessage(HttpMethod.Get, attachment.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackBotToken!.Trim());
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Slack attachment download returned HTTP {(int)response.StatusCode}.");
        }

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = File.Create(destinationPath))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        var local = ToLocalPath(Path.GetRelativePath(workingDirectoryFullPath, destinationPath));
        ChannelAttachmentStore.RecordAttachment(
            workingDirectoryFullPath,
            attachment.Original,
            local,
            attachment.Url,
            DateTimeOffset.UtcNow,
            _logger);
        return local;
    }

    private static long ResolveTimestamp(string ts)
    {
        if (ts.Contains('.', StringComparison.Ordinal) &&
            double.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var slackSeconds))
        {
            return (long)Math.Floor(slackSeconds * 1000);
        }

        return long.TryParse(ts, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var epochMilliseconds)
            ? epochMilliseconds
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string GetAvailableAttachmentPath(string attachmentsDirectory, long timestamp, string safeOriginal)
    {
        var candidate = Path.Combine(attachmentsDirectory, $"{timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{safeOriginal}");
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
                $"{timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{name}_{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string MakeSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "attachment";
        }

        var chars = value.Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')
            .ToArray();
        var safe = new string(chars).Trim('_');
        return safe.Length == 0 ? "attachment" : safe;
    }

    private static string ToLocalPath(string value)
    {
        return value.Replace('\\', '/');
    }
}
