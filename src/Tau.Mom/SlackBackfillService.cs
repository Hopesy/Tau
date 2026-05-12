using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tau.Mom;

public sealed class SlackBackfillService
{
    private readonly MomOptions _options;
    private readonly SlackAttachmentDownloader _attachmentDownloader;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SlackBackfillService> _logger;
    private string? _botUserId;

    public SlackBackfillService(
        MomOptions options,
        SlackAttachmentDownloader attachmentDownloader,
        ILogger<SlackBackfillService> logger,
        HttpClient? httpClient = null)
    {
        _options = options;
        _attachmentDownloader = attachmentDownloader;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(EnsureTrailingSlash(options.SlackApiBaseUrl), UriKind.Absolute);
    }

    public async Task<int> BackfillExistingChannelsAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.SlackBackfillEnabled)
        {
            _logger.LogInformation("Slack startup backfill disabled.");
            return 0;
        }

        var root = Path.GetFullPath(_options.DefaultWorkingDirectory);
        if (!Directory.Exists(root))
        {
            _logger.LogInformation("Skipping Slack startup backfill; working directory {WorkingDirectory} does not exist.", root);
            return 0;
        }

        var botUserId = await ResolveBotUserIdAsync(cancellationToken).ConfigureAwait(false);
        var total = 0;
        foreach (var channelDirectory in Directory.EnumerateDirectories(root))
        {
            var channelId = Path.GetFileName(channelDirectory);
            if (string.IsNullOrWhiteSpace(channelId) ||
                !File.Exists(Path.Combine(channelDirectory, "log.jsonl")))
            {
                continue;
            }

            try
            {
                var count = await BackfillChannelAsync(channelId, botUserId, cancellationToken).ConfigureAwait(false);
                if (count > 0)
                {
                    _logger.LogInformation("Backfilled {Count} Slack message(s) for {ChannelId}.", count, channelId);
                }

                total += count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to backfill Slack channel {ChannelId}.", channelId);
            }
        }

        _logger.LogInformation("Slack startup backfill completed with {Count} message(s).", total);
        return total;
    }

    public async Task<int> BackfillChannelAsync(
        string channelId,
        string? botUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return 0;
        }

        botUserId ??= await ResolveBotUserIdAsync(cancellationToken).ConfigureAwait(false);
        var workingDirectory = MomChannelWorkspace.ResolveWorkingDirectory(_options.DefaultWorkingDirectory, channelId);
        if (!File.Exists(Path.Combine(workingDirectory, "log.jsonl")))
        {
            return 0;
        }

        var existingTimestamps = ChannelLogStore.ReadTimestamps(workingDirectory, _logger);
        var latestTs = ResolveLatestTimestamp(existingTimestamps);
        var allMessages = new List<SlackBackfillMessage>();
        string? cursor = null;
        var pageCount = 0;
        var maxPages = Math.Max(1, _options.SlackBackfillMaxPages);

        do
        {
            var page = await FetchHistoryPageAsync(
                    channelId.Trim(),
                    latestTs,
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);
            allMessages.AddRange(page.Messages);
            cursor = page.NextCursor;
            pageCount++;
        }
        while (!string.IsNullOrWhiteSpace(cursor) && pageCount < maxPages);

        var relevant = allMessages
            .Where(message => IsRelevant(message, botUserId, existingTimestamps))
            .Reverse()
            .ToArray();

        var appended = 0;
        foreach (var message in relevant)
        {
            var isBot = IsSameUser(message.User, botUserId);
            var text = SlackEventMapper.StripSlackMentions(message.Text);
            var downloaded = await _attachmentDownloader
                .DownloadAttachmentsAsync(
                    new MomChannelMessage(
                        channelId.Trim(),
                        text,
                        message.Ts!,
                        message.User ?? "bot",
                        message.Attachments),
                    workingDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            var entry = new ChannelLogEntry(
                ResolveDate(message.Ts!),
                message.Ts!,
                isBot ? "bot" : message.User!.Trim(),
                text,
                BuildLogAttachments(downloaded.Attachments),
                isBot);
            if (await ChannelLogStore.AppendMessageAsync(workingDirectory, entry, cancellationToken, _logger)
                    .ConfigureAwait(false))
            {
                appended++;
            }
        }

        return appended;
    }

    private async Task<SlackHistoryPage> FetchHistoryPageAsync(
        string channelId,
        string? oldest,
        string? cursor,
        CancellationToken cancellationToken)
    {
        EnsureBotToken();
        var fields = new List<KeyValuePair<string, string>>
        {
            new("channel", channelId),
            new("inclusive", "false"),
            new("limit", Math.Max(1, _options.SlackBackfillPageSize).ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(oldest))
        {
            fields.Add(new KeyValuePair<string, string>("oldest", oldest.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            fields.Add(new KeyValuePair<string, string>("cursor", cursor.Trim()));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "conversations.history")
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackBotToken!.Trim());
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);

        var messages = new List<SlackBackfillMessage>();
        if (root.TryGetProperty("messages", out var messagesElement) &&
            messagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var messageElement in messagesElement.EnumerateArray())
            {
                if (messageElement.ValueKind == JsonValueKind.Object)
                {
                    messages.Add(ParseMessage(messageElement));
                }
            }
        }

        string? nextCursor = null;
        if (root.TryGetProperty("response_metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("next_cursor", out var nextCursorElement) &&
            nextCursorElement.ValueKind == JsonValueKind.String)
        {
            nextCursor = string.IsNullOrWhiteSpace(nextCursorElement.GetString())
                ? null
                : nextCursorElement.GetString()!.Trim();
        }

        return new SlackHistoryPage(messages, nextCursor);
    }

    private async Task<string?> ResolveBotUserIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_botUserId))
        {
            return _botUserId;
        }

        EnsureBotToken();
        using var request = new HttpRequestMessage(HttpMethod.Post, "auth.test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackBotToken!.Trim());
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);
        if (root.TryGetProperty("user_id", out var userId) &&
            userId.ValueKind == JsonValueKind.String)
        {
            _botUserId = userId.GetString();
        }

        return _botUserId;
    }

    private static SlackBackfillMessage ParseMessage(JsonElement element)
    {
        return new SlackBackfillMessage(
            GetString(element, "user"),
            GetString(element, "bot_id"),
            GetString(element, "text"),
            GetString(element, "ts"),
            GetString(element, "subtype"),
            BuildAttachments(element));
    }

    private static IReadOnlyList<MomChannelAttachment>? BuildAttachments(JsonElement element)
    {
        if (!element.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
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
            attachments.Add(new MomChannelAttachment(name.Trim(), Url: Normalize(url)));
        }

        return attachments.Count == 0 ? null : attachments;
    }

    private static bool IsRelevant(
        SlackBackfillMessage message,
        string? botUserId,
        IReadOnlySet<string> existingTimestamps)
    {
        if (string.IsNullOrWhiteSpace(message.Ts) || existingTimestamps.Contains(message.Ts.Trim()))
        {
            return false;
        }

        if (IsSameUser(message.User, botUserId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(message.BotId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(message.Subtype) &&
            !string.Equals(message.Subtype, "file_share", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(message.User))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(message.Text) ||
            (message.Attachments is not null && message.Attachments.Count > 0);
    }

    private static IReadOnlyList<ChannelLogAttachment> BuildLogAttachments(
        IReadOnlyList<MomChannelAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return [];
        }

        var result = new List<ChannelLogAttachment>();
        foreach (var attachment in attachments)
        {
            if (!string.IsNullOrWhiteSpace(attachment.Local))
            {
                result.Add(new ChannelLogAttachment(attachment.Local.Trim(), attachment.Original));
            }
        }

        return result.Count == 0 ? [] : result;
    }

    private static string? ResolveLatestTimestamp(IReadOnlySet<string> timestamps)
    {
        string? latest = null;
        double latestValue = 0;
        foreach (var timestamp in timestamps)
        {
            if (!TryParseSlackTimestamp(timestamp, out var raw, out var value))
            {
                continue;
            }

            if (latest is null || value > latestValue)
            {
                latest = raw;
                latestValue = value;
            }
        }

        return latest;
    }

    private static bool TryParseSlackTimestamp(string timestamp, out string raw, out double value)
    {
        raw = timestamp.Trim();
        var suffixIndex = raw.IndexOf('-', StringComparison.Ordinal);
        if (suffixIndex > 0)
        {
            raw = raw[..suffixIndex];
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string ResolveDate(string ts)
    {
        if (TryParseSlackTimestamp(ts, out _, out var slackSeconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(slackSeconds * 1000))
                .ToString("O", CultureInfo.InvariantCulture);
        }

        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private static bool IsSameUser(string? user, string? otherUser)
    {
        return !string.IsNullOrWhiteSpace(user) &&
            !string.IsNullOrWhiteSpace(otherUser) &&
            string.Equals(user.Trim(), otherUser.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureBotToken()
    {
        if (string.IsNullOrWhiteSpace(_options.SlackBotToken))
        {
            throw new InvalidOperationException("Mom Slack bot token is not configured.");
        }
    }

    private static async Task<JsonElement> EnsureSlackOkAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Slack Web API returned HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ok", out var ok) ||
            ok.ValueKind != JsonValueKind.True)
        {
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errorProperty)
                ? errorProperty.GetString()
                : null;
            throw new InvalidOperationException($"Slack Web API returned ok=false{(string.IsNullOrWhiteSpace(error) ? string.Empty : $": {error}")}.");
        }

        return root;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? Normalize(property.GetString()) : null;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string EnsureTrailingSlash(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "https://slack.com/api/" : value.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
    }

    private sealed record SlackBackfillMessage(
        string? User,
        string? BotId,
        string? Text,
        string? Ts,
        string? Subtype,
        IReadOnlyList<MomChannelAttachment>? Attachments);

    private sealed record SlackHistoryPage(IReadOnlyList<SlackBackfillMessage> Messages, string? NextCursor);
}
