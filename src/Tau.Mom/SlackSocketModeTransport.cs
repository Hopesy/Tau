using System.Buffers;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Tau.Mom;

public interface ISlackSocketModeConnector
{
    Task<ISlackSocketModeConnection> ConnectAsync(Uri socketUrl, CancellationToken cancellationToken = default);
}

public interface ISlackSocketModeConnection : IAsyncDisposable
{
    IAsyncEnumerable<string> ReadTextMessagesAsync(CancellationToken cancellationToken = default);

    Task SendTextAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class ClientWebSocketSlackSocketModeConnector : ISlackSocketModeConnector
{
    public async Task<ISlackSocketModeConnection> ConnectAsync(
        Uri socketUrl,
        CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUrl, cancellationToken).ConfigureAwait(false);
        return new ClientWebSocketSlackSocketModeConnection(socket);
    }
}

public sealed class SlackSocketModeTransport : IMomChannelTransport
{
    private readonly MomOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ISlackSocketModeConnector _connector;
    private readonly ILogger<SlackSocketModeTransport> _logger;
    private string? _botUserId;

    public SlackSocketModeTransport(
        MomOptions options,
        SlackWebApiResponder responder,
        ILogger<SlackSocketModeTransport> logger,
        HttpClient? httpClient = null,
        ISlackSocketModeConnector? connector = null)
    {
        _options = options;
        Responder = responder;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(EnsureTrailingSlash(options.SlackApiBaseUrl), UriKind.Absolute);
        _connector = connector ?? new ClientWebSocketSlackSocketModeConnector();
    }

    public IMomChannelResponder Responder { get; }

    public async IAsyncEnumerable<MomChannelMessage> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        while (!cancellationToken.IsCancellationRequested)
        {
            var botUserId = await ResolveBotUserIdAsync(cancellationToken).ConfigureAwait(false);
            var socketUrl = await OpenSocketModeConnectionAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Opening Slack Socket Mode connection.");

            await using var connection = await _connector.ConnectAsync(socketUrl, cancellationToken).ConfigureAwait(false);
            await foreach (var rawEnvelope in connection.ReadTextMessagesAsync(cancellationToken).ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(rawEnvelope);
                if (TryGetEnvelopeId(document.RootElement, out var envelopeId))
                {
                    await AcknowledgeAsync(connection, envelopeId, cancellationToken).ConfigureAwait(false);
                }

                var message = SlackEventMapper.MapSocketModeEvent(
                    document.RootElement,
                    botUserId,
                    users: null,
                    _options.DefaultProvider,
                    _options.DefaultModel);
                if (message is not null)
                {
                    yield return message;
                }
            }

            var delay = TimeSpan.FromSeconds(Math.Max(1, _options.SlackSocketModeReconnectDelaySeconds));
            _logger.LogWarning("Slack Socket Mode connection ended. Reconnecting in {DelaySeconds}s.", delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
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
        if (root.TryGetProperty("user_id", out var userId) && userId.ValueKind == JsonValueKind.String)
        {
            _botUserId = userId.GetString();
        }

        return _botUserId;
    }

    private async Task<Uri> OpenSocketModeConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureAppToken();
        using var request = new HttpRequestMessage(HttpMethod.Post, "apps.connections.open");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SlackAppToken!.Trim());
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = await EnsureSlackOkAsync(response, cancellationToken).ConfigureAwait(false);
        if (!root.TryGetProperty("url", out var urlProperty) ||
            urlProperty.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(urlProperty.GetString()))
        {
            throw new InvalidOperationException("Slack Socket Mode open response did not include a socket URL.");
        }

        return new Uri(urlProperty.GetString()!, UriKind.Absolute);
    }

    private static async Task AcknowledgeAsync(
        ISlackSocketModeConnection connection,
        string envelopeId,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["envelope_id"] = envelopeId
        });
        await connection.SendTextAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureConfigured()
    {
        EnsureAppToken();
        EnsureBotToken();
    }

    private void EnsureAppToken()
    {
        if (string.IsNullOrWhiteSpace(_options.SlackAppToken))
        {
            throw new InvalidOperationException("Mom Slack app token is not configured.");
        }
    }

    private void EnsureBotToken()
    {
        if (string.IsNullOrWhiteSpace(_options.SlackBotToken))
        {
            throw new InvalidOperationException("Mom Slack bot token is not configured.");
        }
    }

    private static bool TryGetEnvelopeId(JsonElement root, out string envelopeId)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("envelope_id", out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            envelopeId = property.GetString()!.Trim();
            return true;
        }

        envelopeId = string.Empty;
        return false;
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

    private static string EnsureTrailingSlash(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "https://slack.com/api/" : value.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
    }
}

internal sealed class ClientWebSocketSlackSocketModeConnection : ISlackSocketModeConnection
{
    private readonly ClientWebSocket _socket;

    public ClientWebSocketSlackSocketModeConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public async IAsyncEnumerable<string> ReadTextMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[8192];
        var builder = new ArrayBufferWriter<byte>();
        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            builder.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }

                builder.Write(buffer.AsSpan(0, result.Count));
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                yield return Encoding.UTF8.GetString(builder.WrittenSpan);
            }
        }
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Tau.Mom shutdown", cts.Token)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }

        _socket.Dispose();
    }
}
