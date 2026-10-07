// 作者：xxx
using System.Diagnostics;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【剪贴板命令】运行有超时和输出上限的命令，读写并行且取消可终止所属进程。</summary>
internal sealed class SystemCodingAgentClipboardCommandRunner : ICodingAgentClipboardCommandRunner
{
    /// <summary>【CodingAgent】【命令生命周期】启动隐藏进程，立即关闭无输入的管道；写剪贴板时不等待后台进程保留的输出。</summary>
    /// <param name="fileName">程序。</param><param name="arguments">独立参数。</param><param name="stdin">输入字节，空引用表示读取模式。</param>
    /// <param name="timeoutMs">超时毫秒。</param><param name="maxBufferBytes">读取输出的字节上限。</param>
    /// <param name="cancellationToken">调用者取消信号。</param><returns>成功输出或失败原因，调用者取消抛出异常。</returns>
    public async Task<CodingAgentClipboardCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        byte[]? stdin, int timeoutMs, int maxBufferBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBufferBytes);
        using var process = new Process { StartInfo = new()
        {
            FileName = fileName, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        { return new(false, [], ex.Message); }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeoutMs);
        using var abort = lifetime.Token.Register(() => KillOwnedProcess(process));
        var exceeded = 0;
        // 1. 【CodingAgent】【并行管道】边写边读，读取命令也立即收到 stdin EOF，避免双向管道阻塞
        var write = WriteInputAsync(process.StandardInput.BaseStream, stdin, lifetime.Token);
        var output = stdin is null
            ? ReadBoundedAsync(process.StandardOutput.BaseStream, maxBufferBytes,
                () => { Interlocked.Exchange(ref exceeded, 1); lifetime.Cancel(); }, lifetime.Token)
            : DiscardOutputAsync(process.StandardOutput.BaseStream, lifetime.Token);
        var error = DiscardOutputAsync(process.StandardError.BaseStream, lifetime.Token);
        var exit = process.WaitForExitAsync(lifetime.Token);
        try
        {
            if (stdin is null) await Task.WhenAll(write, output, exit).ConfigureAwait(false);
            else await Task.WhenAll(write, exit).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsCancellationRequested) return new(false, [], exceeded != 0 ? "output exceeded max buffer" : "command timed out");
            return new(process.ExitCode == 0, stdin is null ? await output.ConfigureAwait(false) : [],
                process.ExitCode == 0 ? string.Empty : $"command exited with code {process.ExitCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, [], exceeded != 0 ? "output exceeded max buffer" : "command timed out"); }
        catch (IOException ex) { cancellationToken.ThrowIfCancellationRequested(); return new(false, [], ex.Message); }
        finally
        {
            // 2. 【CodingAgent】【资源回收】父进程结束后停止输出读取，不等待守护进程持有的管道 EOF
            lifetime.Cancel();
            try { await Task.WhenAll(write, output, error, exit).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>【CodingAgent】【命令输入】完整写入字节并关闭输入，工具提前关闭 stdin 时仍依据退出码判断。</summary>
    /// <param name="stream">进程输入管道。</param><param name="input">输入字节。</param><param name="token">取消信号。</param><returns>写入任务。</returns>
    private static async Task WriteInputAsync(Stream stream, byte[]? input, CancellationToken token)
    {
        try
        {
            if (input is not null) { await stream.WriteAsync(input, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
        }
        catch (IOException) { token.ThrowIfCancellationRequested(); }
        finally { stream.Dispose(); }
    }

    /// <summary>【CodingAgent】【有界输出】逐块检查输出上限，超出时立即终止运行且不保留超限块。</summary>
    /// <param name="stream">输出管道。</param><param name="limit">允许字节数。</param><param name="exceeded">超限回调。</param>
    /// <param name="token">取消信号。</param><returns>完整且未超限的输出。</returns>
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, Action exceeded, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) { exceeded(); token.ThrowIfCancellationRequested(); }
            output.Write(buffer, 0, count);
        }
    }

    /// <summary>【CodingAgent】【忽略输出】持续丢弃诊断或写入命令的输出，不累积无界缓冲。</summary>
    /// <param name="stream">输出管道。</param><param name="token">取消信号。</param><returns>空字节数组。</returns>
    private static async Task<byte[]> DiscardOutputAsync(Stream stream, CancellationToken token)
    { await stream.CopyToAsync(Stream.Null, token).ConfigureAwait(false); return []; }

    /// <summary>【CodingAgent】【进程中止】只终止本次启动且仍运行的进程树，兼容取消与自然退出竞争。</summary>
    /// <param name="process">本次命令进程。</param>
    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
