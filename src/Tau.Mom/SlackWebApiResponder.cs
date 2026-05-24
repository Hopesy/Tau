using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tau.Mom;

public sealed class SlackWebApiResponder : IMomChannelMessageRuntimeResponder
{
    private readonly MomOptions _options;
    private readonly HttpClient _httpClient;

    public SlackWebApiResponder(MomOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(EnsureTrailingSlash(options.SlackApiBaseUrl), UriKind.Absolute);
    }

    public async Task<string?> RespondAsync(
        MomChannelMessage message,
        string text,
        CancellationToken cancellationToken = default)
    {
        return await PostMessageAsync(message.ChannelId, text, threadTs: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> RespondInThreadAsync(
        MomChannelMessage message,
        string text,
        CancellationToken cancellationToken = default)
    {
        var threadTs = string.IsNullOrWhiteSpace(message.ThreadTs) ? message.Ts : message.ThreadTs;
        return await PostMessageAsync(message.ChannelId, text, threadTs, cancellationToken).ConfigureAwait(false);
    }

    public Task SetTypingAsync(
        MomChannelMessage message,
        bool isTyping,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<string?> StartResponseAsync(
        MomChannelMessage message,
        string text,
        CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(message.ThreadTs)
            ? RespondAsync(message, text, cancellationToken)
            : RespondInThreadAsync(message, text, cancellationToken);
    }

    public async Task UpdateResponseAsync(
        MomChannelMessage message,
        string responseTs,
        string text,
        CancellationToken cancellationToken = default)
    {
        EnsureToken();
        if (string.IsNullOrWhiteSpace(responseTs))
        {
            throw new InvalidOperationException("Slack response timestamp is required.");
        }

        var payload = new Dictionary<string, string>
        {
            ["channel"] = message.ChannelId,
            ["ts"] = responseTs.Trim(),
            ["text"] = text
        };
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat.update")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackBotToken!.Trim());

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteResponseAsync(
        MomChannelMessage message,
        string responseTs,
        CancellationToken cancellationToken = default)
    {
        EnsureToken();
        if (string.IsNullOrWhiteSpace(responseTs))
        {
            throw new InvalidOperationException("Slack response timestamp is required.");
        }

        var payload = new Dictionary<string, string>
        {
            ["channel"] = message.ChannelId,
            ["ts"] = responseTs.Trim()
        };
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat.delete")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackBotToken!.Trim());

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadFileAsync(
        MomChannelMessage message,
        string filePath,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        EnsureToken();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Slack file path is required.");
        }

        await using var stream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent
        {
            { new StringContent(message.ChannelId, Encoding.UTF8), "channel_id" },
            { new StringContent(title ?? Path.GetFileName(filePath), Encoding.UTF8), "title" }
        };
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, "files.uploadV2") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackBotToken!.Trim());
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> PostMessageAsync(
        string channelId,
        string text,
        string? threadTs,
        CancellationToken cancellationToken)
    {
        EnsureToken();
        var payload = new Dictionary<string, string>
        {
            ["channel"] = channelId,
            ["text"] = text
        };
        if (!string.IsNullOrWhiteSpace(threadTs))
        {
            payload["thread_ts"] = threadTs.Trim();
        }

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat.postMessage")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackBotToken!.Trim());

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureToken()
    {
        if (string.IsNullOrWhiteSpace(_options.SlackBotToken))
        {
            throw new InvalidOperationException("Mom Slack bot token is not configured.");
        }
    }

    private static async Task<string?> EnsureSlackOkAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Slack Web API returned HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ok", out var ok) ||
            ok.ValueKind != JsonValueKind.True)
        {
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errorProperty)
                ? errorProperty.GetString()
                : null;
            throw new InvalidOperationException($"Slack Web API returned ok=false{(string.IsNullOrWhiteSpace(error) ? string.Empty : $": {error}")}.");
        }

        return root.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String
            ? ts.GetString()
            : null;
    }

    private static string EnsureTrailingSlash(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "https://slack.com/api/" : value.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
    }
}
