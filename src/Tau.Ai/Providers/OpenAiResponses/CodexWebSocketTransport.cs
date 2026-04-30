using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tau.Ai.Providers.OpenAiResponses;

public interface ICodexWebSocketTransport
{
    Task<ICodexWebSocketConnection> ConnectAsync(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default);
}

public interface ICodexWebSocketConnection : IAsyncDisposable
{
    WebSocketState State { get; }

    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ReadTextMessagesAsync(CancellationToken cancellationToken = default);

    ValueTask CloseAsync(int statusCode, string reason, CancellationToken cancellationToken = default);
}

public sealed class ClientCodexWebSocketTransport : ICodexWebSocketTransport
{
    public async Task<ICodexWebSocketConnection> ConnectAsync(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        foreach (var (key, value) in headers)
        {
            socket.Options.SetRequestHeader(key, value);
        }

        await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
        return new ClientCodexWebSocketConnection(socket);
    }
}

internal sealed class ClientCodexWebSocketConnection : ICodexWebSocketConnection
{
    private readonly ClientWebSocket _socket;

    public ClientCodexWebSocketConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public WebSocketState State => _socket.State;

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return _socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    public async IAsyncEnumerable<string> ReadTextMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        while (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (message.Length == 0)
            {
                continue;
            }

            yield return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    public async ValueTask CloseAsync(
        int statusCode,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseAsync(
                (WebSocketCloseStatus)statusCode,
                reason,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
