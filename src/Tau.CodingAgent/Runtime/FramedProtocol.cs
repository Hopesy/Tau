using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Tau.CodingAgent.Runtime;

/// <summary>上游 protocol 的长度前缀配置。</summary>
public sealed record FramedProtocolOptions(int MaxFrameLength = 16 * 1024 * 1024);

/// <summary>
/// 实现 4 字节 big-endian 长度前缀的 framed byte 编解码。
/// </summary>
public static class FramedProtocol
{
    /// <summary>为 payload 添加无符号 32 位 big-endian 长度前缀。</summary>
    /// <param name="payload">待封装字节。</param>
    /// <returns>完整 frame。</returns>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    /// <summary>将对象序列化为 UTF-8 JSON 并添加 framed 长度前缀。</summary>
    /// <param name="value">协议对象。</param>
    /// <returns>完整 frame。</returns>
    public static byte[] EncodeJson<T>(T value) => Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    /// <summary>将协议对象编码为严格 CBOR framed 消息。</summary>
    /// <param name="value">协议对象。</param>
    /// <param name="options">frame 和 CBOR 限制。</param>
    /// <returns>完整 frame。</returns>
    public static byte[] EncodeCbor(object? value, FramedProtocolOptions? options = null) => Encode(CborCodec.Encode(value, new CborOptions((options ?? new()).MaxFrameLength)));

    /// <summary>
    /// 校验并编码一个 client protocol 消息。
    /// </summary>
    /// <param name="message">待发送的 client hello 或 request 消息。</param>
    /// <param name="options">协议帧长度限制。</param>
    /// <returns>完整的 CBOR framed 消息。</returns>
    public static byte[] EncodeClientMessage(object? message, FramedProtocolOptions? options = null)
    {
        var resolved = options ?? new FramedProtocolOptions();
        ProtocolMessages.ValidateClientMessage(message);
        return EncodeCbor(message, resolved);
    }

    /// <summary>
    /// 校验并编码一个 server protocol 消息。
    /// </summary>
    /// <param name="message">待发送的 server hello、response 或 event 消息。</param>
    /// <param name="options">协议帧长度限制。</param>
    /// <returns>完整的 CBOR framed 消息。</returns>
    public static byte[] EncodeServerMessage(object? message, FramedProtocolOptions? options = null)
    {
        var resolved = options ?? new FramedProtocolOptions();
        ProtocolMessages.ValidateServerMessage(message);
        return EncodeCbor(message, resolved);
    }

    /// <summary>校验并读取恰好一个完整 frame。</summary>
    /// <param name="frame">完整 frame 字节。</param>
    /// <param name="options">协议限制。</param>
    public static void AssertCompleteFrame(ReadOnlySpan<byte> frame, FramedProtocolOptions? options = null)
    {
        if (frame.Length < 4) throw new InvalidDataException("Frame does not contain a complete length prefix.");
        var length = ReadLength(frame[..4], options);
        if (frame.Length != length + 4) throw new InvalidDataException("Frame must contain exactly one complete payload.");
    }

    /// <summary>读取一个完整 frame 的长度并校验最大尺寸。</summary>
    /// <param name="header">4 字节长度头。</param>
    /// <param name="options">协议限制。</param>
    /// <returns>payload 长度。</returns>
    public static int ReadLength(ReadOnlySpan<byte> header, FramedProtocolOptions? options = null)
    {
        if (header.Length != 4) throw new InvalidDataException("Frame header must contain four bytes.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        var maximum = options?.MaxFrameLength ?? 16 * 1024 * 1024;
        if (length > maximum) throw new InvalidDataException($"Frame length {length} exceeds limit {maximum}.");
        return checked((int)length);
    }
}

/// <summary>可增量消费任意网络分片的 framed decoder。</summary>
public sealed class FramedProtocolDecoder
{
    private readonly FramedProtocolOptions _options;
    private readonly List<byte> _buffer = [];
    private bool _ended;
    private bool _failed;
    /// <summary>创建 decoder。</summary>
    /// <param name="options">协议限制。</param>
    public FramedProtocolDecoder(FramedProtocolOptions? options = null) => _options = options ?? new FramedProtocolOptions();
    /// <summary>追加网络分片并返回已完成的 payload。</summary>
    /// <param name="chunk">网络字节。</param>
    /// <returns>完整 payload 列表。</returns>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> chunk)
    {
        if (_ended) throw new InvalidDataException("Frame decoder has ended.");
        if (_failed) throw new InvalidDataException("Frame decoder has failed.");
        foreach (var value in chunk) _buffer.Add(value);
        var frames = new List<byte[]>();
        while (_buffer.Count >= 4)
        {
            int length;
            try { length = FramedProtocol.ReadLength(CollectionsMarshal.AsSpan(_buffer)[..4], _options); }
            catch { _failed = true; throw; }
            if (_buffer.Count < length + 4) break;
            var payload = _buffer.Skip(4).Take(length).ToArray();
            _buffer.RemoveRange(0, length + 4);
            frames.Add(payload);
        }
        return frames;
    }

    /// <summary>标记底层字节流结束，并拒绝未完成的 header/body。</summary>
    public void End()
    {
        if (_ended) throw new InvalidDataException("Frame decoder has ended.");
        if (_failed) throw new InvalidDataException("Frame decoder has failed.");
        if (_buffer.Count != 0) { _failed = true; throw new InvalidDataException("Truncated frame at end of stream."); }
        _ended = true;
    }
}

/// <summary>协议消息校验失败异常。</summary>
public sealed class ProtocolValidationException : Exception
{
    /// <summary>创建协议校验异常。</summary>
    public ProtocolValidationException(string message) : base(message) { }
}

/// <summary>增量解码 CBOR client 消息。</summary>
public sealed class ClientMessageDecoder
{
    private readonly FramedProtocolDecoder _frames;
    private bool _failed;
    /// <summary>创建 client decoder。</summary>
    public ClientMessageDecoder(FramedProtocolOptions? options = null) => _frames = new(options);
    /// <summary>追加字节并返回已验证消息。</summary>
    public IReadOnlyList<object?> Push(ReadOnlySpan<byte> chunk) => Decode(chunk, "client");
    /// <summary>结束并检查截断。</summary>
    public void End()
    {
        if (_failed) throw new ProtocolValidationException("client message decoder has failed");
        try { _frames.End(); }
        catch (Exception ex)
        {
            _failed = true;
            throw new ProtocolValidationException($"Invalid client protocol framing: {ex.Message}");
        }
    }
    private IReadOnlyList<object?> Decode(ReadOnlySpan<byte> chunk, string kind)
    {
        if (_failed) throw new ProtocolValidationException($"{kind} message decoder has failed");
        try
        {
            var result = new List<object?>();
            foreach (var frame in _frames.Push(chunk))
            {
                var value = CborCodec.Decode(frame);
                ProtocolMessages.Validate(value, kind);
                result.Add(value);
            }
            return result;
        }
        catch (Exception ex)
        {
            _failed = true;
            if (ex is ProtocolValidationException protocolError) throw protocolError;
            throw new ProtocolValidationException($"Invalid {kind} protocol frame: {ex.Message}");
        }
    }

    internal static void ValidateEnvelope(object? value, string kind) => ProtocolMessages.Validate(value, kind);
}

/// <summary>增量解码 CBOR server 消息。</summary>
public sealed class ServerMessageDecoder
{
    private readonly FramedProtocolDecoder _frames;
    private bool _failed;
    /// <summary>创建 server decoder。</summary>
    public ServerMessageDecoder(FramedProtocolOptions? options = null) => _frames = new(options);
    /// <summary>追加字节并返回已验证消息。</summary>
    public IReadOnlyList<object?> Push(ReadOnlySpan<byte> chunk)
    {
        if (_failed) throw new ProtocolValidationException("server message decoder has failed");
        try
        {
            var result = new List<object?>();
            foreach (var frame in _frames.Push(chunk))
            {
                var value = CborCodec.Decode(frame);
                ProtocolMessages.Validate(value, "server");
                result.Add(value);
            }
            return result;
        }
        catch (Exception ex)
        {
            _failed = true;
            if (ex is ProtocolValidationException protocolError) throw protocolError;
            throw new ProtocolValidationException($"Invalid server protocol frame: {ex.Message}");
        }
    }
    /// <summary>结束并检查截断。</summary>
    public void End()
    {
        if (_failed) throw new ProtocolValidationException("server message decoder has failed");
        try { _frames.End(); }
        catch (Exception ex)
        {
            _failed = true;
            throw new ProtocolValidationException($"Invalid server protocol framing: {ex.Message}");
        }
    }
}

/// <summary>远程 session 的连接生命周期。</summary>
public enum RemoteSessionLifecycle { Unbound, Ready, Busy, Disposed }

/// <summary>远程会话当前执行的操作。</summary>
public enum RemoteSessionOperation { Open, Create, Submit, Abort, SetModel, SetThinking, Reconnect }

/// <summary>远程 session 的轻量状态快照。</summary>
public sealed record RemoteSessionState(RemoteSessionLifecycle Lifecycle, string? SessionId, IReadOnlyList<JsonElement> Transcript, RemoteSessionOperation? Operation = null);

/// <summary>
/// 基于任意 Stream 的远程 framed client，提供连接、发送命令和事件读取能力。
/// </summary>
public sealed class PiRemoteClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly FramedProtocolDecoder _decoder;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;
    private readonly Queue<byte[]> _pendingFrames = new();
    /// <summary>创建远程客户端。</summary>
    /// <param name="stream">双向网络流。</param>
    /// <param name="options">协议限制。</param>
    public PiRemoteClient(Stream stream, FramedProtocolOptions? options = null) { _stream = stream ?? throw new ArgumentNullException(nameof(stream)); _decoder = new FramedProtocolDecoder(options); }
    /// <summary>发送一个 JSON 协议命令。</summary>
    /// <param name="command">命令对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SendAsync<T>(T command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = FramedProtocol.EncodeJson(command);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false); await _stream.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    /// <summary>发送严格 CBOR 协议消息。</summary>
    /// <param name="message">协议对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SendCborAsync(object? message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = FramedProtocol.EncodeClientMessage(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false); await _stream.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }
    /// <summary>读取下一个完整 JSON 消息。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>JSON 元素，流结束时返回 null。</returns>
    public async Task<JsonElement?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            if (_pendingFrames.Count > 0)
            {
                using var pendingDocument = JsonDocument.Parse(_pendingFrames.Dequeue());
                return pendingDocument.RootElement.Clone();
            }
            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) { _decoder.End(); return null; }
            var frames = _decoder.Push(buffer.AsSpan(0, read));
            if (frames.Count == 0) continue;
            foreach (var frame in frames.Skip(1)) _pendingFrames.Enqueue(frame);
            using var document = JsonDocument.Parse(frames[0]);
            return document.RootElement.Clone();
        }
    }

    /// <summary>读取下一个严格 CBOR 消息，流结束时返回 null。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码后的协议对象。</returns>
    public async Task<object?> ReceiveCborAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            if (_pendingFrames.Count > 0)
            {
                var pending = CborCodec.Decode(_pendingFrames.Dequeue());
                ProtocolMessages.ValidateServerMessage(pending);
                return pending;
            }
            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) { _decoder.End(); return null; }
            var frames = _decoder.Push(buffer.AsSpan(0, read));
            if (frames.Count == 0) continue;
            foreach (var frame in frames.Skip(1)) _pendingFrames.Enqueue(frame);
            var value = CborCodec.Decode(frames[0]);
            ProtocolMessages.ValidateServerMessage(value);
            return value;
        }
    }
    /// <summary>释放远程客户端和底层流。</summary>
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await _stream.DisposeAsync().ConfigureAwait(false); _writeLock.Dispose(); }
}

/// <summary>面向 Coding Agent 的远程 session 外观。</summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private readonly PiRemoteClient _client;
    private readonly List<JsonElement> _transcript = [];
    private RemoteSessionLifecycle _lifecycle = RemoteSessionLifecycle.Unbound;
    private RemoteSessionOperation? _operation;
    private readonly List<Action<RemoteSessionState>> _listeners = [];
    /// <summary>创建远程 session。</summary>
    /// <param name="client">远程 framed client。</param>
    /// <param name="sessionId">服务端 session id。</param>
    public RemoteSession(PiRemoteClient client, string? sessionId = null) { _client = client ?? throw new ArgumentNullException(nameof(client)); SessionId = sessionId; }
    /// <summary>服务端 session id。</summary>
    public string? SessionId { get; private set; }
    /// <summary>当前状态快照。</summary>
    public RemoteSessionState State => new(_lifecycle, SessionId, _transcript.ToArray(), _operation);
    /// <summary>当前远程操作。</summary>
    public RemoteSessionOperation? Operation => _operation;
    /// <summary>会话是否已经释放。</summary>
    public bool IsDisposed => _lifecycle == RemoteSessionLifecycle.Disposed;
    /// <summary>订阅状态变化。</summary>
    /// <param name="listener">状态回调。</param>
    /// <returns>取消订阅句柄。</returns>
    public IDisposable Subscribe(Action<RemoteSessionState> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_listeners) _listeners.Add(listener);
        listener(State);
        return new DelegateSubscription(() => { lock (_listeners) _listeners.Remove(listener); });
    }
    /// <summary>连接并绑定 session。</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(RemoteSessionOperation.Open, async () => { await _client.SendCborAsync(new Dictionary<string, object?> { ["type"] = "request", ["id"] = Guid.NewGuid().ToString("N"), ["request"] = new Dictionary<string, object?> { ["command"] = "attach", ["sessionId"] = SessionId ?? throw new InvalidOperationException("Session id is required") } }, cancellationToken).ConfigureAwait(false); _lifecycle = RemoteSessionLifecycle.Ready; }).ConfigureAwait(false);
    }
    /// <summary>创建远程会话。</summary>
    /// <param name="cwd">工作目录。</param>
    /// <param name="name">可选名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CreateAsync(string cwd, string? name = null, CancellationToken cancellationToken = default)
    {
        await RunAsync(RemoteSessionOperation.Create, async () =>
        {
            var request = new Dictionary<string, object?> { ["command"] = "create", ["cwd"] = cwd };
            if (name is not null) request["name"] = name;
            await _client.SendCborAsync(new Dictionary<string, object?>
            {
                ["type"] = "request",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["request"] = request
            }, cancellationToken).ConfigureAwait(false);
            _lifecycle = RemoteSessionLifecycle.Ready;
        }).ConfigureAwait(false);
    }
    /// <summary>提交一条用户消息。</summary>
    /// <param name="message">消息文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SubmitAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        await RunAsync(RemoteSessionOperation.Submit, () => _client.SendCborAsync(new { type = "request", id = Guid.NewGuid().ToString("N"), request = new { command = "prompt", sessionId = SessionId, text = message.Trim() } }, cancellationToken)).ConfigureAwait(false);
    }
    /// <summary>请求服务端中止当前操作。</summary>
    public Task AbortAsync(CancellationToken cancellationToken = default) => RunAsync(RemoteSessionOperation.Abort, () => _client.SendCborAsync(new { type = "request", id = Guid.NewGuid().ToString("N"), request = new { command = "abort", sessionId = SessionId } }, cancellationToken));
    /// <summary>切换会话模型。</summary>
    /// <param name="provider">模型 provider。</param>
    /// <param name="model">模型 id。</param>
    public Task SetModelAsync(string provider, string model, CancellationToken cancellationToken = default) => RunAsync(RemoteSessionOperation.SetModel, () => _client.SendCborAsync(new { type = "request", id = Guid.NewGuid().ToString("N"), request = new { command = "set_model", sessionId = SessionId, model = new { provider, id = model } } }, cancellationToken));
    /// <summary>切换会话思考级别。</summary>
    /// <param name="thinkingLevel">思考级别字符串。</param>
    public Task SetThinkingAsync(string thinkingLevel, CancellationToken cancellationToken = default) => RunAsync(RemoteSessionOperation.SetThinking, () => _client.SendCborAsync(new { type = "request", id = Guid.NewGuid().ToString("N"), request = new { command = "set_thinking", sessionId = SessionId, thinkingLevel } }, cancellationToken));
    /// <summary>重连并重新打开当前会话。</summary>
    public Task ReconnectAsync(CancellationToken cancellationToken = default) => RunAsync(RemoteSessionOperation.Reconnect, () => _client.SendCborAsync(new { type = "request", id = Guid.NewGuid().ToString("N"), request = new { command = "attach", sessionId = SessionId } }, cancellationToken));
    /// <summary>读取并应用一个 transcript 事件。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>收到的事件，流结束时为 null。</returns>
    public async Task<JsonElement?> ReadEventAsync(CancellationToken cancellationToken = default) { var value = await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false); if (value is JsonElement eventValue) { _transcript.Add(eventValue); _lifecycle = RemoteSessionLifecycle.Ready; _operation = null; Notify(); } return value; }
    /// <summary>释放 session 和远程连接。</summary>
    public async ValueTask DisposeAsync() { if (IsDisposed) return; _lifecycle = RemoteSessionLifecycle.Disposed; _operation = null; Notify(); await _client.DisposeAsync().ConfigureAwait(false); }

    private async Task RunAsync(RemoteSessionOperation operation, Func<Task> action)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_lifecycle == RemoteSessionLifecycle.Busy) throw new InvalidOperationException($"Remote session is busy with {_operation}.");
        _lifecycle = RemoteSessionLifecycle.Busy; _operation = operation; Notify();
        try { await action().ConfigureAwait(false); }
        finally { if (!IsDisposed) { _lifecycle = RemoteSessionLifecycle.Ready; _operation = null; Notify(); } }
    }
    private void Notify()
    {
        Action<RemoteSessionState>[] listeners;
        lock (_listeners) listeners = _listeners.ToArray();
        foreach (var listener in listeners) try { listener(State); } catch { }
    }
    private sealed class DelegateSubscription(Action action) : IDisposable { public void Dispose() => action(); }
}
