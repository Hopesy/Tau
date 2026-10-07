// 作者：xxx
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tau.Ai.Auth.OAuth;

/// <summary>【AI】【OAuth 回调】在实际授权兑换完成后响应浏览器的一次性本地回调服务器。</summary>
/// <typeparam name="T">授权兑换结果。</typeparam>
internal sealed class OAuthLoopbackServer<T> : IAsyncDisposable where T : class
{
    private readonly TcpListener _listener;
    private readonly string _providerName;
    private readonly string _path;
    private readonly string? _state;
    private readonly Func<Uri, CancellationToken, Task<T>> _complete;
    private readonly Func<Uri, string?>? _validate;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly Timer _timer;
    private readonly TaskCompletionSource<T?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<Task> _clients = [];
    private readonly Task _accept;
    private bool _claimed;
    private Task? _dispose;

    /// <summary>【AI】【OAuth 回调】保存已绑定的监听器并启动接收、取消和超时监视。</summary>
    /// <param name="listener">已启动监听器。</param><param name="providerName">提供方名称。</param>
    /// <param name="path">精确回调路径。</param><param name="state">可选随机状态。</param>
    /// <param name="redirectHost">对浏览器公布的主机。</param><param name="complete">授权兑换。</param>
    /// <param name="token">登录取消信号。</param><param name="timeout">登录期限。</param><param name="validate">可选完整回调校验。</param>
    private OAuthLoopbackServer(TcpListener listener, string providerName, string path, string? state,
        string redirectHost, Func<Uri, CancellationToken, Task<T>> complete, CancellationToken token, TimeSpan timeout, Func<Uri, string?>? validate)
    {
        _listener = listener; _providerName = providerName; _path = path; _state = state; _complete = complete;
        _validate = validate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        RedirectUri = new UriBuilder("http", redirectHost, ((IPEndPoint)listener.LocalEndpoint).Port, path).Uri.AbsoluteUri;
        _cancellation = token.Register(() => Finish(null, new OperationCanceledException("Login cancelled", token)));
        _timer = new Timer(_ => Finish(null, new TimeoutException($"{_providerName} sign-in timed out")), null, timeout, Timeout.InfiniteTimeSpan);
        // 1. 【AI】【OAuth 回调】即使调用方尚未等待，也观察回调失败，防止悬空异常
        _ = _result.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _accept = AcceptAsync();
    }

    /// <summary>浏览器应访问的真实端口与随机路径。</summary>
    public string RedirectUri { get; }

    /// <summary>【AI】【OAuth 回调】直接绑定指定端口，零端口由操作系统分配，不预先探测空闲端口。</summary>
    /// <param name="providerName">提供方名称。</param><param name="host">监听地址或主机名。</param>
    /// <param name="port">端口，零表示自动分配。</param><param name="path">精确路径。</param>
    /// <param name="complete">授权码兑换函数。</param><param name="token">登录取消信号。</param>
    /// <param name="timeout">最长等待时间。</param><param name="state">可选状态校验。</param>
    /// <param name="redirectHost">可选外部重定向主机。</param><param name="validate">可选附加参数校验，返回错误文本。</param>
    /// <param name="completeRequest">需要完整回调参数时的兑换函数。</param><returns>已经开始监听的服务器。</returns>
    internal static OAuthLoopbackServer<T> Listen(string providerName, string host, int port, string path,
        Func<string, CancellationToken, Task<T>> complete, CancellationToken token, TimeSpan timeout,
        string? state = null, string? redirectHost = null, Func<Uri, string?>? validate = null,
        Func<Uri, CancellationToken, Task<T>>? completeRequest = null)
    {
        token.ThrowIfCancellationRequested();
        if (!path.StartsWith('/') || path.Contains('?') || path.Contains('#')) throw new ArgumentException("Invalid OAuth callback path", nameof(path));
        if (timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var address = IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed : Dns.GetHostAddresses(host)[0];
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
            return new(listener, providerName, path, state, redirectHost ?? host,
                completeRequest ?? ((uri, signal) => complete(Query(uri.Query)["code"], signal)), token, timeout, validate);
        }
        catch { listener.Stop(); throw; }
    }

    /// <summary>【AI】【OAuth 回调】等待授权结果，手动输入提前接管时返回空。</summary><returns>授权结果。</returns>
    internal Task<T?> WaitAsync() => _result.Task;

    /// <summary>【AI】【OAuth 回调】仅允许手动输入接管尚未被浏览器认领的登录。</summary>
    internal void Cancel()
    {
        lock (_gate) if (!_claimed) _result.TrySetResult(null);
    }

    /// <summary>【AI】【OAuth 回调】并发接收请求，让无效或重复回调可以及时得到响应。</summary><returns>监听任务。</returns>
    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _clients.RemoveAll(task => task.IsCompleted);
                    _clients.Add(HandleAsync(client));
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (!_lifetime.IsCancellationRequested) Finish(null, error);
        }
    }

    /// <summary>【AI】【OAuth 回调】限制请求头读取时间和大小，精确校验方法、路径与状态。</summary>
    /// <param name="client">已接收连接。</param><returns>连接处理任务。</returns>
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var reading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            reading.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024]; var length = 0; var end = -1;
                while (length < buffer.Length)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(length), reading.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    length += count; end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                    if (end >= 0) break;
                }
                if (end < 0) { await ReplyAsync(stream, 431, "Request headers too large.", reading.Token).ConfigureAwait(false); return; }
                var line = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n", 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (line.Length != 3 || !line[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !Uri.TryCreate(new Uri(RedirectUri), line[1], out var url))
                { await ReplyAsync(stream, 400, "Invalid request.", reading.Token).ConfigureAwait(false); return; }
                if (line[0] != "GET" || url.AbsolutePath != _path)
                { await ReplyAsync(stream, 404, "Callback route not found.", reading.Token).ConfigureAwait(false); return; }
                var query = Query(url.Query);
                if (_state is not null && query.GetValueOrDefault("state") != _state)
                { await ReplyAsync(stream, 400, "State mismatch.", reading.Token).ConfigureAwait(false); return; }
                // 2. 【AI】【OAuth 回调】授权错误与有效代码都只能被认领一次，无效请求不消费登录
                var error = query.GetValueOrDefault("error");
                if (string.IsNullOrEmpty(error) && _validate?.Invoke(url) is { } validationError)
                { await ReplyAsync(stream, 400, Page(validationError, ""), reading.Token).ConfigureAwait(false); return; }
                var code = query.GetValueOrDefault("code");
                var status = 200;
                lock (_gate)
                {
                    if (_claimed || _result.Task.IsCompleted) status = 409;
                    else if (string.IsNullOrEmpty(error) && string.IsNullOrEmpty(code)) status = 400;
                    else _claimed = true;
                }
                if (status != 200)
                { await ReplyAsync(stream, status, status == 409 ? "This sign-in has already been handled." : "Missing authorization code.", reading.Token).ConfigureAwait(false); return; }
                reading.CancelAfter(Timeout.InfiniteTimeSpan);
                T? value = null; Exception? failure = null;
                string page;
                if (!string.IsNullOrEmpty(error))
                {
                    var detail = query.GetValueOrDefault("error_description") ?? error;
                    failure = new InvalidOperationException($"{_providerName} authorization failed: {detail}");
                    status = 400; page = Page($"{_providerName} authorization failed.", detail);
                }
                else
                {
                    try
                    {
                        // 3. 【AI】【OAuth 回调】兑换完成后才向浏览器展示成功，避免登录失败却显示成功页面
                        value = await _complete(url, _lifetime.Token).WaitAsync(_lifetime.Token).ConfigureAwait(false);
                        page = Page($"Signed in to {_providerName}.", "You may now close this page.");
                    }
                    catch (Exception exception)
                    { failure = exception; status = 502; page = Page($"{_providerName} sign-in failed.", exception.Message); }
                }
                try { await ReplyAsync(stream, status, page, reading.Token).ConfigureAwait(false); }
                finally { Finish(value, failure); }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    /// <summary>【AI】【OAuth 回调】输出转义过的标题和说明，避免把服务端错误解释成网页脚本。</summary>
    /// <param name="title">标题。</param><param name="detail">说明。</param><returns>HTML 页面。</returns>
    private static string Page(string title, string detail) => $"<!doctype html><html><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title><body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(detail)}</p></body></html>";

    /// <summary>【AI】【OAuth 回调】写入禁止缓存的 UTF-8 响应并关闭连接。</summary>
    /// <param name="stream">网络流。</param><param name="status">HTTP 状态。</param><param name="body">页面。</param>
    /// <param name="token">取消信号。</param><returns>响应任务。</returns>
    private static async Task ReplyAsync(NetworkStream stream, int status, string body, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    /// <summary>【AI】【OAuth 回调】按表单规则解码查询参数，重复字段使用第一项。</summary>
    /// <param name="query">查询串。</param><returns>区分大小写的参数。</returns>
    internal static Dictionary<string, string> Query(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            result.TryAdd(Uri.UnescapeDataString(pair[0].Replace('+', ' ')), Uri.UnescapeDataString((pair.Length > 1 ? pair[1] : "").Replace('+', ' ')));
        }
        return result;
    }

    /// <summary>【AI】【OAuth 回调】仅发布第一次结果或异常。</summary>
    /// <param name="value">授权结果。</param><param name="error">失败原因。</param>
    private void Finish(T? value, Exception? error)
    {
        lock (_gate)
        {
            if (error is null) _result.TrySetResult(value);
            else _result.TrySetException(error);
        }
    }

    /// <summary>【AI】【OAuth 回调】幂等关闭监听器和所有未完成连接。</summary><returns>关闭任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= CloseAsync());
    }

    /// <summary>【AI】【OAuth 回调】释放超时、取消注册及网络资源，关闭前终止未完成等待。</summary><returns>释放任务。</returns>
    private async Task CloseAsync()
    {
        await Task.Yield();
        Finish(null, new InvalidOperationException("OAuth callback server closed"));
        await _timer.DisposeAsync().ConfigureAwait(false);
        await _cancellation.DisposeAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _accept.ConfigureAwait(false);
        Task[] clients;
        lock (_gate) clients = _clients.ToArray();
        await Task.WhenAll(clients).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
