// 作者：xxx
using System.Diagnostics;
using System.Text;
using Tau.AgentCore.Harness.Env;

namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【命令上下文】扩展或 SDK 可调整的命令、目录和独立环境快照。</summary>
public sealed record CodingAgentShellSpawnContext(string Command, string Cwd, IReadOnlyDictionary<string, string?> Environment);
/// <summary>【CodingAgent】【命令选项】支持自定义执行后端、Bash 配置、会话环境和启动钩子。</summary>
public sealed record CodingAgentShellToolOptions
{
    public ICodingAgentShellOperations? Operations { get; init; }
    public string? ShellPath { get; init; }
    public string? CommandPrefix { get; init; }
    public bool ExposeSessionEnvironment { get; init; } = true;
    public Func<CodingAgentShellSpawnContext, CodingAgentShellSpawnContext>? SpawnHook { get; init; }
}

/// <summary>【CodingAgent】【命令后端】为本地或远程执行提供流式输出及取消契约。</summary>
public interface ICodingAgentShellOperations
{
    /// <summary>【CodingAgent】【执行命令】执行并按收到的顺序投递输出，非零退出码由工具转换为错误结果。</summary>
    /// <param name="context">命令、目录及环境。</param><param name="onData">合并输出接收器。</param>
    /// <param name="token">超时或主动取消信号。</param><returns>退出码，空值表示异常终止。</returns>
    Task<int?> ExecuteAsync(CodingAgentShellSpawnContext context, Func<string, Task> onData, CancellationToken token);
}

/// <summary>【CodingAgent】【本地命令】使用真正的 Bash 或 PowerShell 进程，取消时终止并等待完整子进程树。</summary>
internal sealed class LocalCodingAgentShellOperations(bool powershell, string? shellPath) : ICodingAgentShellOperations
{
    /// <summary>【CodingAgent】【进程执行】并行读取两个输出管道，回调失败或取消时结束进程并排空读取任务。</summary>
    /// <param name="context">执行上下文。</param><param name="onData">流式输出接收器。</param><param name="token">取消信号。</param>
    /// <returns>进程退出码。</returns>
    public Task<int?> ExecuteAsync(CodingAgentShellSpawnContext context, Func<string, Task> onData, CancellationToken token) =>
        ExecuteWithStreamsAsync(context, (_, text) => onData(text), token);

    /// <summary>【CodingAgent】【进程执行】为宿主提供保留 stdout/stderr 标签的同一执行管线。</summary>
    /// <param name="context">执行上下文。</param><param name="onData">流名称及文本接收器。</param><param name="token">取消信号。</param><returns>退出码。</returns>
    internal async Task<int?> ExecuteWithStreamsAsync(CodingAgentShellSpawnContext context, Func<string, string, Task> onData, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(context.Cwd)) throw new DirectoryNotFoundException("Working directory does not exist: " + context.Cwd);
        var shell = powershell ? ResolvePowerShell() : await SystemAgentExecutionEnv.ResolveShellAsync(shellPath, token).ConfigureAwait(false);
        var input = shell.CommandTransport == SystemAgentExecutionEnv.AgentShellCommandTransport.Stdin;
        var start = new ProcessStartInfo(shell.Path)
        {
            WorkingDirectory = context.Cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in shell.Arguments) start.ArgumentList.Add(argument);
        var command = powershell ? "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {}\n" + context.Command : context.Command;
        if (!input) start.ArgumentList.Add(command);
        start.Environment.Clear();
        foreach (var pair in context.Environment) if (pair.Value is not null) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Failed to start shell process.");
        using var job = CodingAgentWindowsJob.Attach(process);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var abort = token.Register(() => Kill(process, job));
        var lastOutput = Stopwatch.GetTimestamp();
        var pendingCallbacks = 0;
        /// <summary>【CodingAgent】【管道活跃】记录输出到达与处理结束时间，避免后台进程继承管道导致永久等待。</summary>
        /// <param name="stream">流名称。</param><param name="text">新输出。</param><returns>回调完成的任务。</returns>
        async Task ReceiveAsync(string stream, string text)
        {
            Interlocked.Increment(ref pendingCallbacks);
            try { await onData(stream, text).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref lastOutput, Stopwatch.GetTimestamp()); Interlocked.Decrement(ref pendingCallbacks); }
        }
        // 1. 【CodingAgent】【输出管道】两个流均立即消费，回调抛错时先终止进程以解除另一条管道的等待
        var stdout = ReadOutputAsync(process, job, process.StandardOutput, text => ReceiveAsync("stdout", text), reading.Token);
        var stderr = ReadOutputAsync(process, job, process.StandardError, text => ReceiveAsync("stderr", text), reading.Token);
        var readers = Task.WhenAll(stdout, stderr);
        try
        {
            if (input)
            {
                await process.StandardInput.WriteAsync(command.AsMemory(), token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            // 2. 【CodingAgent】【退出排空】主进程退出后继续接收活跃输出，仅在管道空闲一百毫秒后解除继承句柄的等待
            Interlocked.Exchange(ref lastOutput, Stopwatch.GetTimestamp());
            while (!readers.IsCompleted)
            {
                await Task.WhenAny(readers, Task.Delay(100, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref pendingCallbacks) == 0 && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastOutput)).TotalMilliseconds >= 100)
                { await reading.CancelAsync().ConfigureAwait(false); break; }
            }
            try { await readers.ConfigureAwait(false); }
            catch (OperationCanceledException) when (reading.IsCancellationRequested && !token.IsCancellationRequested) { }
            token.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            if (token.IsCancellationRequested || !process.HasExited || readers.IsFaulted) Kill(process, job);
            await reading.CancelAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await readers.ConfigureAwait(false); }
            catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        }
    }

    /// <summary>【CodingAgent】【管道消费】完整接收 UTF-8 字符并等待输出处理，避免字符或错误任务丢失。</summary>
    /// <param name="process">所属进程。</param><param name="job">可选 Windows 进程组。</param><param name="reader">输出管道。</param><param name="onData">输出接收器。</param>
    /// <param name="token">取消信号。</param><returns>读到结束时完成的任务。</returns>
    private static async Task ReadOutputAsync(Process process, CodingAgentWindowsJob? job, StreamReader reader, Func<string, Task> onData, CancellationToken token)
    {
        try
        {
            var buffer = new char[4096];
            var pending = "";
            while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) is var length && length > 0)
            {
                var text = pending + new string(buffer, 0, length);
                pending = char.IsHighSurrogate(text[^1]) ? text[^1..] : "";
                if (pending.Length > 0) text = text[..^1];
                if (text.Length > 0) await onData(text).ConfigureAwait(false);
            }
            if (pending.Length > 0) await onData("\uFFFD").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Kill(process, job); throw; }
    }

    /// <summary>【CodingAgent】【进程取消】仅结束本次创建的进程及子进程，处理其已经退出的竞态。</summary>
    /// <param name="process">本次进程。</param><param name="job">可选 Windows 进程组。</param>
    private static void Kill(Process process, CodingAgentWindowsJob? job)
    {
        // 1. 【CodingAgent】【取消竞态】两个管道和等待任务可能同时取消，互斥终止以防父进程先退出导致后续子树查询失效
        lock (process)
        {
            try
            {
                job?.Terminate();
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>【CodingAgent】【PowerShell】在 Windows 的 PATH 中优先选择 pwsh，再选择 powershell。</summary>
    /// <returns>不加载用户配置的非交互 PowerShell 配置。</returns>
    private static SystemAgentExecutionEnv.AgentShellConfig ResolvePowerShell()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The powershell tool is only available on Windows.");
        foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(path)) return new(path, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"], SystemAgentExecutionEnv.AgentShellCommandTransport.Arguments);
            }
        throw new FileNotFoundException("No PowerShell executable found.");
    }
}
