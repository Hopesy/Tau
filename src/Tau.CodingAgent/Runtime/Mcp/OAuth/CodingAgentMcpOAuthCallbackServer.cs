// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【OAuth 回调结果】已匹配 state 和路径的授权响应，发行方由交换流程进一步校验。</summary>
public sealed record CodingAgentMcpOAuthCallback(string Code, string State, string? Issuer);
/// <summary>【CodingAgent】【OAuth 回调页面】用于宿主自定义浏览器结果页。</summary>
public sealed record CodingAgentMcpOAuthCallbackPage(bool Ok, string Message, string? Details = null);
/// <summary>【CodingAgent】【OAuth 回调选项】限定回环监听、允许路径、等待时间和可选 HTML 渲染。</summary>
public sealed record CodingAgentMcpOAuthCallbackOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public string? RedirectHost { get; init; }
    public int Port { get; init; }
    public string Path { get; init; } = "/callback";
    public IReadOnlyList<string> ExtraPaths { get; init; } = [];
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public Func<CodingAgentMcpOAuthCallbackPage, string>? RenderPage { get; init; }
}

/// <summary>【CodingAgent】【OAuth 回环服务】直接绑定回环 TCP，避免临时端口选择竞态和 Windows HTTP URL 预留要求。</summary>
public sealed class CodingAgentMcpOAuthCallbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CodingAgentMcpOAuthCallbackOptions _options;
    private readonly HashSet<string> _paths;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly List<Task> _clients = [];
    private readonly Task _accept;
    private bool _closed;
    private Task? _dispose;
    public string RedirectUrl { get; }

    /// <summary>【CodingAgent】【OAuth 回环实例】发布已绑定的端口后启动接收循环。</summary><param name="listener">已监听套接字。</param><param name="options">选项。</param>
    private CodingAgentMcpOAuthCallbackServer(TcpListener listener, CodingAgentMcpOAuthCallbackOptions options)
    {
        _listener = listener; _options = options; _paths = new(options.ExtraPaths, StringComparer.Ordinal) { options.Path };
        RedirectUrl = new UriBuilder("http", options.RedirectHost ?? options.Host, ((IPEndPoint)listener.LocalEndpoint).Port, options.Path).Uri.AbsoluteUri;
        _accept = AcceptAsync();
    }

    /// <summary>【CodingAgent】【OAuth 回环监听】端口零由操作系统分配，拒绝对外地址和无效路径。</summary><param name="options">可选监听配置。</param><returns>已启动服务。</returns>
    public static CodingAgentMcpOAuthCallbackServer Listen(CodingAgentMcpOAuthCallbackOptions? options = null)
    {
        options ??= new();
        var host = options.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(options.Host.Trim('[', ']'));
        if (!IPAddress.IsLoopback(host)) throw new ArgumentException("OAuth callback must listen on loopback.", nameof(options));
        if (options.Port is < 0 or > 65535 || options.Timeout <= TimeSpan.Zero || options.Timeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (new[] { options.Path }.Concat(options.ExtraPaths).Any(path => !path.StartsWith('/') || path.Contains('?') || path.Contains('#')))
            throw new ArgumentException("OAuth callback paths must be absolute paths without query or fragment.", nameof(options));
        var listener = new TcpListener(host, options.Port);
        try { listener.Start(); return new(listener, options); }
        catch { listener.Stop(); throw; }
    }

    /// <summary>【CodingAgent】【OAuth 回调等待】每个 state 只能等待一次，支持指定服务器独立路径、超时和取消。</summary>
    /// <param name="state">预期随机值。</param><param name="path">可选精确路径。</param><param name="token">取消。</param><returns>授权响应。</returns>
    public async Task<CodingAgentMcpOAuthCallback> WaitAsync(string state, string? path = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var pending = new Pending(path);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_pending.TryAdd(state, pending)) throw new InvalidOperationException("OAuth state is already pending");
        }
        try { return await pending.Completion.Task.WaitAsync(_options.Timeout, token).ConfigureAwait(false); }
        finally { lock (_gate) if (_pending.GetValueOrDefault(state) == pending) _pending.Remove(state); }
    }

    /// <summary>【CodingAgent】【OAuth 回环接收】跟踪全部连接，关闭监听后统一等待退出。</summary><returns>接收循环。</returns>
    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_closed) { client.Dispose(); break; }
                    _clients.RemoveAll(task => task.IsCompleted);
                    _clients.Add(HandleAsync(client));
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                lock (_gate)
                {
                    foreach (var pending in _pending.Values) pending.Completion.TrySetException(error);
                    _pending.Clear();
                }
            }
        }
    }

    /// <summary>【CodingAgent】【OAuth 回环请求】限制请求头读取时间和大小，解析一次请求后关闭连接。</summary><param name="client">已接受客户端。</param><returns>响应任务。</returns>
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024]; var length = 0; var end = -1;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                    if (read == 0) return;
                    length += read; end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                    if (end >= 0) break;
                }
                if (end < 0) { await ReplyAsync(stream, 431, new(false, "Request headers too large"), timeout.Token).ConfigureAwait(false); return; }
                var firstLine = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n", 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (firstLine.Length != 3 || !firstLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal) ||
                    !Uri.TryCreate(new Uri(RedirectUrl), firstLine[1], out var url))
                { await ReplyAsync(stream, 400, new(false, "Invalid request"), timeout.Token).ConfigureAwait(false); return; }
                var (status, page, complete) = Receive(url);
                try { await ReplyAsync(stream, status, page, timeout.Token).ConfigureAwait(false); }
                finally { complete?.Invoke(); }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    /// <summary>【CodingAgent】【OAuth 回调校验】先匹配允许路径和 state，再消费该次等待并验证绑定路径、错误和授权码。</summary><param name="url">回调 URL。</param><returns>HTTP 状态和结果页。</returns>
    private (int Status, CodingAgentMcpOAuthCallbackPage Page, Action? Complete) Receive(Uri url)
    {
        if (!_paths.Contains(url.AbsolutePath)) return (404, new(false, "Not found"), null);
        var query = Query(url); query.TryGetValue("state", out var state);
        Pending? pending;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(state) || !_pending.Remove(state, out pending)) return (400, new(false, "Invalid or expired OAuth state"), null);
        }
        if (pending.Path is { } expected && url.AbsolutePath != expected)
        {
            return (400, new(false, "Unexpected redirect URI"), () => pending.Completion.TrySetException(new InvalidOperationException("The authorization response arrived on another redirect URI")));
        }
        if (query.GetValueOrDefault("error") is { Length: > 0 } error)
        {
            var description = query.GetValueOrDefault("error_description") ?? error;
            return (200, new(false, "Authorization failed. You may close this window.", description), () => pending.Completion.TrySetException(new InvalidOperationException(description)));
        }
        if (query.GetValueOrDefault("code") is not { Length: > 0 } code)
        {
            return (400, new(false, "Missing authorization code"), () => pending.Completion.TrySetException(new InvalidOperationException("OAuth callback did not include an authorization code")));
        }
        var issuer = query.GetValueOrDefault("iss");
        return (200, new(true, "Authorization complete. You may close this window."), () => pending.Completion.TrySetResult(new(code, state!, string.IsNullOrEmpty(issuer) ? null : issuer)));
    }

    /// <summary>【CodingAgent】【OAuth 回环响应】返回 UTF-8 页面并禁止缓存，固定关闭连接。</summary>
    /// <param name="stream">网络流。</param><param name="status">状态。</param><param name="page">页面数据。</param><param name="token">取消。</param><returns>写入任务。</returns>
    private async Task ReplyAsync(NetworkStream stream, int status, CodingAgentMcpOAuthCallbackPage page, CancellationToken token)
    {
        var text = _options.RenderPage?.Invoke(page) ?? page.Message + (page.Details is null ? "" : "\n\n" + page.Details);
        var body = Encoding.UTF8.GetBytes(text);
        var type = _options.RenderPage is null ? "text/plain" : "text/html";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(body, token).ConfigureAwait(false);
    }
    /// <summary>【CodingAgent】【OAuth 查询读取】解码表单字符，重复字段只取第一项，与 URLSearchParams.get 对齐。</summary><param name="url">URL。</param><returns>查询参数。</returns>
    internal static Dictionary<string, string> Query(Uri url)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            result.TryAdd(Uri.UnescapeDataString(pair[0].Replace('+', ' ')), Uri.UnescapeDataString((pair.Length > 1 ? pair[1] : "").Replace('+', ' ')));
        }
        return result;
    }
    /// <summary>【CodingAgent】【OAuth 回环关闭】终止所有等待和网络连接，重复关闭等待同一完成任务。</summary><returns>关闭任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _closed = true;
            foreach (var pending in _pending.Values) pending.Completion.TrySetException(new InvalidOperationException("OAuth callback server closed"));
            _pending.Clear();
            return new(_dispose = CloseAsync());
        }
    }
    /// <summary>【CodingAgent】【OAuth 回环释放】取消接收和慢连接后释放套接字与取消源。</summary><returns>关闭任务。</returns>
    private async Task CloseAsync()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false); _listener.Stop();
        await _accept.ConfigureAwait(false);
        Task[] clients;
        lock (_gate) clients = _clients.ToArray();
        await Task.WhenAll(clients).ConfigureAwait(false);
        _lifetime.Dispose();
    }
    /// <summary>【CodingAgent】【OAuth 回调等待项】保存单次 state 的路径和异步完成通知。</summary><param name="path">可选绑定路径。</param>
    private sealed class Pending(string? path)
    {
        internal string? Path { get; } = path;
        internal TaskCompletionSource<CodingAgentMcpOAuthCallback> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
