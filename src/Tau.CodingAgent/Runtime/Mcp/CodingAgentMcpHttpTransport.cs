// 作者：xxx
using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP HTTP 错误】保留状态、受限错误正文和认证质询供连接层决定恢复动作。</summary>
public sealed class CodingAgentMcpHttpException : IOException
{
    public HttpStatusCode Status { get; }
    public string Body { get; }
    public string? Authenticate { get; }
    public bool SessionExpired { get; }
    /// <summary>【CodingAgent】【MCP HTTP 错误】创建协议可识别的 HTTP 异常。</summary>
    /// <param name="status">状态码。</param><param name="message">说明。</param><param name="body">受限响应正文。</param>
    /// <param name="authenticate">认证质询。</param><param name="sessionExpired">是否为已过期会话。</param>
    public CodingAgentMcpHttpException(HttpStatusCode status, string message, string body = "", string? authenticate = null, bool sessionExpired = false)
        : base(message) { Status = status; Body = body; Authenticate = authenticate; SessionExpired = sessionExpired; }
}

/// <summary>【CodingAgent】【MCP HTTP 认证】允许连接层提供令牌并处理一次重新认证或扩大 scope 的质询。</summary>
public interface ICodingAgentMcpHttpAuth
{
    /// <summary>【CodingAgent】【MCP 质询能力】只有支持质询刷新或授权升级的处理器才重试认证响应。</summary>
    bool CanHandleUnauthorized => true;
    /// <summary>【CodingAgent】【MCP 令牌】读取当前有效令牌。</summary><param name="token">取消信号。</param><returns>令牌或空值。</returns>
    Task<string?> GetTokenAsync(CancellationToken token);
    /// <summary>【CodingAgent】【MCP 质询】处理服务端认证要求，随后传输重新读取令牌并重试一次。</summary>
    /// <param name="response">未释放的认证响应。</param><param name="usedToken">本次使用的令牌。</param><param name="token">取消信号。</param><returns>处理任务。</returns>
    Task OnUnauthorizedAsync(HttpResponseMessage response, string? usedToken, CancellationToken token);
}

/// <summary>【CodingAgent】【MCP HTTP】实现 Streamable HTTP、JSON/SSE 响应、会话标识和事件恢复。</summary>
public sealed class CodingAgentMcpHttpTransport : ICodingAgentMcpTransport
{
    private readonly Uri _url;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp, _openGet, _ownsAuth;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly ICodingAgentMcpHttpAuth? _auth;
    private readonly int _maxBytes, _maxRetries;
    private readonly TimeSpan _initialDelay, _maxDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _operationsGate = new();
    private readonly HashSet<Task> _operations = [];
    private Task? _disposeTask;
    private int _started, _closed, _getStarted;
    private string? _sessionId, _protocolVersion;
    public string? SessionId => _sessionId;
    public event Action<JsonElement>? Message;
    public event Action<Exception>? Error;
    public event Action? Closed;

    /// <summary>【CodingAgent】【MCP HTTP 参数】配置连接、认证和恢复策略，注入的 HttpClient 由调用方管理。</summary>
    /// <param name="url">MCP 地址。</param><param name="httpClient">可选 HTTP 客户端。</param><param name="headers">固定请求头。</param>
    /// <param name="auth">认证处理器。</param><param name="openGetStream">是否打开服务端通知流。</param><param name="maxMessageBytes">报文上限。</param>
    /// <param name="initialDelay">首次恢复间隔。</param><param name="maxDelay">指数间隔上限。</param><param name="maxRetries">恢复次数。</param>
    /// <param name="ownsAuth">是否由传输等待和释放认证处理器。</param>
    public CodingAgentMcpHttpTransport(Uri url, HttpClient? httpClient = null, IReadOnlyDictionary<string, string>? headers = null,
        ICodingAgentMcpHttpAuth? auth = null, bool openGetStream = true, int maxMessageBytes = 16 * 1024 * 1024,
        TimeSpan? initialDelay = null, TimeSpan? maxDelay = null, int maxRetries = 5, bool ownsAuth = false)
    {
        if (url.Scheme is not ("http" or "https")) throw new ArgumentException("MCP URL must be HTTP or HTTPS.", nameof(url));
        if (maxMessageBytes <= 0 || maxRetries < 0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _url = url; _ownsHttp = httpClient is null; _http = httpClient ?? TauHttpClientFactory.Create();
        if (_ownsHttp) _http.Timeout = Timeout.InfiniteTimeSpan;
        _headers = headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        _auth = auth; _ownsAuth = ownsAuth; _openGet = openGetStream; _maxBytes = maxMessageBytes; _maxRetries = maxRetries;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(1); _maxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>【CodingAgent】【MCP HTTP 启动】初始化本地状态，网络握手由 initialize 请求触发。</summary><param name="token">取消信号。</param><returns>启动任务。</returns>
    public Task StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_operationsGate)
        {
            if (_closed != 0 || _started != 0) throw new InvalidOperationException("MCP HTTP transport already started or closed.");
            _started = 1;
        }
        return Task.CompletedTask;
    }
    /// <summary>【CodingAgent】【MCP HTTP 协议】后续请求发送协商版本。</summary><param name="version">协议版本。</param>
    public void SetProtocolVersion(string version) => _protocolVersion = version;

    /// <summary>【CodingAgent】【MCP HTTP 请求】POST 报文，解析 JSON 或后台消费 SSE，通知接受空响应。</summary>
    /// <param name="message">JSON-RPC 报文。</param><param name="token">取消信号。</param><returns>发送完成任务。</returns>
    public Task SendAsync(JsonElement message, CancellationToken token = default)
    {
        lock (_operationsGate)
        {
            if (_started == 0 || _closed != 0) return Task.FromException(new IOException("MCP HTTP connection closed."));
            var copy = message.Clone();
            return RegisterOperation(() => SendCoreAsync(copy, token));
        }
    }

    /// <summary>【MCP】【HTTP 发送生命周期】发送正文及读取响应头、JSON 正文都属于关闭时必须排空的操作。</summary>
    /// <param name="message">独立报文。</param><param name="token">调用取消。</param><returns>发送及前台响应读取任务。</returns>
    private async Task SendCoreAsync(JsonElement message, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var response = await FetchAsync(HttpMethod.Post, message.GetRawText(), null, linked.Token).ConfigureAwait(false);
        var background = false;
        try
        {
            await CheckResponseAsync(response, linked.Token).ConfigureAwait(false); CaptureSession(response);
            var request = message.TryGetProperty("method", out var method) && message.TryGetProperty("id", out _);
            if (!request)
            {
                if (method.ValueKind == JsonValueKind.String && method.GetString() == "notifications/initialized" && _openGet && Interlocked.Exchange(ref _getStarted, 1) == 0)
                    Track(RunGetStreamAsync);
                return;
            }
            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent)
                throw new CodingAgentMcpHttpException(response.StatusCode, "MCP server accepted request without a response");
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == "application/json")
            {
                var bytes = await ReadBytesAsync(response.Content, _maxBytes, false, linked.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var item in document.RootElement.EnumerateArray()) Dispatch(CodingAgentMcpJsonRpc.Validate(item));
                else Dispatch(CodingAgentMcpJsonRpc.Validate(document.RootElement));
                return;
            }
            if (contentType != "text/event-stream") throw new CodingAgentMcpHttpException(response.StatusCode, "Unsupported MCP response content type: " + (contentType ?? "missing"));
            var id = message.GetProperty("id").Clone();
            background = Track(() => ConsumeResponseAsync(response, id));
        }
        finally { if (!background) response.Dispose(); }
    }

    /// <summary>【CodingAgent】【MCP HTTP 认证】生成请求头并对认证质询最多重试一次。</summary>
    /// <param name="method">HTTP 方法。</param><param name="body">可选 JSON 正文。</param><param name="lastId">恢复事件标识。</param>
    /// <param name="token">取消信号。</param><returns>由调用者释放的响应。</returns>
    private async Task<HttpResponseMessage> FetchAsync(HttpMethod method, string? body, string? lastId, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, _url);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            foreach (var pair in _headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            request.Headers.Remove("Accept"); request.Headers.TryAddWithoutValidation("Accept", method == HttpMethod.Get ? "text/event-stream" : "application/json, text/event-stream");
            SetHeader(request, "Mcp-Session-Id", _sessionId); SetHeader(request, "MCP-Protocol-Version", _protocolVersion); SetHeader(request, "Last-Event-ID", lastId);
            var credential = _auth is null ? null : await _auth.GetTokenAsync(token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(credential)) SetHeader(request, "Authorization", "Bearer " + credential);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (attempt > 0 || _auth is null || !_auth.CanHandleUnauthorized || method == HttpMethod.Delete || !(response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden && string.Join(",", response.Headers.WwwAuthenticate).Contains("insufficient_scope", StringComparison.Ordinal))) return response;
            try { await _auth.OnUnauthorizedAsync(response, credential, token).ConfigureAwait(false); }
            finally { response.Dispose(); }
        }
    }

    /// <summary>【CodingAgent】【MCP HTTP 恢复】响应流中断时用最后事件标识 GET 续接，无法恢复只失败原请求。</summary>
    /// <param name="initial">首次 POST 响应。</param><param name="id">原始请求标识。</param><returns>流消费任务。</returns>
    private async Task ConsumeResponseAsync(HttpResponseMessage initial, JsonElement id)
    {
        var cursor = new Cursor(); var answered = false; var response = initial; Exception? failure = null;
        for (var attempt = 0; ;)
        {
            if (response is not null)
            {
                try
                {
                    await ConsumeAsync(response, cursor, message =>
                    {
                        if (CodingAgentMcpJsonRpc.IsResponse(message) && message.TryGetProperty("id", out var replyId) &&
                            CodingAgentMcpJsonRpc.IdKey(id) == CodingAgentMcpJsonRpc.IdKey(replyId)) answered = true;
                    }).ConfigureAwait(false);
                    failure = null;
                }
                catch (Exception error) { failure = error; }
                finally { response.Dispose(); response = null; }
            }
            if (answered || Volatile.Read(ref _closed) != 0) return;
            if (failure is not null && !IsRetryable(failure) || cursor.LastId is null || attempt >= _maxRetries) break;
            if (cursor.Received) attempt = 0;
            cursor.Received = false;
            if (!await DelayAsync(attempt++, cursor.RetryMs).ConfigureAwait(false)) return;
            try { response = await OpenGetAsync(cursor.LastId).ConfigureAwait(false); }
            catch (Exception error) { failure = error; if (!IsRetryable(error)) break; }
        }
        if (Volatile.Read(ref _closed) == 0) Dispatch(JsonSerializer.SerializeToElement(new
        { jsonrpc = "2.0", id, error = new { code = -32603, message = "MCP response stream failed: " + (failure?.Message ?? "stream ended without a response") } }));
    }

    /// <summary>【CodingAgent】【MCP HTTP 通知流】初始化后接收服务端消息，支持退避恢复和不支持 GET 的服务器。</summary><returns>通知流任务。</returns>
    private async Task RunGetStreamAsync()
    {
        var cursor = new Cursor();
        for (var attempt = 0; Volatile.Read(ref _closed) == 0;)
        {
            try
            {
                using var response = await OpenGetAsync(cursor.LastId).ConfigureAwait(false);
                if (response is null) return;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await ConsumeAsync(response, cursor).ConfigureAwait(false);
                if (cursor.Received || System.Diagnostics.Stopwatch.GetElapsedTime(started) > _maxDelay) attempt = 0;
            }
            catch (Exception error)
            {
                if (Volatile.Read(ref _closed) != 0) return;
                if (!IsRetryable(error)) { ReportError(error); return; }
            }
            cursor.Received = false;
            if (attempt >= _maxRetries) { ReportError(new IOException("MCP server-to-client stream dropped and could not be reopened")); return; }
            if (!await DelayAsync(attempt++, cursor.RetryMs).ConfigureAwait(false)) return;
        }
    }

    /// <summary>【CodingAgent】【MCP HTTP GET】打开通知或恢复流，405 表示服务器不提供此能力。</summary>
    /// <param name="lastId">可选事件标识。</param><returns>活动响应或空值。</returns>
    private async Task<HttpResponseMessage?> OpenGetAsync(string? lastId)
    {
        var response = await FetchAsync(HttpMethod.Get, null, lastId, _lifetime.Token).ConfigureAwait(false);
        try
        {
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed) { response.Dispose(); return null; }
            await CheckResponseAsync(response, _lifetime.Token).ConfigureAwait(false); CaptureSession(response);
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new CodingAgentMcpHttpException(response.StatusCode, "Unsupported MCP GET response content type");
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    /// <summary>【CodingAgent】【MCP HTTP SSE】消费并验证数据事件，控制字段更新恢复游标。</summary>
    /// <param name="response">活动响应。</param><param name="cursor">恢复游标。</param><param name="observe">原请求完成观察器。</param><returns>消费任务。</returns>
    private async Task ConsumeAsync(HttpResponseMessage response, Cursor cursor, Action<JsonElement>? observe = null)
    {
        var stream = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);
        await CodingAgentMcpSseParser.ConsumeAsync(stream, (type, data) =>
        {
            cursor.Received = true;
            if (type is not (null or "message") || string.IsNullOrWhiteSpace(data)) return;
            try
            {
                using var document = JsonDocument.Parse(data);
                var message = CodingAgentMcpJsonRpc.Validate(document.RootElement);
                observe?.Invoke(message); Dispatch(message);
            }
            catch (Exception error) { ReportError(error); }
        }, id => cursor.LastId = id, delay => cursor.RetryMs = delay, _maxBytes, _lifetime.Token).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【MCP HTTP 错误】读取受限诊断正文并识别认证及会话失效。</summary>
    /// <param name="response">响应。</param><param name="token">取消信号。</param><returns>成功时完成，否则抛出 HTTP 异常。</returns>
    private async Task CheckResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var body = Encoding.UTF8.GetString(await ReadBytesAsync(response.Content, 8192, true, token).ConfigureAwait(false));
        var expired = response.StatusCode == HttpStatusCode.NotFound && _sessionId is not null;
        var message = response.StatusCode == HttpStatusCode.Unauthorized ? "MCP server requires authentication" : expired ? "MCP session expired" :
            $"MCP HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}";
        throw new CodingAgentMcpHttpException(response.StatusCode, message, body, string.Join(",", response.Headers.WwwAuthenticate), expired);
    }

    /// <summary>【CodingAgent】【MCP HTTP 有界正文】限制 JSON 和错误响应的内存占用。</summary>
    /// <param name="content">响应内容。</param><param name="limit">字节上限。</param><param name="truncate">超限时是否只保留前缀。</param>
    /// <param name="token">取消信号。</param><returns>受限字节。</returns>
    private static async Task<byte[]> ReadBytesAsync(HttpContent content, int limit, bool truncate, CancellationToken token)
    {
        var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int length;
        while ((length = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - output.Length + 1)), token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + length > limit)
            {
                if (!truncate) throw new IOException($"MCP HTTP message exceeds {limit} bytes");
                output.Write(buffer, 0, (int)(limit - output.Length)); break;
            }
            output.Write(buffer, 0, length);
        }
        return output.ToArray();
    }

    /// <summary>【MCP】【HTTP 操作登记】持有生命周期锁时登记操作，操作完成即移除，避免长期会话积累任务。</summary>
    /// <param name="operation">待执行操作。</param><returns>已纳入关闭屏障的操作任务。</returns>
    private Task RegisterOperation(Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(completion.Task);
        _ = RunOperationAsync(operation, completion);
        return completion.Task;
    }

    /// <summary>【MCP】【HTTP 操作启动】先登记完成信号，再运行可能同步完成的发送，所有退出路径都解除跟踪。</summary>
    /// <param name="operation">待执行操作。</param><param name="completion">已登记的完成信号。</param><returns>观察任务。</returns>
    private async Task RunOperationAsync(Func<Task> operation, TaskCompletionSource completion)
    {
        try { await operation().ConfigureAwait(false); completion.TrySetResult(); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { lock (_operationsGate) _operations.Remove(completion.Task); }
    }

    /// <summary>【CodingAgent】【MCP HTTP 任务】后台流与发送使用同一关闭屏障，关闭后拒绝接管响应。</summary>
    /// <param name="operation">流任务。</param><returns>是否已接管后台流及其响应释放责任。</returns>
    private bool Track(Func<Task> operation)
    {
        lock (_operationsGate)
        {
            if (_closed != 0) return false;
            RegisterOperation(() => Task.Run(async () =>
            {
                try { await operation().ConfigureAwait(false); }
                catch (Exception error) { if (Volatile.Read(ref _closed) == 0) ReportError(error); }
            }));
            return true;
        }
    }

    /// <summary>【CodingAgent】【MCP HTTP 关闭】原子停止接受请求，所有调用方共同等待同一次清理。</summary><returns>释放任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_operationsGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _closed = 1;
            return new(_disposeTask = DisposeCoreAsync(_operations.ToArray()));
        }
    }

    /// <summary>【MCP】【HTTP 关闭排空】取消并等待发送与流清理，再删除服务端会话和释放自有认证、客户端。</summary>
    /// <param name="operations">关闭前原子捕获的全部操作。</param><returns>关闭任务。</returns>
    private async Task DisposeCoreAsync(Task[] operations)
    {
        await Task.Yield();
        List<Exception> failures = [];
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        // 1. 【MCP】【网络排空】失败由原调用方或后台观察器处理，关闭只等待其资源释放
        try { await Task.WhenAll(operations).ConfigureAwait(false); } catch (Exception) { }
        if (_sessionId is not null && Volatile.Read(ref _started) != 0)
        {
            using var deletion = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { using var response = await FetchAsync(HttpMethod.Delete, null, null, deletion.Token).ConfigureAwait(false); } catch { }
        }
        // 2. 【MCP】【异常回收】单项释放失败不能跳过其余自有资源，也不能遗漏连接关闭通知
        try { if (_ownsAuth && _auth is IAsyncDisposable disposableAuth) await disposableAuth.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { if (_ownsHttp) _http.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        try { _lifetime.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        try { Closed?.Invoke(); } catch (Exception error) { ReportError(error); }
        // 3. 【MCP】【关闭结果】所有调用方等待同一清理任务，保留单个异常堆栈并汇总同时发生的失败
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("MCP HTTP transport cleanup failed.", failures);
    }

    /// <summary>【CodingAgent】【MCP HTTP 退避】等待服务端指定或指数间隔，关闭时立即停止。</summary>
    /// <param name="attempt">恢复序号。</param><param name="serverMs">服务端毫秒间隔。</param><returns>是否继续。</returns>
    private async Task<bool> DelayAsync(int attempt, long? serverMs)
    {
        var milliseconds = serverMs ?? Math.Min(_initialDelay.TotalMilliseconds * Math.Pow(2, attempt), _maxDelay.TotalMilliseconds);
        try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 0, uint.MaxValue - 1)), _lifetime.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }
    /// <summary>【CodingAgent】【MCP HTTP 重试】认证、会话和报文错误不重试，只处理临时 HTTP 或网络错误。</summary>
    /// <param name="error">失败异常。</param><returns>是否可恢复。</returns>
    private static bool IsRetryable(Exception error) => error is CodingAgentMcpHttpException http
        ? (int)http.Status is 408 or 429 || (int)http.Status >= 500 && (int)http.Status != 501
        : error is HttpRequestException || error is IOException && !error.Message.StartsWith("MCP ", StringComparison.Ordinal);
    /// <summary>【CodingAgent】【MCP HTTP 会话】保存服务端新会话标识。</summary><param name="response">响应。</param>
    private void CaptureSession(HttpResponseMessage response) { if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) _sessionId = values.FirstOrDefault(value => value.Length > 0) ?? _sessionId; }
    /// <summary>【CodingAgent】【MCP HTTP 请求头】大小写无关地覆盖协议字段。</summary><param name="request">请求。</param><param name="name">字段。</param><param name="value">值。</param>
    private static void SetHeader(HttpRequestMessage request, string name, string? value) { if (value is null) return; request.Headers.Remove(name); request.Headers.TryAddWithoutValidation(name, value); }
    /// <summary>【CodingAgent】【MCP HTTP 消息】分发独立 JSON 并隔离监听器异常。</summary><param name="message">消息。</param>
    private void Dispatch(JsonElement message)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try { Message?.Invoke(CodingAgentMcpJsonRpc.Validate(message).Clone()); } catch (Exception error) { ReportError(error); }
    }
    /// <summary>【CodingAgent】【MCP HTTP 诊断】隔离诊断监听器异常。</summary><param name="error">异常。</param>
    private void ReportError(Exception error) { try { Error?.Invoke(error); } catch { } }
    /// <summary>【CodingAgent】【MCP SSE 游标】保存单个流的最后事件和恢复间隔。</summary>
    private sealed class Cursor { internal string? LastId; internal long? RetryMs; internal bool Received; }
}
