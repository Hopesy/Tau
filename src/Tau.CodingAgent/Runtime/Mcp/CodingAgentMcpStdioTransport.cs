// 作者：xxx
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 标准管道】通过独立进程收发逐行 JSON-RPC，保留有界 stderr 并关闭整个子进程树。</summary>
public sealed class CodingAgentMcpStdioTransport : ICodingAgentMcpTransport
{
    private readonly ProcessStartInfo _start;
    private readonly int _maxMessageBytes;
    private readonly int _maxStderrBytes;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _stderrGate = new();
    private readonly object _lifecycleGate = new();
    private Task? _disposeTask;
    private byte[] _stderr = [];
    private Process? _process;
    private CodingAgentWindowsJob? _job;
    private Task _stdoutTask = Task.CompletedTask, _stderrTask = Task.CompletedTask;
    private int _started, _closed, _disposed;
    public event Action<JsonElement>? Message;
    public event Action<Exception>? Error;
    public event Action? Closed;
    public string StandardError { get { lock (_stderrGate) return Encoding.UTF8.GetString(_stderr); } }

    /// <summary>【CodingAgent】【MCP 启动参数】建立无需 shell 拼接的进程参数，环境可以继承或完全隔离。</summary>
    /// <param name="command">可执行文件。</param><param name="args">独立参数。</param><param name="cwd">工作目录。</param>
    /// <param name="environment">环境覆盖。</param><param name="inheritEnvironment">是否继承宿主环境。</param>
    /// <param name="maxMessageBytes">单条报文上限。</param><param name="maxStderrBytes">stderr 尾部上限。</param>
    public CodingAgentMcpStdioTransport(string command, IReadOnlyList<string>? args = null, string? cwd = null,
        IReadOnlyDictionary<string, string>? environment = null, bool inheritEnvironment = true,
        int maxMessageBytes = 16 * 1024 * 1024, int maxStderrBytes = 64 * 1024)
    {
        if (maxMessageBytes <= 0 || maxStderrBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _maxMessageBytes = maxMessageBytes; _maxStderrBytes = maxStderrBytes;
        _start = new(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? Environment.CurrentDirectory };
        foreach (var argument in args ?? []) _start.ArgumentList.Add(argument);
        if (!inheritEnvironment) _start.Environment.Clear();
        foreach (var pair in environment ?? new Dictionary<string, string>()) _start.Environment[pair.Key] = pair.Value;
        CodingAgentMcpWindowsCommand.Prepare(_start);
    }

    /// <summary>【CodingAgent】【MCP 启动】创建隐藏进程并立即接收标准输出和错误输出。</summary>
    /// <param name="token">启动取消信号。</param><returns>启动任务。</returns>
    public Task StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_lifecycleGate)
        {
            if (_disposed != 0 || Volatile.Read(ref _closed) != 0 || _started != 0) throw new InvalidOperationException("MCP stdio transport already started or closed.");
            _started = 1;
            var process = new Process { StartInfo = _start };
            _process = process;
            try
            {
                if (!process.Start()) throw new IOException("Could not start MCP server.");
                _job = CodingAgentWindowsJob.Attach(process);
                _stderrTask = ReadErrorAsync(process.StandardError.BaseStream, _lifetime.Token);
                _stdoutTask = ReadOutputAsync(process.StandardOutput.BaseStream, _lifetime.Token);
            }
            catch { process.Dispose(); _process = null; CloseOnce(); throw; }
        }
        return Task.CompletedTask;
    }

    /// <summary>【CodingAgent】【MCP 发送】串行写入一行 UTF-8 JSON，防止并发请求交错。</summary>
    /// <param name="message">完整报文。</param><param name="token">取消信号。</param><returns>写入任务。</returns>
    public async Task SendAsync(JsonElement message, CancellationToken token = default)
    {
        CancellationTokenSource linked;
        lock (_lifecycleGate)
        {
            if (_started == 0 || _disposed != 0 || Volatile.Read(ref _closed) != 0) throw new IOException("MCP stdio connection closed.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        }
        using (linked)
        {
            await _writes.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (_process is not { } process || Volatile.Read(ref _closed) != 0) throw new IOException("MCP stdio connection closed.");
                var bytes = Encoding.UTF8.GetBytes(message.GetRawText() + "\n");
                await process.StandardInput.BaseStream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            finally { _writes.Release(); }
        }
    }

    /// <summary>【CodingAgent】【MCP 接收】按换行拆包，坏报文仅报告错误，超大报文丢弃到下一个边界。</summary>
    /// <param name="stream">服务器 stdout。</param><param name="token">生命周期取消信号。</param><returns>接收任务。</returns>
    private async Task ReadOutputAsync(Stream stream, CancellationToken token)
    {
        using var pending = new MemoryStream();
        var buffer = new byte[8192]; var dropping = false;
        try
        {
            int length;
            while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                for (var index = 0; index < length; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        if (!dropping) DispatchLine(pending.ToArray());
                        pending.SetLength(0); dropping = false;
                    }
                    else if (!dropping)
                    {
                        if (pending.Length >= _maxMessageBytes)
                        {
                            ReportError(new IOException($"MCP stdio message exceeds {_maxMessageBytes} bytes"));
                            pending.SetLength(0); dropping = true;
                        }
                        else pending.WriteByte(buffer[index]);
                    }
                }
            }
            if (pending.Length > 0 && !string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(pending.ToArray())))
                ReportError(new IOException("MCP stdio server closed with an incomplete JSON-RPC message"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ReportError(error); }
        finally
        {
            // 1. 【MCP】【退出诊断】stdout 先结束时短暂排空 stderr，避免关闭事件先于进程最后的错误输出
            try { await _stderrTask.WaitAsync(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            CloseOnce();
        }
    }

    /// <summary>【CodingAgent】【MCP 报文】解析独立 JSON 并隔离监听器异常。</summary>
    /// <param name="line">单行 UTF-8 数据。</param>
    private void DispatchLine(byte[] line)
    {
        if (line.All(value => value is (byte)' ' or (byte)'\t' or (byte)'\r')) return;
        try { using var document = JsonDocument.Parse(line); Message?.Invoke(CodingAgentMcpJsonRpc.Validate(document.RootElement).Clone()); }
        catch (Exception error) { ReportError(error); }
    }

    /// <summary>【CodingAgent】【MCP 错误尾部】持续读取 stderr，避免服务端阻塞并限制保留字节。</summary>
    /// <param name="stream">服务器 stderr。</param><param name="token">生命周期信号。</param><returns>读取任务。</returns>
    private async Task ReadErrorAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            int length;
            while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                lock (_stderrGate) _stderr = _stderr.Concat(buffer.Take(length)).TakeLast(_maxStderrBytes).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ReportError(error); }
    }

    /// <summary>【CodingAgent】【MCP 关闭】先关闭 stdin 等待退出，再终止子进程树并排空读取任务。</summary><returns>关闭任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = 1;
            return new(_disposeTask = DisposeCoreAsync());
        }
    }

    /// <summary>【MCP】【管道关闭屏障】启动完成后再关闭进程，重复关闭共同等待读取和正在写入的操作退出。</summary><returns>实际清理任务。</returns>
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        CloseOnce();
        var process = _process;
        try
        {
            if (process is not null)
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
                if (!process.HasExited)
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
                    catch (TimeoutException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                }
                _job?.Terminate();
            }
        }
        finally
        {
            // 1. 【MCP】【管道排空】进程退出后先读取剩余字节，继承了管道的子进程无法无限延迟清理
            var readers = Task.WhenAll(_stdoutTask, _stderrTask);
            try { await readers.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _writes.WaitAsync().ConfigureAwait(false);
            _writes.Release();
            try { await readers.ConfigureAwait(false); }
            finally { _job?.Dispose(); process?.Dispose(); _process = null; _lifetime.Dispose(); }
        }
    }

    /// <summary>【CodingAgent】【MCP 关闭通知】仅发布一次关闭事件。</summary>
    private void CloseOnce() { if (Interlocked.Exchange(ref _closed, 1) == 0) try { Closed?.Invoke(); } catch (Exception error) { ReportError(error); } }
    /// <summary>【CodingAgent】【MCP 诊断】隔离观察者异常。</summary><param name="error">传输错误。</param>
    private void ReportError(Exception error) { try { Error?.Invoke(error); } catch { } }
}
