using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Tau.CodingAgent.Runtime;

/// <summary>远程 session 的连接生命周期。</summary>
public enum RemoteSessionLifecycle { Unbound, Ready, Busy, Disposed }

/// <summary>远程会话当前执行的操作。</summary>
public enum RemoteSessionOperation { Open, Create, Submit, Abort, SetModel, SetThinking, Reconnect }

/// <summary>远程客户端连接状态。</summary>
public enum RemoteSessionConnectionState { Disconnected, Connecting, Connected }

/// <summary>远程 session 租约模式。</summary>
public enum RemoteSessionLeaseMode { Shared, Exclusive }

/// <summary>远程 session 的状态快照。</summary>
/// <param name="Lifecycle">客户端生命周期。</param>
/// <param name="SessionId">服务端 session id。</param>
/// <param name="Transcript">收到的事件记录。</param>
/// <param name="Operation">当前客户端操作。</param>
/// <param name="Snapshot">服务端 session snapshot。</param>
/// <param name="Phase">服务端 phase。</param>
/// <param name="ConnectionState">底层连接状态。</param>
public sealed record RemoteSessionState(
    RemoteSessionLifecycle Lifecycle,
    string? SessionId,
    IReadOnlyList<JsonElement> Transcript,
    RemoteSessionOperation? Operation = null,
    JsonElement? Snapshot = null,
    string? Phase = null,
    RemoteSessionConnectionState ConnectionState = RemoteSessionConnectionState.Connected);

/// <summary>远程 session 所有权冲突。</summary>
public sealed class RemoteSessionOwnershipException : InvalidOperationException
{
    /// <summary>初始化所有权冲突。</summary>
    /// <param name="sessionId">冲突 session id。</param>
    /// <param name="message">异常信息。</param>
    public RemoteSessionOwnershipException(string sessionId, string message) : base(message) => SessionId = sessionId;
    /// <summary>冲突 session id。</summary>
    public string SessionId { get; }
}

/// <summary>租约已经 detach。</summary>
public sealed class RemoteSessionDetachedException : InvalidOperationException
{
    /// <summary>初始化 detach 异常。</summary>
    /// <param name="sessionId">session id。</param>
    public RemoteSessionDetachedException(string sessionId) : base($"Remote session '{sessionId}' is detached.") => SessionId = sessionId;
    /// <summary>session id。</summary>
    public string SessionId { get; }
}

/// <summary>远程连接不可用。</summary>
public sealed class RemoteSessionDisconnectedException : IOException
{
    /// <summary>初始化断线异常。</summary>
    /// <param name="message">异常信息。</param>
    public RemoteSessionDisconnectedException(string message = "Remote client is disconnected.") : base(message) { }
}

/// <summary>远程 session 已释放。</summary>
public sealed class RemoteSessionDisposedException : ObjectDisposedException
{
    /// <summary>初始化释放异常。</summary>
    public RemoteSessionDisposedException() : base("RemoteSession") { }
}

/// <summary>服务端拒绝了远程请求。</summary>
public sealed class RemoteSessionServerException : InvalidOperationException
{
    /// <summary>初始化服务端异常。</summary>
    /// <param name="code">服务端错误码。</param>
    /// <param name="message">服务端错误消息。</param>
    public RemoteSessionServerException(string code, string message) : base(message) => Code = code;
    /// <summary>服务端错误码。</summary>
    public string Code { get; }
}

internal static class RemoteProtocolValues
{
    public static IReadOnlyDictionary<string, object?> Map(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> dictionary => dictionary,
        IDictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary),
        _ => throw new ProtocolValidationException("Protocol value is not an object map.")
    };

    public static string String(IReadOnlyDictionary<string, object?> map, string name) =>
        map.TryGetValue(name, out var value) && value is string text
            ? text
            : throw new ProtocolValidationException($"Protocol object is missing '{name}'.");

    public static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
}

/// <summary>
/// 基于任意双向 Stream 的远程 framed client，负责请求匹配、事件分发、租约计数和断线重连。
/// </summary>
public sealed class PiRemoteClient : IAsyncDisposable
{
    private Stream? _stream;
    private FramedProtocolDecoder _decoder;
    private readonly FramedProtocolOptions _options;
    private readonly Func<CancellationToken, Task<Stream>>? _transportFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _rawReadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private Channel<object?> _events = CreateEventChannel();
    private readonly object _eventGate = new();
    private readonly List<Action<object?>> _eventListeners = [];
    private readonly List<Action<RemoteSessionConnectionState>> _connectionStateListeners = [];
    private readonly object _stateGate = new();
    private readonly object _leaseGate = new();
    private readonly Dictionary<string, int> _leaseCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exclusiveLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leaseGenerations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cleanupRequired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _sessionSnapshots = new(StringComparer.Ordinal);
    private JsonElement? _serverSnapshot;
    private readonly Dictionary<string, Task<JsonElement>> _sessionAttachments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _sessionDetachments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _sessionReconciliations = new(StringComparer.Ordinal);
    private readonly Queue<byte[]> _pendingFrames = new();
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pump;
    private int _requestSequence;
    private bool _disposed;
    private RemoteSessionConnectionState _connectionState;

    /// <summary>使用已建立的双向流创建客户端。</summary>
    /// <param name="stream">双向协议流。</param>
    /// <param name="options">协议限制。</param>
    public PiRemoteClient(Stream stream, FramedProtocolOptions? options = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _options = options ?? new FramedProtocolOptions();
        _decoder = new FramedProtocolDecoder(_options);
        _connectionState = RemoteSessionConnectionState.Connected;
    }

    /// <summary>使用传输工厂创建支持 reconnect 的客户端。</summary>
    /// <param name="transportFactory">每次重连创建协议流的工厂。</param>
    /// <param name="options">协议限制。</param>
    public PiRemoteClient(Func<CancellationToken, Task<Stream>> transportFactory, FramedProtocolOptions? options = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _options = options ?? new FramedProtocolOptions();
        _decoder = new FramedProtocolDecoder(_options);
        _connectionState = RemoteSessionConnectionState.Disconnected;
    }

    /// <summary>获取连接状态。</summary>
    public RemoteSessionConnectionState ConnectionState { get { lock (_stateGate) return _connectionState; } }
    /// <summary>客户端是否已经释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 获取最近一次握手或 server_snapshot 事件发布的服务端快照。
    /// </summary>
    /// <returns>服务端快照；尚未完成握手时返回 null。</returns>
    public JsonElement? ServerSnapshot
    {
        get
        {
            lock (_leaseGate)
                return _serverSnapshot;
        }
    }

    /// <summary>
    /// 使用 transport factory 建立连接并完成 pi hello 握手。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>握手完成后的异步任务。</returns>
    public Task ConnectAsync(CancellationToken cancellationToken = default) => ReconnectAsync(cancellationToken);

    /// <summary>发送 JSON 消息。</summary>
    /// <param name="command">消息对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SendAsync<T>(T command, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await WriteAsync(FramedProtocol.EncodeJson(command), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送严格 CBOR 消息。</summary>
    /// <param name="message">协议消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SendCborAsync(object? message, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await WriteAsync(FramedProtocol.EncodeClientMessage(message, _options), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送 request 并等待相同 id 的 response。</summary>
    /// <param name="request">协议 command map。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端 result map。</returns>
    public async Task<IReadOnlyDictionary<string, object?>> RequestAsync(IReadOnlyDictionary<string, object?> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        EnsurePump();
        var id = $"request-{Interlocked.Increment(ref _requestSequence)}";
        var pending = new PendingRequest(RemoteProtocolValues.String(request, "command"));
        _pendingRequests[id] = pending;
        try
        {
            await SendCborAsync(new Dictionary<string, object?> { ["type"] = "request", ["id"] = id, ["request"] = request }, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(id, out _);
            throw;
        }
    }

    /// <summary>订阅所有服务端事件，回调异常不会破坏协议泵。</summary>
    /// <param name="listener">事件回调。</param>
    /// <returns>取消订阅动作。</returns>
    public IDisposable SubscribeEvents(Action<object?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        EnsureNotDisposed();
        lock (_eventListeners) _eventListeners.Add(listener);
        return new DelegateSubscription(() => { lock (_eventListeners) _eventListeners.Remove(listener); });
    }

    /// <summary>
    /// 订阅底层远程客户端的连接状态变化。
    /// </summary>
    /// <param name="listener">连接状态回调，参数为新的连接状态。</param>
    /// <returns>取消订阅句柄。</returns>
    public IDisposable OnConnectionStateChange(Action<RemoteSessionConnectionState> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        EnsureNotDisposed();
        lock (_stateGate) _connectionStateListeners.Add(listener);
        return new DelegateSubscription(() =>
        {
            lock (_stateGate) _connectionStateListeners.Remove(listener);
        });
    }

    /// <summary>读取下一个事件或未匹配消息。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>协议对象，流结束时返回 null。</returns>
    public async Task<object?> ReceiveEventAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (ConnectionState != RemoteSessionConnectionState.Connected)
            throw new RemoteSessionDisconnectedException();
        EnsurePump();
        Channel<object?> events;
        lock (_eventGate) events = _events;
        try
        {
            return await events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex) when (ex.InnerException is RemoteSessionDisconnectedException disconnected)
        {
            throw disconnected;
        }
    }

    /// <summary>读取下一个 JSON 消息。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>JSON 元素，流结束时返回 null。</returns>
    public async Task<JsonElement?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (_pump is not null)
        {
            var value = await ReceiveEventAsync(cancellationToken).ConfigureAwait(false);
            return value is null ? null : RemoteProtocolValues.Json(value);
        }
        var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null) return null;
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.Clone();
    }

    /// <summary>读取下一个 CBOR 消息。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>协议对象，流结束时返回 null。</returns>
    public async Task<object?> ReceiveCborAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (_pump is not null) return await ReceiveEventAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRawAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重建传输并恢复 connected 状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (_transportFactory is null)
        {
            EnsureConnected();
            return;
        }
        if (ConnectionState != RemoteSessionConnectionState.Disconnected)
            Disconnect("Reconnecting remote transport.");
        SetConnectionState(RemoteSessionConnectionState.Connecting);
        var pumpCancellation = Interlocked.Exchange(ref _pumpCancellation, null);
        pumpCancellation?.Cancel();
        var previousPump = Interlocked.Exchange(ref _pump, null);
        var previous = Interlocked.Exchange(ref _stream, null);
        if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        if (previousPump is not null)
        {
            try { await previousPump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        pumpCancellation?.Dispose();
        lock (_eventGate) _events = CreateEventChannel();
        try
        {
            var stream = await _transportFactory(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _stream, stream);
            _decoder = new FramedProtocolDecoder(_options);
            await WriteAsync(FramedProtocol.EncodeClientMessage(new Dictionary<string, object?> { ["type"] = "hello", ["version"] = 1 }, _options), cancellationToken).ConfigureAwait(false);
            var hello = await ReadRawAsync(cancellationToken).ConfigureAwait(false);
            if (hello is not IReadOnlyDictionary<string, object?> helloMap || !helloMap.TryGetValue("type", out var helloType))
                throw new ProtocolValidationException("Reconnect did not receive a server hello.");
            if (helloType is "hello_error")
            {
                var error = helloMap.TryGetValue("error", out var rawError) && rawError is IReadOnlyDictionary<string, object?> errorMap
                    ? new RemoteSessionServerException(
                        errorMap.TryGetValue("code", out var code) && code is string errorCode ? errorCode : "server_error",
                        errorMap.TryGetValue("message", out var message) && message is string errorMessage ? errorMessage : "Server handshake failed")
                    : new RemoteSessionServerException("server_error", "Server handshake failed");
                throw error;
            }
            if (helloType is not "hello")
                throw new ProtocolValidationException("Reconnect did not receive a server hello.");
            if (helloMap.TryGetValue("snapshot", out var helloSnapshot))
                UpdateServerSnapshot(helloSnapshot);
            SetConnectionState(RemoteSessionConnectionState.Connected);
            EnsurePump();
        }
        catch
        {
            Disconnect("Remote transport handshake failed.");
            var failedStream = Interlocked.Exchange(ref _stream, null);
            if (failedStream is not null) await failedStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>主动标记断线并使旧租约失效。</summary>
    /// <param name="reason">断开原因。</param>
    public void Disconnect(string reason = "Client disconnected")
    {
        SetConnectionState(RemoteSessionConnectionState.Disconnected);
        var error = new RemoteSessionDisconnectedException(reason);
        foreach (var pending in _pendingRequests.Values) pending.Completion.TrySetException(error);
        _pendingRequests.Clear();
        lock (_leaseGate)
        {
            foreach (var id in _leaseCounts.Keys.ToArray()) _leaseGenerations[id] = _leaseGenerations.GetValueOrDefault(id) + 1;
            _leaseCounts.Clear();
            _exclusiveLeases.Clear();
            _sessionSnapshots.Clear();
            _cleanupRequired.Clear();
            _sessionAttachments.Clear();
            _sessionDetachments.Clear();
            _sessionReconciliations.Clear();
            _pendingFrames.Clear();
        }
        _pumpCancellation?.Cancel();
        Channel<object?> events;
        lock (_eventGate) events = _events;
        events.Writer.TryComplete(error);
    }

    /// <summary>获取 shared 或 exclusive session 租约。</summary>
    /// <param name="sessionId">session id。</param>
    /// <param name="mode">租约模式。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>session 租约。</returns>
    public async Task<RemoteSessionLease> AcquireSessionAsync(string sessionId, RemoteSessionLeaseMode mode = RemoteSessionLeaseMode.Shared, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureConnected();
        SessionLeaseToken token;
        lock (_leaseGate) token = ReserveLease(sessionId, mode);
        try
        {
            Task? previousDetachment;
            lock (_leaseGate) _sessionDetachments.TryGetValue(sessionId, out previousDetachment);
            if (previousDetachment is not null)
            {
                try { await previousDetachment.ConfigureAwait(false); }
                catch { }
            }
            var reconciled = IsCleanupRequired(sessionId);
            if (reconciled) await ReconcileCleanupAsync(sessionId, cancellationToken).ConfigureAwait(false);
            Task<JsonElement>? attachment;
            lock (_leaseGate)
            {
                if (reconciled || !_sessionSnapshots.TryGetValue(sessionId, out _))
                {
                    if (!_sessionAttachments.TryGetValue(sessionId, out attachment))
                    {
                        attachment = AttachSessionCoreAsync(sessionId, cancellationToken);
                        _sessionAttachments[sessionId] = attachment;
                    }
                }
                else attachment = null;
            }
            JsonElement? snapshot;
            if (attachment is not null)
            {
                try { snapshot = await attachment.ConfigureAwait(false); }
                finally
                {
                    lock (_leaseGate)
                    {
                        if (_sessionAttachments.TryGetValue(sessionId, out var current) && ReferenceEquals(current, attachment))
                            _sessionAttachments.Remove(sessionId);
                    }
                }
            }
            else lock (_leaseGate) snapshot = _sessionSnapshots.TryGetValue(sessionId, out var knownSnapshot) ? knownSnapshot : null;
            return new RemoteSessionLease(this, sessionId, token.Generation, snapshot);
        }
        catch
        {
            lock (_leaseGate) ReleaseLeaseBookkeeping(sessionId, token);
            throw;
        }
    }

    /// <summary>兼容 pi-client 的 shared attach 入口。</summary>
    /// <param name="sessionId">session id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<RemoteSessionLease> AttachSessionAsync(string sessionId, CancellationToken cancellationToken = default) => AcquireSessionAsync(sessionId, RemoteSessionLeaseMode.Shared, cancellationToken);

    /// <summary>创建服务端 session 并返回 exclusive 租约。</summary>
    /// <param name="cwd">工作目录。</param>
    /// <param name="name">可选名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<RemoteSessionLease> CreateSessionAsync(string cwd, string? name = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        var request = new Dictionary<string, object?> { ["command"] = "create", ["cwd"] = cwd };
        if (name is not null) request["name"] = name;
        var result = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        var snapshot = SessionResult(result, "create");
        var id = snapshot.GetProperty("id").GetString() ?? throw new ProtocolValidationException("Create result has no session id.");
        lock (_leaseGate)
        {
            var token = ReserveLease(id, RemoteSessionLeaseMode.Exclusive);
            MarkSessionAttached(id);
            return new RemoteSessionLease(this, id, token.Generation, snapshot);
        }
    }

    internal async Task<JsonElement> SessionRequestAsync(string sessionId, int generation, string command, object? payload, CancellationToken cancellationToken)
    {
        EnsureLeaseActive(sessionId, generation);
        var request = new Dictionary<string, object?> { ["command"] = command, ["sessionId"] = sessionId };
        if (payload is IReadOnlyDictionary<string, object?> map) foreach (var pair in map) request[pair.Key] = pair.Value;
        var result = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        var snapshot = SessionResult(result, command);
        MarkSessionAttached(sessionId);
        return snapshot;
    }

    internal IDisposable SubscribeSessionEvents(string sessionId, Action<object?> listener) => SubscribeEvents(value =>
    {
        if (value is not IReadOnlyDictionary<string, object?> envelope || !envelope.TryGetValue("event", out var raw) || raw is not IReadOnlyDictionary<string, object?> evt) return;
        if (evt.TryGetValue("sessionId", out var id) && id is string eventSession && eventSession == sessionId) listener(value);
        else if (evt.TryGetValue("snapshot", out var snapshot) && snapshot is IReadOnlyDictionary<string, object?> map && map.TryGetValue("id", out var snapshotId) && snapshotId is string idValue && idValue == sessionId) listener(value);
    });

    /// <summary>
    /// 订阅指定 session 的权威快照变化。
    /// </summary>
    /// <param name="sessionId">需要监听的 session id。</param>
    /// <param name="listener">收到快照后的回调。</param>
    /// <returns>取消订阅的句柄。</returns>
    internal IDisposable SubscribeSessionSnapshots(string sessionId, Action<JsonElement> listener) => SubscribeEvents(value =>
    {
        if (value is not IReadOnlyDictionary<string, object?> envelope ||
            !envelope.TryGetValue("event", out var raw) ||
            raw is not IReadOnlyDictionary<string, object?> evt)
            return;
        if (evt.TryGetValue("snapshot", out var snapshot) &&
            snapshot is IReadOnlyDictionary<string, object?> map &&
            map.TryGetValue("id", out var id) &&
            id is string eventSession && eventSession == sessionId)
        {
            listener(RemoteProtocolValues.Json(snapshot));
        }
    });

    internal bool IsLeaseActive(string sessionId, int generation)
    {
        lock (_leaseGate) return !_disposed && ConnectionState == RemoteSessionConnectionState.Connected && _leaseGenerations.GetValueOrDefault(sessionId) == generation && _leaseCounts.ContainsKey(sessionId);
    }

    /// <summary>读取指定 session 当前缓存的最新 snapshot。</summary>
    /// <param name="sessionId">目标 session id。</param>
    /// <returns>当前 snapshot；尚未收到 snapshot 时返回 null。</returns>
    internal JsonElement? GetSessionSnapshot(string sessionId)
    {
        lock (_leaseGate)
            return _sessionSnapshots.TryGetValue(sessionId, out var snapshot) ? snapshot : null;
    }

    /// <summary>读取指定 session 当前缓存的最新 snapshot。</summary>
    /// <param name="sessionId">目标 session id。</param>
    /// <returns>当前 snapshot；尚未收到 snapshot 时返回 null。</returns>
    internal async Task ReleaseLeaseAsync(string sessionId, int generation, bool relinquishOnFailure, CancellationToken cancellationToken)
    {
        if (!IsLeaseActive(sessionId, generation)) return;
        EnsureLeaseGeneration(sessionId, generation);
        lock (_leaseGate)
        {
            var count = _leaseCounts.GetValueOrDefault(sessionId);
            if (count > 1)
            {
                ReleaseLeaseBookkeeping(sessionId, new SessionLeaseToken(RemoteSessionLeaseMode.Shared, generation));
                return;
            }
        }
        try
        {
            var detachment = RequestAsync(new Dictionary<string, object?> { ["command"] = "detach", ["sessionId"] = sessionId }, cancellationToken);
            lock (_leaseGate) _sessionDetachments[sessionId] = detachment;
            try { await detachment.ConfigureAwait(false); }
            finally
            {
                lock (_leaseGate)
                {
                    if (_sessionDetachments.TryGetValue(sessionId, out var current) && ReferenceEquals(current, detachment))
                        _sessionDetachments.Remove(sessionId);
                }
            }
            lock (_leaseGate) ReleaseLeaseBookkeeping(sessionId, new SessionLeaseToken(RemoteSessionLeaseMode.Exclusive, generation));
            MarkSessionDetached(sessionId);
        }
        catch
        {
            if (relinquishOnFailure)
            {
                lock (_leaseGate)
                {
                    ReleaseLeaseBookkeeping(sessionId, new SessionLeaseToken(RemoteSessionLeaseMode.Exclusive, generation));
                    _cleanupRequired.Add(sessionId);
                }
            }
            throw;
        }
    }

    /// <summary>释放客户端和底层流。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect("Client disposed");
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        _writeLock.Dispose();
        _rawReadLock.Dispose();
        lock (_stateGate) _connectionStateListeners.Clear();
    }

    private static Channel<object?> CreateEventChannel() =>
        Channel.CreateUnbounded<object?>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private void EnsureNotDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PiRemoteClient)); }
    private void EnsureConnected() { EnsureNotDisposed(); if (ConnectionState != RemoteSessionConnectionState.Connected || _stream is null) throw new RemoteSessionDisconnectedException(); }
    private void SetConnectionState(RemoteSessionConnectionState state)
    {
        Action<RemoteSessionConnectionState>[] listeners;
        lock (_stateGate)
        {
            if (_connectionState == state) return;
            _connectionState = state;
            listeners = _connectionStateListeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            try { listener(state); }
            catch { }
        }
    }
    private async Task WriteAsync(byte[] frame, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new RemoteSessionDisconnectedException();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Disconnect("Remote transport write failed.");
            throw;
        }
        finally { _writeLock.Release(); }
    }
    private void EnsurePump()
    {
        lock (_stateGate)
        {
            if (_pump is not null && !_pump.IsCompleted) return;
            _pumpCancellation?.Dispose();
            _pumpCancellation = new CancellationTokenSource();
            _pump = Task.Run(() => PumpAsync(_pumpCancellation.Token));
        }
    }
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_disposed && ConnectionState == RemoteSessionConnectionState.Connected)
            {
                var value = await ReadRawAsync(cancellationToken).ConfigureAwait(false);
                if (value is null) { Disconnect("Remote transport closed."); break; }
                var map = RemoteProtocolValues.Map(value);
                if (map.TryGetValue("type", out var type) && type is string typeName && typeName == "response")
                {
                    var id = RemoteProtocolValues.String(map, "id");
                    if (!_pendingRequests.TryRemove(id, out var pending))
                    {
                        var protocolError = new ProtocolValidationException($"Response has no matching request '{id}'.");
                        Disconnect(protocolError.Message);
                        break;
                    }
                    if (map.TryGetValue("ok", out var ok) && ok is bool success && !success)
                    {
                        var error = map.TryGetValue("error", out var rawError) && rawError is IReadOnlyDictionary<string, object?> errorMap
                            ? new RemoteSessionServerException(errorMap.TryGetValue("code", out var code) && code is string c ? c : "server_error", errorMap.TryGetValue("message", out var message) && message is string m ? m : "Server request failed")
                            : new RemoteSessionServerException("server_error", "Server request failed");
                        pending.Completion.TrySetException(error);
                    }
                    else if (map.TryGetValue("result", out var rawResult))
                    {
                        var result = RemoteProtocolValues.Map(rawResult);
                        var command = result.TryGetValue("command", out var resultCommand) && resultCommand is string rc ? rc : string.Empty;
                        if (!string.Equals(command, pending.Command, StringComparison.Ordinal))
                        {
                            var protocolError = new ProtocolValidationException($"Response command {command} does not match {pending.Command}");
                            pending.Completion.TrySetException(protocolError);
                            Disconnect(protocolError.Message);
                            break;
                        }
                        UpdateSessionSnapshot(result);
                        pending.Completion.TrySetResult(result);
                    }
                    else pending.Completion.TrySetException(new ProtocolValidationException("Response has no result or error."));
                }
                else if (map.TryGetValue("type", out type) && type is "hello" or "hello_error")
                {
                    var protocolError = new ProtocolValidationException("Unexpected handshake message after connection.");
                    Disconnect(protocolError.Message);
                    break;
                }
                else
                {
                    ApplyEventSnapshot(value);
                    Channel<object?> events;
                    lock (_eventGate) events = _events;
                    await events.Writer.WriteAsync(value).ConfigureAwait(false);
                    Action<object?>[] listeners; lock (_eventListeners) listeners = _eventListeners.ToArray();
                    foreach (var listener in listeners) try { listener(value); } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (!_disposed) { Disconnect(ex.Message); }
    }
    private async Task<object?> ReadRawAsync(CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null) return null;
        var value = CborCodec.Decode(frame);
        ProtocolMessages.ValidateServerMessage(value);
        return value;
    }
    private async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        await _rawReadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new RemoteSessionDisconnectedException();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                lock (_pendingFrames) if (_pendingFrames.Count > 0) return _pendingFrames.Dequeue();
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) { _decoder.End(); SetConnectionState(RemoteSessionConnectionState.Disconnected); return null; }
                var frames = _decoder.Push(buffer.AsSpan(0, read));
                if (frames.Count == 0) continue;
                lock (_pendingFrames) foreach (var frame in frames.Skip(1)) _pendingFrames.Enqueue(frame);
                return frames[0];
            }
        }
        finally { _rawReadLock.Release(); }
    }
    private SessionLeaseToken ReserveLease(string sessionId, RemoteSessionLeaseMode mode)
    {
        var count = _leaseCounts.GetValueOrDefault(sessionId);
        if (mode == RemoteSessionLeaseMode.Exclusive && count > 0) throw new RemoteSessionOwnershipException(sessionId, $"Session {sessionId} already has an active lease.");
        if (mode == RemoteSessionLeaseMode.Shared && _exclusiveLeases.Contains(sessionId)) throw new RemoteSessionOwnershipException(sessionId, $"Session {sessionId} has an exclusive lease.");
        var generation = _leaseGenerations.GetValueOrDefault(sessionId);
        var token = new SessionLeaseToken(mode, generation);
        _leaseCounts[sessionId] = count + 1;
        if (mode == RemoteSessionLeaseMode.Exclusive) _exclusiveLeases.Add(sessionId);
        return token;
    }
    private int LeaseCount(string id) { lock (_leaseGate) return _leaseCounts.GetValueOrDefault(id); }
    private bool IsCleanupRequired(string id) { lock (_leaseGate) return _cleanupRequired.Contains(id); }
    private void ReleaseLeaseBookkeeping(string id, SessionLeaseToken token) { var count = _leaseCounts.GetValueOrDefault(id); if (count <= 1) _leaseCounts.Remove(id); else _leaseCounts[id] = count - 1; if (token.Mode == RemoteSessionLeaseMode.Exclusive) _exclusiveLeases.Remove(id); }
    private void MarkSessionAttached(string id) { _cleanupRequired.Remove(id); }
    private void MarkSessionDetached(string id) { _cleanupRequired.Remove(id); _sessionSnapshots.Remove(id); }

    /// <summary>
    /// 将 command result 中的 session 快照写入客户端状态，供后续共享 lease 复用。
    /// </summary>
    /// <param name="result">服务端返回的 command result。</param>
    private void UpdateSessionSnapshot(IReadOnlyDictionary<string, object?> result)
    {
        if (!result.TryGetValue("session", out var rawSession) || rawSession is null)
            return;
        var snapshot = RemoteProtocolValues.Json(rawSession);
        if (snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("id", out var id) && id.GetString() is { } sessionId)
        {
            lock (_leaseGate) _sessionSnapshots[sessionId] = snapshot;
        }
    }

    /// <summary>
    /// 处理 session_snapshot 和 session_removed 事件对本地 lease 状态的影响。
    /// </summary>
    /// <param name="value">服务端事件 envelope。</param>
    private void ApplyEventSnapshot(object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?> envelope ||
            !envelope.TryGetValue("event", out var raw) ||
            raw is not IReadOnlyDictionary<string, object?> evt)
            return;
        if (evt.TryGetValue("snapshot", out var rawSnapshot) && rawSnapshot is IReadOnlyDictionary<string, object?> snapshotMap &&
            snapshotMap.TryGetValue("id", out var snapshotId) && snapshotId is string sessionId)
        {
            lock (_leaseGate) _sessionSnapshots[sessionId] = RemoteProtocolValues.Json(rawSnapshot);
        }
        if (evt.TryGetValue("type", out var eventType) && eventType is "server_snapshot" && evt.TryGetValue("snapshot", out var serverSnapshot))
            UpdateServerSnapshot(serverSnapshot);
        if (evt.TryGetValue("type", out var type) && type is "session_removed" &&
            evt.TryGetValue("sessionId", out var removedId) && removedId is string id)
        {
            lock (_leaseGate)
            {
                _leaseCounts.Remove(id);
                _exclusiveLeases.Remove(id);
                _cleanupRequired.Remove(id);
                _sessionSnapshots.Remove(id);
                _leaseGenerations[id] = _leaseGenerations.GetValueOrDefault(id) + 1;
            }
        }
    }

    /// <summary>
    /// 更新服务端全局快照缓存，供上层 session 外观读取 durable session/model 元数据。
    /// </summary>
    /// <param name="value">服务端快照 JSON 对象。</param>
    private void UpdateServerSnapshot(object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?>)
            return;
        lock (_leaseGate)
            _serverSnapshot = RemoteProtocolValues.Json(value);
    }
    private void EnsureLeaseActive(string id, int generation) { EnsureNotDisposed(); lock (_leaseGate) if (!IsLeaseActive(id, generation)) throw new RemoteSessionDetachedException(id); }
    private void EnsureLeaseGeneration(string id, int generation) { EnsureNotDisposed(); lock (_leaseGate) if (_leaseGenerations.GetValueOrDefault(id) != generation || !_leaseCounts.ContainsKey(id)) throw new RemoteSessionDetachedException(id); }
    private async Task ReconcileCleanupAsync(string id, CancellationToken cancellationToken)
    {
        Task reconciliation;
        lock (_leaseGate)
        {
            if (!_sessionReconciliations.TryGetValue(id, out reconciliation!))
            {
                reconciliation = ReconcileCleanupCoreAsync(id, cancellationToken);
                _sessionReconciliations[id] = reconciliation;
            }
        }
        try { await reconciliation.ConfigureAwait(false); }
        finally
        {
            lock (_leaseGate)
            {
                if (_sessionReconciliations.TryGetValue(id, out var current) && ReferenceEquals(current, reconciliation))
                    _sessionReconciliations.Remove(id);
            }
        }
    }

    /// <summary>
    /// 执行一次 attach，并把服务端快照提交到共享客户端状态。
    /// </summary>
    /// <param name="sessionId">要 attach 的 session id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端返回的 session 快照。</returns>
    private async Task<JsonElement> AttachSessionCoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        var result = await RequestAsync(new Dictionary<string, object?> { ["command"] = "attach", ["sessionId"] = sessionId }, cancellationToken).ConfigureAwait(false);
        var snapshot = SessionResult(result, "attach");
        lock (_leaseGate) _sessionSnapshots[sessionId] = snapshot;
        return snapshot;
    }

    /// <summary>
    /// 清理 dispose 失败留下的服务端 attachment，并在成功后清除补偿标记。
    /// </summary>
    /// <param name="sessionId">待清理的 session id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ReconcileCleanupCoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        await RequestAsync(new Dictionary<string, object?> { ["command"] = "detach", ["sessionId"] = sessionId }, cancellationToken).ConfigureAwait(false);
        lock (_leaseGate)
        {
            _cleanupRequired.Remove(sessionId);
            _sessionSnapshots.Remove(sessionId);
        }
    }
    private static JsonElement SessionResult(IReadOnlyDictionary<string, object?> result, string command)
    {
        if (!result.TryGetValue("session", out var raw)) throw new ProtocolValidationException($"{command} result has no session snapshot.");
        return RemoteProtocolValues.Json(raw);
    }
    private sealed record PendingRequest(string Command) { public TaskCompletionSource<IReadOnlyDictionary<string, object?>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    internal sealed record SessionLeaseToken(RemoteSessionLeaseMode Mode, int Generation);
    private sealed class DelegateSubscription(Action action) : IDisposable { public void Dispose() => action(); }
}

/// <summary>由 PiRemoteClient 管理的 session 租约。</summary>
public sealed class RemoteSessionLease : IAsyncDisposable
{
    private readonly PiRemoteClient _client;
    private readonly int _generation;
    private int _released;
    private JsonElement? _snapshot;

    internal RemoteSessionLease(PiRemoteClient client, string id, int generation, JsonElement? snapshot) { _client = client; Id = id; _generation = generation; _snapshot = snapshot; }
    /// <summary>session id。</summary>
    public string Id { get; }
    /// <summary>租约是否仍然有效。</summary>
    public bool Active => _client.IsLeaseActive(Id, _generation);
    /// <summary>session 是否 attached。</summary>
    public bool Attached => Active;
    /// <summary>最近一次 session snapshot。</summary>
    public JsonElement? Snapshot => _client.GetSessionSnapshot(Id) ?? _snapshot;
    /// <summary>订阅 session 事件。</summary>
    /// <param name="listener">事件回调。</param>
    /// <returns>取消订阅动作。</returns>
    public IDisposable OnEvent(Action<object?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        EnsureActive();
        return _client.SubscribeSessionEvents(Id, listener);
    }

    /// <summary>
    /// 订阅服务端推送的权威 session 快照。
    /// </summary>
    /// <param name="listener">收到快照后的回调。</param>
    /// <returns>取消订阅的句柄。</returns>
    public IDisposable Subscribe(Action<JsonElement> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        EnsureActive();
        return _client.SubscribeSessionSnapshots(Id, snapshot =>
        {
            _snapshot = snapshot;
            listener(snapshot);
        });
    }
    /// <summary>发送 prompt。</summary>
    /// <param name="text">提示文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端 session snapshot。</returns>
    public Task<JsonElement> PromptAsync(string text, CancellationToken cancellationToken = default) => RequestAsync("prompt", new Dictionary<string, object?> { ["text"] = text }, cancellationToken);
    /// <summary>发送 steer。</summary>
    /// <param name="text">引导文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端 session snapshot。</returns>
    public Task<JsonElement> SteerAsync(string text, CancellationToken cancellationToken = default) => RequestAsync("steer", new Dictionary<string, object?> { ["text"] = text }, cancellationToken);
    /// <summary>发送 abort。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端 session snapshot。</returns>
    public Task<JsonElement> AbortAsync(CancellationToken cancellationToken = default) => RequestAsync("abort", null, cancellationToken);
    /// <summary>切换模型。</summary>
    /// <param name="provider">provider。</param>
    /// <param name="model">模型 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端 session snapshot。</returns>
    public Task<JsonElement> SetModelAsync(string provider, string model, CancellationToken cancellationToken = default) => RequestAsync("set_model", new Dictionary<string, object?> { ["model"] = new Dictionary<string, object?> { ["provider"] = provider, ["id"] = model } }, cancellationToken);
    /// <summary>切换思考级别。</summary>
    /// <param name="thinkingLevel">思考级别。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端 session snapshot。</returns>
    public Task<JsonElement> SetThinkingAsync(string thinkingLevel, CancellationToken cancellationToken = default) => RequestAsync("set_thinking", new Dictionary<string, object?> { ["thinkingLevel"] = thinkingLevel }, cancellationToken);
    /// <summary>detach 租约，失败时保持 active 便于重试。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task DetachAsync(CancellationToken cancellationToken = default) => ReleaseAsync(false, cancellationToken);
    /// <summary>兼容同步命名的异步 detach。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task Detach(CancellationToken cancellationToken = default) => DetachAsync(cancellationToken);
    /// <summary>释放租约，失败时放弃本地 ownership 并登记补偿 detach。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task DisposeAsync(CancellationToken cancellationToken = default) => ReleaseAsync(true, cancellationToken);
    /// <summary>兼容同步命名的异步释放。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task Dispose(CancellationToken cancellationToken = default) => DisposeAsync(cancellationToken);
    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());
    private async Task<JsonElement> RequestAsync(string command, object? payload, CancellationToken cancellationToken) { var snapshot = await _client.SessionRequestAsync(Id, _generation, command, payload, cancellationToken).ConfigureAwait(false); _snapshot = snapshot; return snapshot; }
    private async Task ReleaseAsync(bool relinquishOnFailure, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try { await _client.ReleaseLeaseAsync(Id, _generation, relinquishOnFailure, cancellationToken).ConfigureAwait(false); }
        catch { if (!relinquishOnFailure) Volatile.Write(ref _released, 0); throw; }
    }

    /// <summary>校验租约仍处于活动状态，防止释放或失效后的句柄继续注册回调。</summary>
    private void EnsureActive()
    {
        if (_client.ConnectionState != RemoteSessionConnectionState.Connected)
            throw new RemoteSessionDisconnectedException();
        if (!_client.IsLeaseActive(Id, _generation))
            throw new RemoteSessionDetachedException(Id);
    }

    /// <summary>发送 prompt 的兼容别名。</summary>
    /// <param name="text">提示文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>session snapshot。</returns>
    public Task<JsonElement> Prompt(string text, CancellationToken cancellationToken = default) => PromptAsync(text, cancellationToken);
    /// <summary>发送 steer 的兼容别名。</summary>
    /// <param name="text">引导文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>session snapshot。</returns>
    public Task<JsonElement> Steer(string text, CancellationToken cancellationToken = default) => SteerAsync(text, cancellationToken);
    /// <summary>发送 abort 的兼容别名。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>session snapshot。</returns>
    public Task<JsonElement> Abort(CancellationToken cancellationToken = default) => AbortAsync(cancellationToken);
}

/// <summary>面向 Coding Agent 的远程 session 外观。</summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private readonly PiRemoteClient _client;
    private readonly List<JsonElement> _transcript = [];
    private readonly Dictionary<string, string> _toolCallBuffers = new(StringComparer.Ordinal);
    private readonly List<Action<RemoteSessionState>> _listeners = [];
    private readonly HashSet<Task> _attachments = [];
    private readonly HashSet<OperationFrame> _activeOperations = [];
    private readonly object _attachmentGate = new();
    private readonly TaskCompletionSource<bool> _disposeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RemoteSessionLifecycle _lifecycle = RemoteSessionLifecycle.Unbound;
    private RemoteSessionOperation? _operation;
    private OperationFrame? _currentOperation;
    private RemoteSessionLease? _lease;
    private JsonElement? _snapshot;
    private string? _phase;
    private IDisposable? _eventSubscription;
    private IDisposable? _snapshotSubscription;
    private Task? _disposeTask;
    private readonly object _disposeGate = new();
    private Action<Exception>? _listenerError;
    private string? _requestedSessionId;

    /// <summary>创建远程 session。</summary>
    /// <param name="client">远程客户端。</param>
    /// <param name="sessionId">服务端 session id。</param>
    public RemoteSession(PiRemoteClient client, string? sessionId = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        SessionId = sessionId;
        _requestedSessionId = sessionId;
    }
    /// <summary>打开指定 session 的工厂方法。</summary>
    /// <param name="client">远程客户端。</param>
    /// <param name="sessionId">session id。</param>
    /// <param name="listenerError">listener 异常诊断回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已绑定的远程 session。</returns>
    public static async Task<RemoteSession> OpenSessionAsync(PiRemoteClient client, string sessionId, Action<Exception>? listenerError = null, CancellationToken cancellationToken = default)
    {
        var session = new RemoteSession(client, sessionId) { _listenerError = listenerError };
        try { await session.OpenAsync(cancellationToken).ConfigureAwait(false); return session; }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <summary>兼容工厂命名：打开指定远程 session。</summary>
    /// <param name="client">远程客户端。</param>
    /// <param name="sessionId">session id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已绑定的远程 session。</returns>
    public static Task<RemoteSession> OpenAsync(PiRemoteClient client, string sessionId, CancellationToken cancellationToken = default) => OpenSessionAsync(client, sessionId, null, cancellationToken);
    /// <summary>创建远程 session 的工厂方法。</summary>
    /// <param name="client">远程客户端。</param>
    /// <param name="cwd">工作目录。</param>
    /// <param name="name">可选名称。</param>
    /// <param name="listenerError">listener 异常诊断回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已创建的远程 session。</returns>
    public static async Task<RemoteSession> CreateSessionAsync(PiRemoteClient client, string cwd, string? name = null, Action<Exception>? listenerError = null, CancellationToken cancellationToken = default)
    {
        var session = new RemoteSession(client) { _listenerError = listenerError };
        try { await session.CreateAsync(cwd, name, cancellationToken).ConfigureAwait(false); return session; }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <summary>兼容工厂命名：创建远程 session。</summary>
    /// <param name="client">远程客户端。</param>
    /// <param name="cwd">工作目录。</param>
    /// <param name="name">可选名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已创建的远程 session。</returns>
    public static Task<RemoteSession> CreateAsync(PiRemoteClient client, string cwd, string? name = null, CancellationToken cancellationToken = default) => CreateSessionAsync(client, cwd, name, null, cancellationToken);
    /// <summary>服务端 session id。</summary>
    public string? SessionId { get; private set; }
    /// <summary>当前状态。</summary>
    public RemoteSessionState State => new(_lifecycle, SessionId, _transcript.ToArray(), _operation, _snapshot, _phase, _client.ConnectionState);
    /// <summary>当前操作。</summary>
    public RemoteSessionOperation? Operation => _operation;
    /// <summary>服务端 phase。</summary>
    public string? Phase => _phase;
    /// <summary>服务端 snapshot。</summary>
    public JsonElement? Snapshot => SessionId is { } id ? _client.GetSessionSnapshot(id) ?? _snapshot : _snapshot;
    /// <summary>底层连接状态。</summary>
    public RemoteSessionConnectionState ConnectionState => _client.ConnectionState;
    /// <summary>服务端 session id 的兼容别名。</summary>
    public string? Id => SessionId;
    /// <summary>最近一次服务端握手快照中的模型元数据。</summary>
    public IReadOnlyList<JsonElement> Models => ReadServerSnapshotArray("models");
    /// <summary>最近一次服务端握手快照中的 session 元数据。</summary>
    public IReadOnlyList<JsonElement> Sessions => ReadServerSnapshotArray("sessions");
    /// <summary>是否已经释放。</summary>
    public bool IsDisposed => _lifecycle == RemoteSessionLifecycle.Disposed;
    /// <summary>是否已经释放。</summary>
    public bool Disposed => IsDisposed;

    /// <summary>订阅状态变化。</summary>
    /// <param name="listener">状态回调。</param>
    /// <returns>取消订阅句柄。</returns>
    public IDisposable Subscribe(Action<RemoteSessionState> listener)
    {
        ArgumentNullException.ThrowIfNull(listener); AssertNotDisposed(); lock (_listeners) _listeners.Add(listener); CallListener(listener, State); return new DelegateSubscription(() => { lock (_listeners) _listeners.Remove(listener); });
    }

    /// <summary>
    /// 订阅底层远程客户端的连接状态变化。
    /// </summary>
    /// <param name="listener">连接状态回调，参数为新的连接状态。</param>
    /// <returns>取消订阅句柄。</returns>
    public IDisposable OnConnectionStateChange(Action<RemoteSessionConnectionState> listener)
    {
        AssertNotDisposed();
        return _client.OnConnectionStateChange(listener);
    }
    /// <summary>连接并绑定 session。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_lease is not null && _lifecycle == RemoteSessionLifecycle.Ready) return Task.CompletedTask;
        return ReplaceAsync(RemoteSessionOperation.Open, () => _client.AcquireSessionAsync(_requestedSessionId ?? throw new InvalidOperationException("Session id is required"), RemoteSessionLeaseMode.Exclusive, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 绑定指定 id 的远程 session；当 session 曾因服务端移除而解绑时使用此重载重新打开。
    /// </summary>
    /// <param name="sessionId">需要绑定的 session id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成绑定后的异步任务。</returns>
    public Task OpenAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_lease is not null && string.Equals(_lease.Id, sessionId, StringComparison.Ordinal) && _lifecycle == RemoteSessionLifecycle.Ready)
            return Task.CompletedTask;
        return ReplaceAsync(
            RemoteSessionOperation.Open,
            () => _client.AcquireSessionAsync(sessionId, RemoteSessionLeaseMode.Exclusive, cancellationToken),
            cancellationToken);
    }
    /// <summary>创建远程会话。</summary>
    /// <param name="cwd">工作目录。</param>
    /// <param name="name">可选名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task CreateAsync(string cwd, string? name = null, CancellationToken cancellationToken = default) => ReplaceAsync(RemoteSessionOperation.Create, () => _client.CreateSessionAsync(cwd, name, cancellationToken), cancellationToken);
    /// <summary>提交消息，根据 phase 选择 prompt 或 steer。</summary>
    /// <param name="message">消息文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SubmitAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        AssertAvailable(); var lease = RequireLease(); if (_phase is not ("idle" or "turn")) throw new InvalidOperationException($"Session cannot accept input during {_phase ?? "unknown"} phase");
        var operation = BeginOperation(RemoteSessionOperation.Submit);
        try
        {
            _snapshot = await AwaitUntilDisposeAsync(_phase == "idle"
                ? lease.PromptAsync(message.Trim(), cancellationToken)
                : lease.SteerAsync(message.Trim(), cancellationToken)).ConfigureAwait(false);
            UpdateSnapshot();
        }
        finally { EndOperation(operation); }
    }
    /// <summary>中止当前操作，submit 等待时允许抢占。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        var preempt = _lifecycle == RemoteSessionLifecycle.Busy && _operation == RemoteSessionOperation.Submit;
        if (!preempt) AssertAvailable();
        var lease = RequireLease();
        if (_phase == "idle" && !preempt) return;
        var operation = BeginOperation(RemoteSessionOperation.Abort);
        try { _snapshot = await AwaitUntilDisposeAsync(lease.AbortAsync(cancellationToken)).ConfigureAwait(false); UpdateSnapshot(); }
        finally { EndOperation(operation); }
    }
    /// <summary>切换模型，仅允许 idle。</summary>
    /// <param name="provider">provider。</param>
    /// <param name="model">模型 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task SetModelAsync(string provider, string model, CancellationToken cancellationToken = default) => RunIdleAsync(RemoteSessionOperation.SetModel, () => RequireLease().SetModelAsync(provider, model, cancellationToken));
    /// <summary>切换思考级别，仅允许 idle。</summary>
    /// <param name="thinkingLevel">思考级别。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task SetThinkingAsync(string thinkingLevel, CancellationToken cancellationToken = default) => RunIdleAsync(RemoteSessionOperation.SetThinking, () => RequireLease().SetThinkingAsync(thinkingLevel, cancellationToken));
    /// <summary>detach 当前租约但保留对象。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DetachAsync(CancellationToken cancellationToken = default)
    {
        AssertAvailable();
        var lease = RequireLease();
        await lease.DetachAsync(cancellationToken).ConfigureAwait(false);
        _lease = null;
        SessionId = null;
        _snapshot = null;
        _phase = null;
        _eventSubscription?.Dispose();
        _eventSubscription = null;
        _snapshotSubscription?.Dispose();
        _snapshotSubscription = null;
        _lifecycle = RemoteSessionLifecycle.Unbound;
        Notify();
    }
    /// <summary>重连并重新 acquire/attach 当前 session。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        AssertNotDisposed();
        if (_lifecycle == RemoteSessionLifecycle.Busy)
            throw new InvalidOperationException($"Remote session is busy with {_operation}.");
        var id = RequireLease().Id;
        var operationFrame = BeginOperation(RemoteSessionOperation.Reconnect);
        var reconnect = ReconnectCoreAsync(id, cancellationToken);
        Track(reconnect);
        try
        {
            await AwaitUntilDisposeAsync(reconnect).ConfigureAwait(false);
        }
        finally { EndOperation(operationFrame); }
    }
    /// <summary>读取并应用一个 session 事件。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>收到的事件，流结束时返回 null。</returns>
    public async Task<JsonElement?> ReadEventAsync(CancellationToken cancellationToken = default)
    {
        var value = await _client.ReceiveEventAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : RemoteProtocolValues.Json(value);
    }
    /// <summary>释放 session 租约，不关闭借用的 client。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }
    private async Task DisposeCoreAsync()
    {
        _lifecycle = RemoteSessionLifecycle.Disposed;
        _operation = null;
        _disposeSignal.TrySetResult(true);
        _eventSubscription?.Dispose();
        _eventSubscription = null;
        _snapshotSubscription?.Dispose();
        _snapshotSubscription = null;
        var lease = _lease;
        _lease = null;
        SessionId = null;
        Notify();
        if (lease is not null) Track(lease.DisposeAsync());
        while (true)
        {
            Task[] cleanup;
            lock (_attachmentGate) cleanup = _attachments.ToArray();
            if (cleanup.Length == 0) break;
            await Task.WhenAll(cleanup.Select(SuppressExpectedDisposalFailure)).ConfigureAwait(false);
        }
        lock (_listeners) _listeners.Clear();
    }
    private async Task ReplaceAsync(RemoteSessionOperation operation, Func<Task<RemoteSessionLease>> acquire, CancellationToken cancellationToken)
    {
        AssertAvailable();
        if (_lease is not null && _phase is not null && _phase != "idle")
            throw new InvalidOperationException($"Cannot replace a session while session is {_phase}");
        var operationFrame = BeginOperation(operation);
        var replacement = ReplaceCoreAsync(acquire, cancellationToken);
        Track(replacement);
        try
        {
            await AwaitUntilDisposeAsync(replacement).ConfigureAwait(false);
        }
        finally { EndOperation(operationFrame); }
    }

    /// <summary>
    /// 执行 session 替换的完整异步链路，并保证 dispose 竞态期间新租约最终会被释放。
    /// </summary>
    /// <param name="acquire">获取新租约的异步操作。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>替换完成的异步任务。</returns>
    private async Task ReplaceCoreAsync(Func<Task<RemoteSessionLease>> acquire, CancellationToken cancellationToken)
    {
        RemoteSessionLease? next = null;
        try
        {
            next = await acquire().ConfigureAwait(false);
            if (IsDisposed)
            {
                await next.DisposeAsync().ConfigureAwait(false);
                throw new RemoteSessionDisposedException();
            }
            var previous = _lease;
            if (previous is not null && previous.Id != next.Id)
            {
                try
                {
                    await previous.DetachAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception detachError)
                {
                    try
                    {
                        await next.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("Failed to replace remote session attachment.", detachError, cleanupError);
                    }

                    throw;
                }
            }
            if (IsDisposed)
            {
                await next.DisposeAsync().ConfigureAwait(false);
                throw new RemoteSessionDisposedException();
            }
            Bind(next);
            next = null;
        }
        catch
        {
            if (next is not null && !ReferenceEquals(next, _lease))
            {
                try { await next.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// 重建底层连接、重新取得当前 session 的 exclusive 租约，并在 dispose 竞态下回收新租约。
    /// </summary>
    /// <param name="sessionId">需要重新绑定的 session id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重连和重新绑定完成的异步任务。</returns>
    private async Task ReconnectCoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _client.ReconnectAsync(cancellationToken).ConfigureAwait(false);
        RemoteSessionLease? next = await _client.AcquireSessionAsync(sessionId, RemoteSessionLeaseMode.Exclusive, cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDisposed)
            {
                await next.DisposeAsync().ConfigureAwait(false);
                throw new RemoteSessionDisposedException();
            }

            Bind(next);
            next = null!;
        }
        finally
        {
            if (next is not null)
            {
                try { await next.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
    }
    private async Task RunIdleAsync(RemoteSessionOperation operation, Func<Task<JsonElement>> action)
    {
        AssertAvailable();
        if (_phase != "idle") throw new InvalidOperationException($"Cannot perform {operation} while session is {_phase ?? "unknown"}");
        var operationFrame = BeginOperation(operation);
        try { _snapshot = await AwaitUntilDisposeAsync(action()).ConfigureAwait(false); UpdateSnapshot(); }
        finally { EndOperation(operationFrame); }
    }
    private void Bind(RemoteSessionLease lease)
    {
        _eventSubscription?.Dispose();
        _snapshotSubscription?.Dispose();
        _lease = lease;
        SessionId = lease.Id;
        _requestedSessionId = lease.Id;
        _snapshot = lease.Snapshot;
        UpdateSnapshot();
        _eventSubscription = lease.OnEvent(value => { ApplyEvent(value); Notify(); });
        _snapshotSubscription = lease.Subscribe(snapshot => { _snapshot = snapshot; UpdateSnapshot(); Notify(); });
        _lifecycle = RemoteSessionLifecycle.Ready;
        Notify();
    }
    private void ApplyEvent(object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?> envelope || !envelope.TryGetValue("event", out var raw) || raw is not IReadOnlyDictionary<string, object?> evt) return;
        if (evt.TryGetValue("type", out var type) && type is string eventType && eventType == "session_removed")
        {
            _eventSubscription?.Dispose();
            _eventSubscription = null;
            _snapshotSubscription?.Dispose();
            _snapshotSubscription = null;
            _lease = null;
            SessionId = null;
            _snapshot = null;
            _phase = null;
            if (_lifecycle != RemoteSessionLifecycle.Busy) _lifecycle = RemoteSessionLifecycle.Unbound;
            return;
        }
        if (evt.TryGetValue("snapshot", out var snapshot))
        {
            _snapshot = RemoteProtocolValues.Json(snapshot);
            UpdateSnapshot();
            return;
        }
        if (evt.TryGetValue("progress", out var progress)) ApplyTranscriptProgress(progress);
    }

    /// <summary>从权威 session snapshot 重建 transcript 投影。</summary>
    /// <param name="snapshot">服务端 session snapshot。</param>
    private void ReplaceTranscript(JsonElement snapshot)
    {
        _transcript.Clear();
        _toolCallBuffers.Clear();
        if (snapshot.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in transcript.EnumerateArray()) _transcript.Add(item.Clone());
        }
        if (snapshot.TryGetProperty("queuedSteer", out var queued) && queued.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in queued.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                if (_transcript.Any(existing => existing.ValueKind == JsonValueKind.Object && existing.TryGetProperty("id", out var existingId) && existingId.GetString() == id.GetString())) continue;
                _transcript.Add(item.Clone());
            }
        }
    }

    /// <summary>应用服务端增量 transcript progress，同时保留 snapshot 的权威数据不变。</summary>
    /// <param name="value">progress 协议对象。</param>
    private void ApplyTranscriptProgress(object? value)
    {
        var progress = RemoteProtocolValues.Map(value);
        if (!progress.TryGetValue("type", out var rawType) || rawType is not string type) return;
        if (type is "item_started" or "item_updated" or "item_finished")
        {
            if (!progress.TryGetValue("item", out var rawItem)) return;
            var item = RemoteProtocolValues.Json(rawItem);
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return;
            var idValue = id.GetString()!;
            var index = _transcript.FindIndex(existing => existing.ValueKind == JsonValueKind.Object && existing.TryGetProperty("id", out var existingId) && existingId.GetString() == idValue);
            if (index >= 0) _transcript[index] = item;
            else _transcript.Add(item);
            if (type == "item_finished")
            {
                foreach (var key in _toolCallBuffers.Keys.Where(key => key.StartsWith(idValue + ":", StringComparison.Ordinal)).ToArray())
                    _toolCallBuffers.Remove(key);
            }
            return;
        }
        if (type != "assistant_delta" || !progress.TryGetValue("messageId", out var messageIdValue) || messageIdValue is not string messageId ||
            !progress.TryGetValue("contentIndex", out var contentIndexValue) || !TryInt32(contentIndexValue, out var contentIndex) || contentIndex < 0 ||
            !progress.TryGetValue("kind", out var kindValue) || kindValue is not string kind ||
            !progress.TryGetValue("delta", out var deltaValue) || deltaValue is not string delta)
            return;

        var itemIndex = _transcript.FindIndex(existing => existing.ValueKind == JsonValueKind.Object && existing.TryGetProperty("id", out var existingId) && existingId.GetString() == messageId);
        if (itemIndex < 0) return;
        var itemNode = JsonNode.Parse(_transcript[itemIndex].GetRawText()) as JsonObject;
        if (itemNode?["role"]?.GetValue<string>() != "assistant" || itemNode["content"] is not JsonArray content || contentIndex >= content.Count)
            return;
        if (content[contentIndex] is not JsonObject part) return;
        if (kind == "text" && part["type"]?.GetValue<string>() == "text")
        {
            part["text"] = (part["text"]?.GetValue<string>() ?? string.Empty) + delta;
        }
        else if (kind == "thinking" && part["type"]?.GetValue<string>() == "thinking")
        {
            part["thinking"] = (part["thinking"]?.GetValue<string>() ?? string.Empty) + delta;
        }
        else if (kind == "toolCall" && part["type"]?.GetValue<string>() == "toolCall")
        {
            var key = messageId + ":" + contentIndex;
            var existingInput = part["input"] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var textInput) ? textInput : string.Empty;
            var buffer = _toolCallBuffers.GetValueOrDefault(key) ?? existingInput;
            buffer += delta;
            _toolCallBuffers[key] = buffer;
            try { part["input"] = JsonNode.Parse(buffer) ?? buffer; }
            catch (JsonException) { part["input"] = buffer; }
        }
        else return;
        _transcript[itemIndex] = JsonSerializer.SerializeToElement(itemNode);
    }

    private static bool TryInt32(object? value, out int result)
    {
        switch (value)
        {
            case int number: result = number; return true;
            case long number when number is >= int.MinValue and <= int.MaxValue: result = (int)number; return true;
            case JsonElement element when element.TryGetInt32(out result): return true;
            default: result = 0; return false;
        }
    }

    private void UpdateSnapshot()
    {
        if (_snapshot is not { } snapshot || snapshot.ValueKind != JsonValueKind.Object) return;
        if (snapshot.TryGetProperty("phase", out var phase)) _phase = phase.GetString();
        ReplaceTranscript(snapshot);
    }

    /// <summary>
    /// 从客户端缓存的服务端快照读取指定数组字段，并返回独立 JSON 副本。
    /// </summary>
    /// <param name="propertyName">快照数组字段名。</param>
    /// <returns>字段中的 JSON 元素列表；快照不存在或字段不是数组时返回空列表。</returns>
    private IReadOnlyList<JsonElement> ReadServerSnapshotArray(string propertyName)
    {
        var snapshot = _client.ServerSnapshot;
        if (snapshot is not { ValueKind: JsonValueKind.Object } ||
            !snapshot.Value.TryGetProperty(propertyName, out var values) ||
            values.ValueKind != JsonValueKind.Array)
            return [];
        return values.EnumerateArray().Select(static value => value.Clone()).ToArray();
    }
    private void Track(Task task)
    {
        lock (_attachmentGate) _attachments.Add(task);
        _ = task.ContinueWith(
            _ => { lock (_attachmentGate) _attachments.Remove(task); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
    private RemoteSessionLease RequireLease() => _lease ?? throw new RemoteSessionDetachedException(SessionId ?? "unknown");
    private void AssertNotDisposed() { if (IsDisposed) throw new RemoteSessionDisposedException(); }
    private void AssertAvailable() { AssertNotDisposed(); if (_lifecycle == RemoteSessionLifecycle.Busy) throw new InvalidOperationException($"Remote session is busy with {_operation}."); if (_client.ConnectionState != RemoteSessionConnectionState.Connected) throw new RemoteSessionDisconnectedException(); }
    private OperationFrame BeginOperation(RemoteSessionOperation operation)
    {
        var frame = new OperationFrame(operation);
        _activeOperations.Add(frame);
        _currentOperation = frame;
        _lifecycle = RemoteSessionLifecycle.Busy;
        _operation = operation;
        Notify();
        return frame;
    }

    private void EndOperation(OperationFrame frame)
    {
        _activeOperations.Remove(frame);
        if (IsDisposed || !ReferenceEquals(_currentOperation, frame)) return;
        if (_activeOperations.Count > 0)
        {
            var current = _activeOperations.Last();
            _currentOperation = current;
            _lifecycle = RemoteSessionLifecycle.Busy;
            _operation = current.Operation;
        }
        else
        {
            _currentOperation = null;
            _lifecycle = _lease is null ? RemoteSessionLifecycle.Unbound : RemoteSessionLifecycle.Ready;
            _operation = null;
        }
        Notify();
    }
    private async Task<T> AwaitUntilDisposeAsync<T>(Task<T> operation)
    {
        var completed = await Task.WhenAny(operation, _disposeSignal.Task).ConfigureAwait(false);
        if (completed == operation) return await operation.ConfigureAwait(false);
        throw new RemoteSessionDisposedException();
    }
    private async Task AwaitUntilDisposeAsync(Task operation)
    {
        var completed = await Task.WhenAny(operation, _disposeSignal.Task).ConfigureAwait(false);
        if (completed == operation) { await operation.ConfigureAwait(false); return; }
        throw new RemoteSessionDisposedException();
    }

    private static async Task SuppressExpectedDisposalFailure(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (RemoteSessionDisposedException) { }
    }
    private void Notify() { Action<RemoteSessionState>[] listeners; lock (_listeners) listeners = _listeners.ToArray(); foreach (var listener in listeners) CallListener(listener, State); }
    private void CallListener(Action<RemoteSessionState> listener, RemoteSessionState state) { try { listener(state); } catch (Exception ex) { try { _listenerError?.Invoke(ex); } catch { } } }
    private sealed class DelegateSubscription(Action action) : IDisposable { public void Dispose() => action(); }
    private sealed record OperationFrame(RemoteSessionOperation Operation);
}
