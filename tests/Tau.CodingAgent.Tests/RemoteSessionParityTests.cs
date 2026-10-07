using System.Collections.Concurrent;
using System.Threading.Channels;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class RemoteSessionParityTests
{
    [Fact]
    public async Task ExclusiveOwnershipRejectsSecondCoordinatorWithoutWireRequest()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var first = client.AttachSessionAsync("session-1");
        var attach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(attach, "attach", Snapshot("session-1", "idle")));
        await using var shared = await first;

        await Assert.ThrowsAsync<RemoteSessionOwnershipException>(() => RemoteSession.OpenAsync(client, "session-1"));
        Assert.Empty(transport.DrainClientMessages());
        var release = shared.DetachAsync();
        var detach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(detach, "detach", null, includeSession: false));
        await release;
    }

    [Fact]
    public async Task SubmitUsesPromptWhenIdleAndSteerWhenTurn()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var session = new RemoteSession(client, "session-1");
        var opening = session.OpenAsync();
        var attach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(attach, "attach", Snapshot("session-1", "idle")));
        await opening;

        var firstSubmit = session.SubmitAsync("first");
        var prompt = await transport.ReadClientMessageAsync();
        Assert.Equal("prompt", RequestCommand(prompt));
        await transport.WriteServerMessageAsync(Response(prompt, "prompt", Snapshot("session-1", "turn")));
        await firstSubmit;

        var secondSubmit = session.SubmitAsync("second");
        var steer = await transport.ReadClientMessageAsync();
        Assert.Equal("steer", RequestCommand(steer));
        await transport.WriteServerMessageAsync(Response(steer, "steer", Snapshot("session-1", "idle")));
        await secondSubmit;
    }

    [Fact]
    public async Task ServerErrorIsPropagatedAndDetachCanBeRetried()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var open = client.AttachSessionAsync("session-1");
        var attach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(attach, "attach", Snapshot("session-1", "idle")));
        await using var lease = await open;

        var firstDetach = lease.DetachAsync();
        var detach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(ErrorResponse(detach, "invalid_request", "temporary"));
        await Assert.ThrowsAsync<RemoteSessionServerException>(() => firstDetach);
        Assert.True(lease.Active);

        var retry = lease.DetachAsync();
        var retryRequest = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(retryRequest, "detach", null, includeSession: false));
        await retry;
        Assert.False(lease.Active);
    }

    [Fact]
    public async Task DetachClearsPublicBindingAndCompatibilityOpenCanReattach()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var session = new RemoteSession(client, "session-1");
        var opening = session.OpenAsync();
        var attach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(attach, "attach", Snapshot("session-1", "idle")));
        await opening;

        var detaching = session.DetachAsync();
        var detach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(detach, "detach", null, includeSession: false));
        await detaching;

        Assert.Null(session.Id);
        Assert.Null(session.SessionId);
        Assert.Equal(RemoteSessionLifecycle.Unbound, session.State.Lifecycle);

        var reopening = session.OpenAsync();
        var reattach = await transport.ReadClientMessageAsync();
        Assert.Equal("attach", RequestCommand(reattach));
        await transport.WriteServerMessageAsync(Response(reattach, "attach", Snapshot("session-1", "idle")));
        await reopening;

        var closing = session.DisposeAsync().AsTask();
        var close = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(close, "detach", null, includeSession: false));
        await closing;
    }

    [Fact]
    public async Task ConnectionStateSubscriptionReceivesTransitions()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var states = new List<RemoteSessionConnectionState>();
        using var subscription = client.OnConnectionStateChange(states.Add);

        client.Disconnect("test disconnect");

        Assert.Contains(RemoteSessionConnectionState.Disconnected, states);
    }

    [Fact]
    public async Task DisposingSessionDoesNotDisposeBorrowedClient()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var session = new RemoteSession(client, "session-1");
        var opening = session.OpenAsync();
        var attach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(attach, "attach", Snapshot("session-1", "idle")));
        await opening;

        var disposing = session.DisposeAsync().AsTask();
        var detach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(detach, "detach", null, includeSession: false));
        await disposing;

        Assert.True(session.IsDisposed);
        Assert.False(client.IsDisposed);
        Assert.Equal(RemoteSessionConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task SessionRemovedInvalidatesLeaseAndAllowsExplicitReopen()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var opening = RemoteSession.OpenAsync(client, "session-1");
        var initialAttach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(initialAttach, "attach", Snapshot("session-1", "idle")));
        var session = await opening;

        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = session.Subscribe(state =>
        {
            if (state.Lifecycle == RemoteSessionLifecycle.Unbound) removed.TrySetResult();
        });
        await transport.WriteServerMessageAsync(Event(new Dictionary<string, object?>
        {
            ["type"] = "session_removed",
            ["sessionId"] = "session-1"
        }));
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RemoteSessionLifecycle.Unbound, session.State.Lifecycle);
        Assert.Null(session.SessionId);

        var reopen = session.OpenAsync("session-1");
        var attach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(attach, "attach", Snapshot("session-1", "idle")));
        await reopen;
        Assert.Equal("session-1", session.SessionId);
        Assert.Equal(RemoteSessionLifecycle.Ready, session.State.Lifecycle);
        var close = session.DisposeAsync().AsTask();
        var detach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(detach, "detach", null, includeSession: false));
        await close;
    }

    [Fact]
    public async Task ReconnectPerformsHelloHandshakeAndReattachesSession()
    {
        await using var firstTransport = new DuplexTestStream();
        await using var secondTransport = new DuplexTestStream();
        var transports = new Queue<DuplexTestStream>([firstTransport, secondTransport]);
        await using var client = new PiRemoteClient(_ => Task.FromResult<Stream>(transports.Dequeue()));

        var connecting = client.ConnectAsync();
        var firstHello = await firstTransport.ReadClientMessageAsync();
        Assert.Equal("hello", firstHello["type"]?.ToString());
        Assert.Equal(RemoteSessionConnectionState.Connecting, client.ConnectionState);
        await firstTransport.WriteServerMessageAsync(ServerHello());
        await connecting;
        Assert.Equal(RemoteSessionConnectionState.Connected, client.ConnectionState);

        var session = new RemoteSession(client, "session-1");
        var opening = session.OpenAsync();
        var attach = await firstTransport.ReadClientMessageAsync();
        await firstTransport.WriteServerMessageAsync(Response(attach, "attach", Snapshot("session-1", "idle")));
        await opening;

        client.Disconnect("test reconnect");
        var reconnecting = session.ReconnectAsync();
        var secondHello = await secondTransport.ReadClientMessageAsync();
        Assert.Equal("hello", secondHello["type"]?.ToString());
        await secondTransport.WriteServerMessageAsync(ServerHello());
        var reattach = await secondTransport.ReadClientMessageAsync();
        Assert.Equal("attach", RequestCommand(reattach));
        await secondTransport.WriteServerMessageAsync(Response(reattach, "attach", Snapshot("session-1", "idle")));
        await reconnecting;

        Assert.Equal(RemoteSessionConnectionState.Connected, client.ConnectionState);
        Assert.Equal(RemoteSessionLifecycle.Ready, session.State.Lifecycle);
        var close = session.DisposeAsync();
        var detach = await secondTransport.ReadClientMessageAsync();
        await secondTransport.WriteServerMessageAsync(Response(detach, "detach", null, includeSession: false));
        await close;
    }

    [Fact]
    public async Task ReceiveEventCompletesWithDisconnectedError()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var waiting = client.ReceiveEventAsync();
        await Task.Delay(20);
        client.Disconnect("test disconnect");
        await Assert.ThrowsAsync<RemoteSessionDisconnectedException>(() => waiting);
    }

    [Fact]
    public async Task ReplacementFailureKeepsPreviousBindingAndCleansNewLease()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var session = new RemoteSession(client, "session-1");
        var opening = session.OpenAsync();
        var firstAttach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(firstAttach, "attach", Snapshot("session-1", "idle")));
        await opening;

        var replacement = session.OpenAsync("session-2");
        var secondAttach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(secondAttach, "attach", Snapshot("session-2", "idle")));
        var oldDetach = await transport.ReadClientMessageAsync();
        Assert.Equal("detach", RequestCommand(oldDetach));
        await transport.WriteServerMessageAsync(ErrorResponse(oldDetach, "invalid_request", "cannot detach yet"));
        var newCleanup = await transport.ReadClientMessageAsync();
        Assert.Equal("detach", RequestCommand(newCleanup));
        await transport.WriteServerMessageAsync(Response(newCleanup, "detach", null, includeSession: false));

        await Assert.ThrowsAsync<RemoteSessionServerException>(() => replacement);
        Assert.Equal("session-1", session.SessionId);
        Assert.Equal(RemoteSessionLifecycle.Ready, session.State.Lifecycle);
        var close = session.DisposeAsync();
        var detach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(detach, "detach", null, includeSession: false));
        await close;
    }

    [Fact]
    public async Task ProgressUpdatesProjectionWithoutMutatingAuthoritativeSnapshot()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var snapshot = Snapshot("session-1", "turn");
        snapshot["transcript"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["id"] = "assistant-1",
                ["role"] = "assistant",
                ["content"] = new object?[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = "hello" } },
                ["model"] = new Dictionary<string, object?> { ["provider"] = "test", ["id"] = "model" },
                ["timestamp"] = 1L,
                ["status"] = "streaming"
            }
        };
        var opening = RemoteSession.OpenAsync(client, "session-1");
        var attach = await transport.ReadClientMessageAsync();
        await transport.WriteServerMessageAsync(Response(attach, "attach", snapshot));
        var session = await opening;
        var projected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Subscribe(state =>
        {
            var item = state.Transcript.FirstOrDefault();
            if (item.ValueKind == System.Text.Json.JsonValueKind.Object &&
                item.GetProperty("content")[0].GetProperty("text").GetString() == "hello world")
                projected.TrySetResult(true);
        });

        await transport.WriteServerMessageAsync(Event(new Dictionary<string, object?>
        {
            ["type"] = "session_progress",
            ["sessionId"] = "session-1",
            ["progress"] = new Dictionary<string, object?>
            {
                ["type"] = "assistant_delta",
                ["messageId"] = "assistant-1",
                ["contentIndex"] = 0L,
                ["kind"] = "text",
                ["delta"] = " world"
            }
        }));
        await projected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var authoritative = session.Snapshot ?? throw new InvalidOperationException("Missing authoritative snapshot");
        Assert.Equal("hello", authoritative.GetProperty("transcript")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("hello world", session.State.Transcript[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task DisposePreemptsPendingOpenAndAwaitsReplacementCleanup()
    {
        await using var transport = new DuplexTestStream();
        await using var client = new PiRemoteClient(transport);
        var session = new RemoteSession(client, "session-1");
        var opening = session.OpenAsync();
        var attach = await transport.ReadClientMessageAsync();
        var disposing = session.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<RemoteSessionDisposedException>(() => opening);

        await transport.WriteServerMessageAsync(Response(attach, "attach", Snapshot("session-1", "idle")));
        var detach = await transport.ReadClientMessageAsync();
        Assert.Equal("detach", RequestCommand(detach));
        await transport.WriteServerMessageAsync(Response(detach, "detach", null, includeSession: false));
        await disposing;
        Assert.True(session.IsDisposed);
    }

    private static string RequestCommand(IReadOnlyDictionary<string, object?> envelope) =>
        ((IReadOnlyDictionary<string, object?>)envelope["request"]!)["command"]!.ToString()!;

    private static Dictionary<string, object?> Response(IReadOnlyDictionary<string, object?> request, string command, Dictionary<string, object?>? snapshot, bool includeSession = true)
    {
        var result = new Dictionary<string, object?> { ["command"] = command };
        if (includeSession) result["session"] = snapshot!;
        else result["sessionId"] = "session-1";
        return new Dictionary<string, object?> { ["type"] = "response", ["id"] = request["id"]!, ["ok"] = true, ["result"] = result };
    }

    private static Dictionary<string, object?> ErrorResponse(IReadOnlyDictionary<string, object?> request, string code, string message) =>
        new() { ["type"] = "response", ["id"] = request["id"]!, ["ok"] = false, ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message } };

    private static Dictionary<string, object?> Event(Dictionary<string, object?> value) =>
        new() { ["type"] = "event", ["event"] = value };

    private static Dictionary<string, object?> ServerHello() => new()
    {
        ["type"] = "hello",
        ["version"] = 1L,
        ["connectionId"] = "connection-1",
        ["snapshot"] = new Dictionary<string, object?>
        {
            ["serverId"] = "server-1",
            ["protocolVersion"] = 1L,
            ["revision"] = 1L,
            ["sessions"] = Array.Empty<object?>(),
            ["models"] = Array.Empty<object?>()
        }
    };

    private static Dictionary<string, object?> Snapshot(string id, string phase) => new()
    {
        ["id"] = id,
        ["cwd"] = "/workspace",
        ["createdAt"] = 1L,
        ["updatedAt"] = 1L,
        ["phase"] = phase,
        ["model"] = new Dictionary<string, object?> { ["provider"] = "test", ["id"] = "model" },
        ["thinkingLevel"] = "off",
        ["attached"] = true,
        ["locked"] = false,
        ["revision"] = 1L,
        ["transcript"] = Array.Empty<object?>(),
        ["queuedSteer"] = Array.Empty<object?>(),
        ["queuedSteerCount"] = 0L
    };

    private sealed class DuplexTestStream : Stream
    {
        private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _toServer = Channel.CreateUnbounded<byte[]>();
        private readonly FramedProtocolDecoder _decoder = new();
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, object?>> _clientMessages = new();
        private byte[]? _readBuffer;
        private int _readOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ReadCoreAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => _toServer.Writer.TryWrite(buffer[offset..(offset + count)].ToArray());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { _toServer.Writer.TryWrite(buffer.ToArray()); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { Write(buffer, offset, count); return Task.CompletedTask; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public async Task<IReadOnlyDictionary<string, object?>> ReadClientMessageAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_clientMessages.TryDequeue(out var message)) return message;
                var data = await _toServer.Reader.ReadAsync(cancellationToken);
                foreach (var frame in _decoder.Push(data))
                {
                    var value = CborCodec.Decode(frame) as IReadOnlyDictionary<string, object?> ?? throw new InvalidOperationException("Client message is not a map");
                    _clientMessages.Enqueue(value);
                }
            }
        }

        public async Task WriteServerMessageAsync(IReadOnlyDictionary<string, object?> message, CancellationToken cancellationToken = default) => await _toClient.Writer.WriteAsync(FramedProtocol.EncodeServerMessage(message), cancellationToken);
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> DrainClientMessages() => _clientMessages.ToArray();

        private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            while (_readBuffer is null || _readOffset >= _readBuffer.Length)
            {
                _readBuffer = await _toClient.Reader.ReadAsync(cancellationToken);
                _readOffset = 0;
            }
            var count = Math.Min(destination.Length, _readBuffer.Length - _readOffset);
            _readBuffer.AsMemory(_readOffset, count).CopyTo(destination);
            _readOffset += count;
            return count;
        }
    }
}
