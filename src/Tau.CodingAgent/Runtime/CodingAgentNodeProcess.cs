// 作者：xxx
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【扩展进程】管理持久 Node 进程及可重入的请求通信</summary>
internal sealed class CodingAgentNodeProcess : IDisposable
{
    private const string ResultPrefix = "__TAU_EXTENSION_RESULT__";
    private const string UiRequestPrefix = "__TAU_EXTENSION_UI_REQUEST__";
    private const string UiResponsePrefix = "__TAU_EXTENSION_UI_RESPONSE__";
    private const string UiCancelPrefix = "__TAU_EXTENSION_UI_CANCEL__";
    private const string SessionActionPrefix = "__TAU_EXTENSION_SESSION_ACTION__";
    private const string HostRequestPrefix = "__TAU_EXTENSION_HOST_REQUEST__";
    private const string HostResponsePrefix = "__TAU_EXTENSION_HOST_RESPONSE__";
    private const string HostCancelPrefix = "__TAU_EXTENSION_HOST_CANCEL__";
    private const string WaitPrefix = "__TAU_EXTENSION_WAIT__";
    private const string CancelPrefix = "__TAU_EXTENSION_CANCEL__";
    private const string ToolUpdatePrefix = "__TAU_EXTENSION_TOOL_UPDATE__";
    private const string BackgroundPrefix = "__TAU_EXTENSION_BACKGROUND__";
    private readonly Process _process = new();
    private readonly string _scriptDirectory = Path.Combine(Path.GetTempPath(), "tau-node-extension-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uiCancellation = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _hostCancellation = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _hostTasks = new();
    private readonly AsyncLocal<string?> _currentHostRequest = new();
    private Task _responsesTask = Task.CompletedTask;
    private readonly object _writeGate = new();
    private readonly object _stopGate = new();
    private readonly object _diagnosticGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<JsonElement, CancellationToken, Task<string>> _handleUi;
    private readonly Action<JsonElement> _handleSession;
    private readonly Func<JsonElement, CancellationToken, Action<JsonElement>, Task<string>> _handleHost;
    private readonly Action<JsonElement> _handleBackground;
    private string _diagnostic = string.Empty;
    private int _stopped;

    /// <summary>【CodingAgent】【扩展进程】启动隐藏的 Node 工作进程并持续读取输出</summary>
    /// <param name="executable">Node 可执行文件路径</param>
    /// <param name="cwd">扩展工作目录</param>
    /// <param name="script">嵌入的运行时脚本</param>
    /// <param name="handleUi">编辑器交互回调</param>
    /// <param name="handleSession">会话元数据同步回调，不得回调 Node</param>
    /// <param name="handleHost">可重入的异步宿主操作回调</param>
    /// <param name="handleBackground">后台消息接收器，只入队，不得同步重入 Node</param>
    public CodingAgentNodeProcess(string executable, string cwd, string script, Func<JsonElement, CancellationToken, Task<string>> handleUi, Action<JsonElement> handleSession,
        Func<JsonElement, CancellationToken, Action<JsonElement>, Task<string>> handleHost, Action<JsonElement> handleBackground)
    {
        _handleUi = handleUi;
        _handleSession = handleSession;
        _handleHost = handleHost;
        _handleBackground = handleBackground;
        try
        {
            // 1. 【CodingAgent】【扩展进程】将程序集内的脚本写入专用临时目录
            Directory.CreateDirectory(_scriptDirectory);
            var scriptPath = Path.Combine(_scriptDirectory, "runtime.cjs");
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
            _process.StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory
            };
            _process.StartInfo.ArgumentList.Add(scriptPath);
            _process.Start();

            // 2. 【CodingAgent】【扩展协议】独立排空两个输出管道，避免扩展日志阻塞请求
            _ = ReadDiagnosticsAsync();
            _responsesTask = ReadResponsesAsync();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <summary>【CodingAgent】【扩展调用】发送请求并等待对应响应，交互等待不消耗执行时限</summary>
    /// <param name="payloadJson">原始调用参数 JSON</param>
    /// <param name="timeout">单次请求可使用的执行时间</param>
    /// <param name="cancellationToken">调用方取消信号，先协作取消，处理器不退出时终止进程</param>
    /// <param name="onUpdate">按到达顺序分发的异步工具进度回调</param>
    /// <returns>对应请求的结果 JSON</returns>
    public async Task<string> ExecuteAsync(string payloadJson, TimeSpan timeout, CancellationToken cancellationToken = default,
        Func<JsonElement, Task>? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var request = new PendingRequest(timeout, () => Stop("node extension runtime timed out"), linked.Token, onUpdate);
        _pending[id] = request;
        try
        {
            request.Start();
            using var payload = JsonDocument.Parse(payloadJson);
            WriteLine(JsonSerializer.Serialize(new { id, payload = payload.RootElement }));
            using var cancellation = cancellationToken.Register(() => CancelRequest(id));
            try
            {
                var result = await request.Completion.Task.ConfigureAwait(false);
                await request.DrainUpdatesAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>【CodingAgent】【协作取消】把取消送到指定 AbortSignal，允许处理器清理后复用进程。</summary>
    /// <param name="id">需要取消的请求标识。</param>
    private void CancelRequest(string id)
    {
        if (!_pending.TryGetValue(id, out var request)) return;
        try { WriteLine(CancelPrefix + JsonSerializer.Serialize(new { id })); }
        catch (IOException) { return; }
        // 1. 【CodingAgent】【取消兜底】独立等待结束通知，忽略取消或永久等待的处理器不能卡住宿主
        _ = Task.Run(async () =>
        {
            var completed = await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            if (completed != request.Completion.Task && _pending.ContainsKey(id)) Stop("node extension request ignored cancellation");
        });
    }

    /// <summary>【CodingAgent】【扩展协议】串行写入完整行以避免多个请求相互穿插</summary>
    /// <param name="line">完整协议行</param>
    private void WriteLine(string line)
    {
        lock (_writeGate)
        {
            if (IsStopped) throw new IOException("node extension runtime has stopped");
            try
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                throw new IOException("node extension runtime input unavailable", ex);
            }
        }
    }

    /// <summary>【CodingAgent】【扩展协议】分发结果与交互请求，允许交互期间回调扩展</summary>
    /// <returns>输出管道关闭后完成的任务</returns>
    private async Task ReadResponsesAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith(ResultPrefix, StringComparison.Ordinal))
                {
                    using var response = JsonDocument.Parse(line[ResultPrefix.Length..]);
                    var root = response.RootElement;
                    var result = root.GetProperty("result").GetRawText();
                    if (_pending.TryRemove(root.GetProperty("id").GetString()!, out var request))
                    {
                        if (request.SessionError is not null)
                            result = JsonSerializer.Serialize(new { ok = false, error = request.SessionError });
                        request.Complete(result);
                    }
                }
                else if (line.StartsWith(BackgroundPrefix, StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(line[BackgroundPrefix.Length..]);
                    try { _handleBackground(document.RootElement); }
                    catch (Exception ex) { AppendDiagnostic("Background extension action failed: " + ex.Message); }
                }
                else if (line.StartsWith(ToolUpdatePrefix, StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(line[ToolUpdatePrefix.Length..]);
                    var root = document.RootElement;
                    if (_pending.TryGetValue(root.GetProperty("requestId").GetString()!, out var request))
                        request.EnqueueUpdate(root.GetProperty("update").Clone());
                }
                else if (line.StartsWith(SessionActionPrefix, StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(line[SessionActionPrefix.Length..]);
                    var action = document.RootElement;
                    if (_pending.TryGetValue(action.GetProperty("requestId").GetString()!, out var request) && request.SessionError is null)
                    {
                        // 1. 【CodingAgent】【扩展会话】按管道顺序提交元数据，再处理随后的 UI 请求和调用终值
                        try { _handleSession(action); }
                        catch (Exception ex) { request.SessionError = "Extension session operation failed: " + ex.Message; }
                    }
                    else if (request is null && action.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True)
                    {
                        try { _handleSession(action); }
                        catch (Exception ex) { AppendDiagnostic("Background extension session operation failed: " + ex.Message); }
                    }
                }
                else if (line.StartsWith(WaitPrefix, StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(line[WaitPrefix.Length..]);
                    var root = document.RootElement;
                    if (_pending.TryGetValue(root.GetProperty("requestId").GetString()!, out var request))
                    {
                        if (root.GetProperty("paused").GetBoolean()) request.Pause();
                        else request.Resume();
                    }
                }
                else if (line.StartsWith(HostRequestPrefix, StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(line[HostRequestPrefix.Length..]);
                    var root = document.RootElement.Clone();
                    _pending.TryGetValue(root.GetProperty("requestId").GetString()!, out var request);
                    if (request is null && (!root.TryGetProperty("background", out var background) || background.ValueKind != JsonValueKind.True)) continue;
                    request?.Pause();
                    // 2. 【CodingAgent】【宿主调用】后台回调和活动请求共用可重入通道
                    var source = CancellationTokenSource.CreateLinkedTokenSource(
                        root.TryGetProperty("background", out var detached) && detached.ValueKind == JsonValueKind.True
                            ? _lifetime.Token : request?.Token ?? _lifetime.Token);
                    if (root.TryGetProperty("aborted", out var aborted) && aborted.ValueKind == JsonValueKind.True) source.Cancel();
                    _hostCancellation[root.GetProperty("id").GetString()!] = source;
                    var hostId = root.GetProperty("id").GetString()!;
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _hostTasks[hostId] = completion;
                    _ = Task.Run(async () =>
                    {
                        _currentHostRequest.Value = hostId;
                        try { await HandleHostAsync(root, request, source).ConfigureAwait(false); }
                        finally { _hostTasks.TryRemove(hostId, out _); completion.TrySetResult(); }
                    });
                }
                else if (line.StartsWith(HostCancelPrefix, StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(line[HostCancelPrefix.Length..]);
                    if (_hostCancellation.TryGetValue(document.RootElement.GetProperty("id").GetString()!, out var source))
                    {
                        try { await source.CancelAsync().ConfigureAwait(false); }
                        catch (ObjectDisposedException) { }
                    }
                }
                else if (line.StartsWith(UiCancelPrefix, StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(line[UiCancelPrefix.Length..]);
                    if (_uiCancellation.TryGetValue(document.RootElement.GetProperty("id").GetString()!, out var source))
                    {
                        try { await source.CancelAsync().ConfigureAwait(false); }
                        catch (ObjectDisposedException) { }
                    }
                }
                else if (line.StartsWith(UiRequestPrefix, StringComparison.Ordinal))
                {
                    using var response = JsonDocument.Parse(line[UiRequestPrefix.Length..]);
                    var root = response.RootElement.Clone();
                    _pending.TryGetValue(root.GetProperty("requestId").GetString()!, out var request);
                    if (request is null && (!root.TryGetProperty("background", out var background) || background.ValueKind != JsonValueKind.True)) continue;
                    request?.Pause();
                    var source = CancellationTokenSource.CreateLinkedTokenSource(request?.Token ?? _lifetime.Token);
                    _uiCancellation[root.GetProperty("id").GetString()!] = source;
                    // 1. 【CodingAgent】【扩展交互】回调可能同步发起新请求，不能占用输出读取任务
                    _ = Task.Run(() => HandleUiAsync(root, request, source));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or KeyNotFoundException or ArgumentException)
        {
            AppendDiagnostic(ex.Message);
        }
        finally
        {
            string diagnostic;
            lock (_diagnosticGate) diagnostic = _diagnostic;
            Stop("node extension runtime exited without a result" + (diagnostic.Length == 0 ? "" : ": " + diagnostic));
        }
    }

    /// <summary>【CodingAgent】【宿主调用】返回异步宿主结果，恢复所属扩展的执行计时。</summary>
    /// <param name="root">请求参数。</param>
    /// <param name="request">所属扩展调用。</param>
    /// <param name="source">支持扩展独立取消的宿主请求取消源。</param>
    /// <returns>响应发送完成的任务。</returns>
    private async Task HandleHostAsync(JsonElement root, PendingRequest? request, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            var response = request?.SessionError is not null
                ? JsonSerializer.Serialize(new { id = root.GetProperty("id").GetString(), ok = false, error = request.SessionError })
                : await _handleHost(root, token, update => WriteLine(HostResponsePrefix + JsonSerializer.Serialize(
                    new { id = root.GetProperty("id").GetString(), progress = true, value = update }))).ConfigureAwait(false);
            WriteLine(HostResponsePrefix + response);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !IsStopped)
        {
            WriteLine(HostResponsePrefix + JsonSerializer.Serialize(new { id = root.GetProperty("id").GetString(), ok = false, error = "Extension request was cancelled." }));
        }
        catch (Exception ex)
        {
            if (!IsStopped) Stop("node extension host request failed: " + ex.Message);
        }
        finally
        {
            _hostCancellation.TryRemove(root.GetProperty("id").GetString()!, out _);
            source.Dispose();
            request?.Resume();
        }
    }

    /// <summary>【CodingAgent】【扩展交互】回传编辑器结果并恢复请求的执行计时</summary>
    /// <param name="root">带关联标识的交互请求</param>
    /// <param name="request">等待交互的执行请求</param>
    /// <param name="source">本次对话独立的取消源</param>
    /// <returns>响应发送完成的任务</returns>
    private async Task HandleUiAsync(JsonElement root, PendingRequest? request, CancellationTokenSource source)
    {
        try
        {
            var response = await _handleUi(root, source.Token).ConfigureAwait(false);
            WriteLine(UiResponsePrefix + response);
        }
        catch (Exception ex)
        {
            if (!IsStopped) Stop("node extension UI failed: " + ex.Message);
        }
        finally
        {
            _uiCancellation.TryRemove(root.GetProperty("id").GetString()!, out _);
            source.Dispose();
            request?.Resume();
        }
    }

    /// <summary>【CodingAgent】【扩展诊断】持续读取标准错误，只保留有界的诊断尾部</summary>
    /// <returns>诊断管道关闭后完成的任务</returns>
    private async Task ReadDiagnosticsAsync()
    {
        try
        {
            var buffer = new char[1024];
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                AppendDiagnostic(new string(buffer, 0, count));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // 1. 【CodingAgent】【扩展诊断】进程释放时管道关闭属于正常结束
        }
    }

    /// <summary>记录最多 2048 个字符的诊断信息</summary>
    /// <param name="text">新增诊断文本</param>
    private void AppendDiagnostic(string text)
    {
        lock (_diagnosticGate)
        {
            _diagnostic += text;
            if (_diagnostic.Length > 2048) _diagnostic = _diagnostic[^2048..];
        }
    }

    /// <summary>【CodingAgent】【扩展生命周期】终止进程树、取消交互并使所有等待请求结束</summary>
    /// <param name="error">返回给未完成请求的诊断信息</param>
    private void Stop(string error)
    {
        lock (_stopGate)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            // 1. 【CodingAgent】【扩展生命周期】异步取消宿主交互，避免取消回调重入释放锁
            _ = CancelInteractionsAsync();
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                // 2. 【CodingAgent】【进程清理】确认已终止的进程释放工作目录句柄后，才允许调用方清理会话目录
                _process.WaitForExit();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 3. 【CodingAgent】【扩展生命周期】启动失败或已退出时仍需释放等待请求
            }

            foreach (var (id, _) in _pending)
            {
                if (_pending.TryRemove(id, out var request)) request.Fail(error);
            }
            _process.Dispose();
            try
            {
                if (Directory.Exists(_scriptDirectory)) Directory.Delete(_scriptDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 4. 【CodingAgent】【扩展生命周期】临时目录清理失败不覆盖原始执行结果
            }
        }
    }

    /// <summary>【CodingAgent】【扩展生命周期】释放工作进程及其未完成请求；允许重复调用</summary>
    public void Dispose() => Stop("node extension runtime disposed");

    /// <summary>【CodingAgent】【宿主清理】进程停止后等待已接收宿主请求退出，避免关闭会话后继续读取资源文件。</summary>
    /// <returns>宿主请求全部完成的任务；从宿主内部关闭时排除当前请求。</returns>
    internal async Task DrainHostRequestsAsync()
    {
        await _responsesTask.ConfigureAwait(false);
        var current = _currentHostRequest.Value;
        await Task.WhenAll(_hostTasks.Where(pair => pair.Key != current).Select(pair => pair.Value.Task)).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【扩展生命周期】取消交互回调后释放取消源</summary>
    /// <returns>取消源释放完成的任务</returns>
    private async Task CancelInteractionsAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    /// <summary>【CodingAgent】【扩展超时】跟踪请求剩余执行时间，暂停用户交互期间的计时</summary>
    private sealed class PendingRequest : IDisposable
    {
        private readonly object _gate = new();
        private readonly Timer _timer;
        private readonly Action _onTimeout;
        private TimeSpan _remaining;
        private long _started;
        private int _pauseDepth;
        private bool _done;
        private readonly Func<JsonElement, Task>? _onUpdate;
        private Task _updates = Task.CompletedTask;

        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? SessionError { get; set; }
        public CancellationToken Token { get; }

        /// <summary>创建尚未启动计时的请求</summary>
        /// <param name="timeout">可消耗的执行时长</param>
        /// <param name="onTimeout">时间耗尽时终止进程的回调</param>
        /// <param name="token">请求或进程生命周期取消信号</param>
        /// <param name="onUpdate">异步工具进度回调</param>
        public PendingRequest(TimeSpan timeout, Action onTimeout, CancellationToken token, Func<JsonElement, Task>? onUpdate)
        {
            Token = token;
            _onUpdate = onUpdate;
            _remaining = timeout;
            _onTimeout = onTimeout;
            _timer = new Timer(CheckTimeout, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>【CodingAgent】【工具进度】按请求顺序排队执行，回调重入 Node 时不占用输出读取管道。</summary>
        /// <param name="update">已脱离 JSON 文档生命周期的中间结果。</param>
        public void EnqueueUpdate(JsonElement update)
        {
            lock (_gate)
            {
                if (_done || _onUpdate is null) return;
                var previous = _updates;
                _updates = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    Token.ThrowIfCancellationRequested();
                    await _onUpdate(update).ConfigureAwait(false);
                });
            }
        }

        /// <summary>【CodingAgent】【工具进度】等待已接收的更新回调，确保最终结果不会越过中间更新。</summary>
        /// <returns>所有回调完成或出现异常的任务。</returns>
        public Task DrainUpdatesAsync()
        {
            lock (_gate) return _updates;
        }

        /// <summary>发送请求前开始计时，管道写入也受执行时限约束</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_done || _pauseDepth > 0 || _remaining == Timeout.InfiniteTimeSpan) return;
                _started = Stopwatch.GetTimestamp();
                _timer.Change(_remaining, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>暂停计时并扣除已经消耗的执行时间</summary>
        public void Pause()
        {
            lock (_gate)
            {
                if (_done || _pauseDepth++ > 0 || _remaining == Timeout.InfiniteTimeSpan) return;
                if (_started != 0) _remaining -= Stopwatch.GetElapsedTime(_started);
                if (_remaining < TimeSpan.Zero) _remaining = TimeSpan.Zero;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>最后一个交互返回后恢复剩余执行时间</summary>
        public void Resume()
        {
            lock (_gate)
            {
                if (_done || --_pauseDepth > 0) return;
                Start();
            }
        }

        /// <summary>忽略过期的计时回调，仅在实际执行时间耗尽后报告超时</summary>
        /// <param name="state">计时器状态，本实现未使用</param>
        private void CheckTimeout(object? state)
        {
            lock (_gate)
            {
                if (_done || _pauseDepth > 0) return;
                var remaining = _remaining - Stopwatch.GetElapsedTime(_started);
                if (remaining > TimeSpan.Zero)
                {
                    _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                    return;
                }
                _done = true;
            }
            _onTimeout();
        }

        /// <summary>停止计时并返回执行结果</summary>
        /// <param name="result">结果 JSON</param>
        public void Complete(string result)
        {
            Dispose();
            Completion.TrySetResult(result);
        }

        /// <summary>停止计时并唤醒发生错误的请求</summary>
        /// <param name="error">错误诊断</param>
        public void Fail(string error)
        {
            Dispose();
            Completion.TrySetException(new IOException(error));
        }

        /// <summary>释放计时器，后续暂停和恢复不再影响请求</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _done = true;
                _timer.Dispose();
            }
        }
    }
}
