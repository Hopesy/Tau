using Tau.CodingAgent.Tools;
using Tau.AgentCore.Harness;

namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentShellRunner
{
    Task<CodingAgentShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default);

    Task<CodingAgentShellResult> ExecuteAsync(
        string command,
        IProgress<CodingAgentShellEvent>? progress,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, cancellationToken);

    void Abort();
}

public sealed record CodingAgentShellEvent(
    string Stream,
    string Text,
    DateTimeOffset Timestamp);

public sealed record CodingAgentShellResult(
    string Output,
    int? ExitCode,
    bool Cancelled,
    bool Truncated,
    string? FullOutputPath = null);

/// <summary>【CodingAgent】【宿主命令】RPC 命令复用内置 Bash 的目录、输出与进程组取消行为。</summary>
public sealed class SystemCodingAgentShellRunner : ICodingAgentShellRunner
{
    private readonly object _gate = new();
    private readonly string _cwd;
    private readonly Func<CodingAgentShellToolOptions>? _options;
    private readonly Func<IReadOnlyDictionary<string, string?>>? _environment;
    private CancellationTokenSource? _activeCts;

    /// <summary>【CodingAgent】【异步命令输出】会话事件接收器，接收完成后再读取后续分片以提供背压。</summary>
    internal Func<CodingAgentShellEvent, Task>? OnOutputAsync { get; init; }

    /// <summary>【CodingAgent】【宿主命令】捕获所属会话的目录与动态配置来源。</summary>
    /// <param name="workingDirectory">会话目录，空值捕获当前进程目录。</param>
    /// <param name="options">动态执行设置。</param><param name="environment">当前会话元数据。</param>
    public SystemCodingAgentShellRunner(string? workingDirectory = null, Func<CodingAgentShellToolOptions>? options = null,
        Func<IReadOnlyDictionary<string, string?>>? environment = null)
    {
        _cwd = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);
        _options = options;
        _environment = environment;
    }

    /// <summary>【CodingAgent】【宿主命令】执行无需增量通知的 Bash 命令。</summary>
    /// <param name="command">命令。</param><param name="cancellationToken">取消信号。</param><returns>输出及退出状态。</returns>
    public Task<CodingAgentShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, progress: null, cancellationToken);

    /// <summary>【CodingAgent】【宿主命令】执行单个命令并保留原始输出流标签，拒绝并发占用同一执行器。</summary>
    /// <param name="command">命令。</param><param name="progress">增量输出接收器。</param>
    /// <param name="cancellationToken">取消信号。</param><returns>有界尾部及可选完整输出文件。</returns>
    public async Task<CodingAgentShellResult> ExecuteAsync(string command, IProgress<CodingAgentShellEvent>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Shell command cannot be empty.", nameof(command));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            if (_activeCts is not null) throw new InvalidOperationException("A bash command is already running.");
            _activeCts = cts;
        }
        using var output = new CodingAgentShellOutput("tau-bash");
        var receiveGate = new SemaphoreSlim(1, 1);
        var accepting = true;
        try
        {
            var options = _options?.Invoke() ?? new();
            var context = new CodingAgentShellSpawnContext(string.IsNullOrEmpty(options.CommandPrefix) ? command : options.CommandPrefix + "\n" + command,
                _cwd, ShellTool.CreateEnvironment(options.ExposeSessionEnvironment ? _environment?.Invoke() : null));
            context = options.SpawnHook?.Invoke(context) ?? context;
            /// <summary>【CodingAgent】【宿主输出】串行保存双管道内容并等待异步订阅，结束后忽略迟到数据。</summary>
            /// <param name="stream">输出流标签。</param><param name="text">输出片段。</param><returns>接收完成的任务。</returns>
            async Task ReceiveAsync(string stream, string text)
            {
                await receiveGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    CodingAgentShellEvent update;
                    lock (output)
                    {
                        if (!accepting) return;
                        output.Append(text);
                        update = new(stream, text, DateTimeOffset.UtcNow);
                        progress?.Report(update);
                    }
                    if (OnOutputAsync is { } receive) await receive(update).ConfigureAwait(false);
                }
                finally { receiveGate.Release(); }
            }
            int? exitCode = null;
            try
            {
                exitCode = options.Operations is { } custom
                    ? await custom.ExecuteAsync(context, text => ReceiveAsync("stdout", text), cts.Token).ConfigureAwait(false)
                    : await new LocalCodingAgentShellOperations(false, options.ShellPath).ExecuteWithStreamsAsync(context, ReceiveAsync, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            // 1. 【CodingAgent】【输出收尾】等待已接收分片完成发布，再返回最终结果
            await receiveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (output)
                {
                    accepting = false;
                    output.Dispose();
                    var snapshot = output.Snapshot();
                    return new(snapshot.Content, cts.IsCancellationRequested ? null : exitCode, cts.IsCancellationRequested, snapshot.Truncated, output.FullOutputPath);
                }
            }
            finally { receiveGate.Release(); }
        }
        finally
        {
            lock (output) accepting = false;
            lock (_gate) if (ReferenceEquals(_activeCts, cts)) _activeCts = null;
        }
    }

    /// <summary>【CodingAgent】【宿主取消】取消当前命令，实际执行管线负责结束并排空进程组。</summary>
    public void Abort()
    {
        lock (_gate) _activeCts?.Cancel();
    }
}
