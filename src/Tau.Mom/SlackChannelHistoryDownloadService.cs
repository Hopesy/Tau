using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Tau.Mom;

public sealed class SlackChannelHistoryDownloadService
{
    private const int PageSize = 200;

    private readonly MomOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SlackChannelHistoryDownloadService> _logger;

    public SlackChannelHistoryDownloadService(
        MomOptions options,
        ILogger<SlackChannelHistoryDownloadService> logger,
        HttpClient? httpClient = null)
    {
        _options = options;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(EnsureTrailingSlash(options.SlackApiBaseUrl), UriKind.Absolute);
    }

    public async Task<SlackChannelHistoryDownloadResult> DownloadAsync(
        string channelId,
        TextWriter output,
        TextWriter? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel id is required.", nameof(channelId));
        }

        ArgumentNullException.ThrowIfNull(output);
        EnsureBotToken();

        var normalizedChannelId = channelId.Trim();
        await WriteProgressAsync(progress, $"Fetching channel info for {normalizedChannelId}...").ConfigureAwait(false);
        var channelName = await TryResolveChannelNameAsync(normalizedChannelId, cancellationToken).ConfigureAwait(false)
            ?? normalizedChannelId;

        await WriteProgressAsync(progress, $"Downloading history for #{channelName} ({normalizedChannelId})...").ConfigureAwait(false);
        var messages = await FetchAllMessagesAsync(normalizedChannelId, cancellationToken).ConfigureAwait(false);
        messages.Reverse();

        var threadParents = messages
            .Where(static message => message.ReplyCount > 0 && !string.IsNullOrWhiteSpace(message.Ts))
            .ToArray();
        await WriteProgressAsync(progress, $"Fetching {threadParents.Length} threads...").ConfigureAwait(false);
        var threadReplies = new Dictionary<string, IReadOnlyList<SlackDownloadedMessage>>(StringComparer.Ordinal);
        for (var index = 0; index < threadParents.Length; index++)
        {
            var parent = threadParents[index];
            await WriteProgressAsync(progress, $"  Thread {index + 1}/{threadParents.Length} ({parent.ReplyCount} replies)...")
                .ConfigureAwait(false);
            threadReplies[parent.Ts!] = await FetchThreadRepliesAsync(normalizedChannelId, parent.Ts!, cancellationToken)
                .ConfigureAwait(false);
        }

        var totalReplies = 0;
        foreach (var message in messages)
        {
            await output.WriteLineAsync(FormatMessage(message)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(message.Ts) &&
                threadReplies.TryGetValue(message.Ts, out var replies))
            {
                foreach (var reply in replies)
                {
                    await output.WriteLineAsync(FormatMessage(reply, indent: "  ")).ConfigureAwait(false);
                    totalReplies++;
                }
            }
        }

        await WriteProgressAsync(progress, $"Done! {messages.Count} messages, {totalReplies} thread replies")
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Downloaded Slack channel history. channel={ChannelId} messages={MessageCount} replies={ReplyCount}",
            normalizedChannelId,
            messages.Count,
            totalReplies);
        return new SlackChannelHistoryDownloadResult(normalizedChannelId, channelName, messages.Count, totalReplies);
    }

    private async Task<string?> TryResolveChannelNameAsync(
        string channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateSlackPost(
                "conversations.info",
                [new KeyValuePair<string, string>("channel", channelId)]);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var root = await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);
            if (root.TryGetProperty("channel", out var channel) &&
                channel.ValueKind == JsonValueKind.Object &&
                channel.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(name.GetString()))
            {
                return name.GetString()!.Trim();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
        {
            _logger.LogDebug(ex, "Slack channel info lookup failed for {ChannelId}; falling back to channel id.", channelId);
        }

        return null;
    }

    private async Task<List<SlackDownloadedMessage>> FetchAllMessagesAsync(
        string channelId,
        CancellationToken cancellationToken)
    {
        var messages = new List<SlackDownloadedMessage>();
        string? cursor = null;
        do
        {
            var page = await FetchMessagesPageAsync(
                    "conversations.history",
                    [
                        new KeyValuePair<string, string>("channel", channelId),
                        new KeyValuePair<string, string>("limit", PageSize.ToString(CultureInfo.InvariantCulture))
                    ],
                    cursor,
                    skipParent: false,
                    cancellationToken)
                .ConfigureAwait(false);
            messages.AddRange(page.Messages);
            cursor = page.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return messages;
    }

    private async Task<IReadOnlyList<SlackDownloadedMessage>> FetchThreadRepliesAsync(
        string channelId,
        string parentTs,
        CancellationToken cancellationToken)
    {
        var replies = new List<SlackDownloadedMessage>();
        string? cursor = null;
        do
        {
            var page = await FetchMessagesPageAsync(
                    "conversations.replies",
                    [
                        new KeyValuePair<string, string>("channel", channelId),
                        new KeyValuePair<string, string>("ts", parentTs),
                        new KeyValuePair<string, string>("limit", PageSize.ToString(CultureInfo.InvariantCulture))
                    ],
                    cursor,
                    skipParent: true,
                    cancellationToken)
                .ConfigureAwait(false);
            replies.AddRange(page.Messages);
            cursor = page.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return replies;
    }

    private async Task<SlackDownloadMessagePage> FetchMessagesPageAsync(
        string method,
        IReadOnlyList<KeyValuePair<string, string>> fields,
        string? cursor,
        bool skipParent,
        CancellationToken cancellationToken)
    {
        var requestFields = new List<KeyValuePair<string, string>>(fields);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            requestFields.Add(new KeyValuePair<string, string>("cursor", cursor.Trim()));
        }

        using var request = CreateSlackPost(method, requestFields);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);
        var messages = new List<SlackDownloadedMessage>();
        if (root.TryGetProperty("messages", out var messagesElement) &&
            messagesElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var messageElement in messagesElement.EnumerateArray())
            {
                if (skipParent && index++ == 0)
                {
                    continue;
                }

                if (messageElement.ValueKind == JsonValueKind.Object)
                {
                    messages.Add(ParseMessage(messageElement));
                }
            }
        }

        return new SlackDownloadMessagePage(messages, ReadNextCursor(root));
    }

    private HttpRequestMessage CreateSlackPost(
        string method,
        IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, method)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackBotToken!.Trim());
        return request;
    }

    private static SlackDownloadedMessage ParseMessage(JsonElement element)
    {
        return new SlackDownloadedMessage(
            GetString(element, "ts"),
            GetString(element, "user") ?? "unknown",
            GetString(element, "text") ?? string.Empty,
            GetInt32(element, "reply_count"));
    }

    private static string FormatMessage(SlackDownloadedMessage message, string indent = "")
    {
        var ts = string.IsNullOrWhiteSpace(message.Ts) ? "0" : message.Ts;
        var prefix = $"[{FormatTimestamp(ts)}] {message.User}: ";
        var normalized = message.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var firstLine = $"{indent}{prefix}{lines[0]}";
        if (lines.Length == 1)
        {
            return firstLine;
        }

        var contentIndent = indent + new string(' ', prefix.Length);
        return string.Join(
            Environment.NewLine,
            new[] { firstLine }.Concat(lines.Skip(1).Select(line => contentIndent + line)));
    }

    private static string FormatTimestamp(string ts)
    {
        if (!TryParseSlackTimestamp(ts, out var slackSeconds))
        {
            return DateTimeOffset.UnixEpoch.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(slackSeconds * 1000))
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static bool TryParseSlackTimestamp(string timestamp, out double value)
    {
        var raw = timestamp.Trim();
        var suffixIndex = raw.IndexOf('-', StringComparison.Ordinal);
        if (suffixIndex > 0)
        {
            raw = raw[..suffixIndex];
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string? ReadNextCursor(JsonElement root)
    {
        if (root.TryGetProperty("response_metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("next_cursor", out var nextCursor) &&
            nextCursor.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(nextCursor.GetString()))
        {
            return nextCursor.GetString()!.Trim();
        }

        return null;
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
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };
    }

    private void EnsureBotToken()
    {
        if (string.IsNullOrWhiteSpace(_options.SlackBotToken))
        {
            throw new InvalidOperationException("Mom Slack bot token is not configured.");
        }
    }

    private static Task WriteProgressAsync(TextWriter? progress, string message) =>
        progress is null ? Task.CompletedTask : progress.WriteLineAsync(message);

    private static string EnsureTrailingSlash(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "https://slack.com/api/" : value.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
    }

    private sealed record SlackDownloadedMessage(
        string? Ts,
        string User,
        string Text,
        int ReplyCount);

    private sealed record SlackDownloadMessagePage(
        IReadOnlyList<SlackDownloadedMessage> Messages,
        string? NextCursor);
}

public sealed record SlackChannelHistoryDownloadResult(
    string ChannelId,
    string ChannelName,
    int MessageCount,
    int ThreadReplyCount);
