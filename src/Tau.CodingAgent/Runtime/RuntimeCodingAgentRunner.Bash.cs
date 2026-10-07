// 作者：xxx
using Tau.AgentCore.Harness;
using Tau.AgentCore;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【直接命令选项】控制结果是否进入模型上下文及可选执行后端。</summary>
/// <param name="ExcludeFromContext">是否只保存显示记录而不送入模型。</param>
/// <param name="Operations">可选远程或自定义 Shell 执行操作。</param>
/// <param name="Id">输出更新事件中的可选请求标识。</param>
public sealed record CodingAgentBashExecutionOptions(bool ExcludeFromContext = false, ICodingAgentShellOperations? Operations = null, string? Id = null)
{
    internal long? ContextGeneration { get; init; }
}

/// <summary>【CodingAgent】【命令输出事件】向会话订阅者发送原始增量及可选请求标识。</summary>
/// <param name="Id">RPC 请求标识。</param><param name="Delta">未截断的输出片段。</param>
public sealed record CodingAgentBashExecutionUpdateEvent(string? Id, string Delta) : AgentEvent("bash_execution_update");

public sealed partial class RuntimeCodingAgentRunner
{
    private readonly HashSet<CancellationTokenSource> _bashCancellations = [];
    private readonly Queue<AgentBashExecutionMessage> _pendingBashMessages = new();
    private long _bashContextGeneration;

    /// <summary>【CodingAgent】【命令目录】返回本运行器捕获的工作目录，供用户命令事件使用。</summary>
    internal string WorkingDirectory => _workingDirectory;

    /// <summary>【CodingAgent】【命令上下文】标识当前会话内容，重置或替换后旧命令不能继续写入。</summary>
    internal long BashContextGeneration { get { lock (_backgroundGate) return _bashContextGeneration; } }

    /// <summary>【CodingAgent】【命令运行状态】是否存在尚未结束的直接 Shell 命令。</summary>
    public bool IsBashRunning { get { lock (_backgroundGate) return _bashCancellations.Count > 0; } }

    /// <summary>【CodingAgent】【待写命令结果】是否有等待模型或压缩边界完成后写入的命令记录。</summary>
    public bool HasPendingBashMessages { get { lock (_backgroundGate) return _pendingBashMessages.Count > 0; } }

    /// <summary>【CodingAgent】【直接命令执行】使用会话目录和动态设置执行命令，独立取消并在安全边界记录结果。</summary>
    /// <param name="command">用户命令，不包含 ! 或 !! 前缀。</param><param name="progress">增量输出接收器。</param>
    /// <param name="options">上下文排除和执行后端选项。</param><param name="cancellationToken">命令取消信号。</param>
    /// <returns>包含尾部输出、退出码及取消状态的结果。</returns>
    public async Task<CodingAgentShellResult> ExecuteBashAsync(string command, IProgress<CodingAgentShellEvent>? progress = null,
        CodingAgentBashExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSessionContext(null, null);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long generation;
        lock (_backgroundGate)
        {
            generation = _bashContextGeneration;
            if (options?.ContextGeneration is { } expected && expected != generation)
                throw new InvalidOperationException("Bash session context has changed.");
            _bashCancellations.Add(cancellation);
        }
        try
        {
            // 1. 【CodingAgent】【命令隔离】每个命令拥有独立执行器，多个直接命令不会占用模型运行锁
            var shell = new SystemCodingAgentShellRunner(_workingDirectory,
                () => GetShellOptions() with { Operations = options?.Operations }, GetShellEnvironment)
            {
                OnOutputAsync = update => generation == BashContextGeneration
                    ? PublishBackgroundEventAsync(new CodingAgentBashExecutionUpdateEvent(options?.Id, update.Text), CancellationToken.None)
                    : Task.CompletedTask
            };
            var result = await shell.ExecuteAsync(command, progress, cancellation.Token).ConfigureAwait(false);
            var appended = false;
            lock (_backgroundGate)
            {
                // 2. 【CodingAgent】【会话代次】旧会话中的迟到结果不能污染新建或恢复后的上下文
                if (generation == _bashContextGeneration) appended = RecordBashResultCore(command, result, options?.ExcludeFromContext == true);
            }
            if (appended) _boundarySession?.Snapshot();
            return result;
        }
        finally { lock (_backgroundGate) _bashCancellations.Remove(cancellation); }
    }

    /// <summary>【CodingAgent】【命令结果记录】保存本地或扩展提供的结果，生成期间延迟追加以保护工具调用和结果顺序。</summary>
    /// <param name="command">原始用户命令。</param><param name="result">执行结果。</param><param name="excludeFromContext">是否排除模型上下文。</param>
    public void RecordBashResult(string command, CodingAgentShellResult result, bool excludeFromContext = false)
        => RecordBashResultForContext(command, result, excludeFromContext, null);

    /// <summary>【CodingAgent】【钩子结果归属】在同一个运行状态锁内检查代次并提交结果，阻止会话切换期间的迟到写入。</summary>
    /// <param name="command">原始命令。</param><param name="result">完成结果。</param><param name="excludeFromContext">是否排除模型上下文。</param>
    /// <param name="generation">提交时的会话代次，空值表示当前上下文。</param>
    internal void RecordBashResultForContext(string command, CodingAgentShellResult result, bool excludeFromContext, long? generation)
    {
        ArgumentNullException.ThrowIfNull(command); ArgumentNullException.ThrowIfNull(result);
        EnsureSessionContext(null, null);
        bool appended;
        lock (_backgroundGate) appended = (generation is null || generation == _bashContextGeneration) && RecordBashResultCore(command, result, excludeFromContext);
        if (appended) _boundarySession?.Snapshot();
    }

    /// <summary>【CodingAgent】【命令取消】取消全部直接命令，模型生成的取消信号保持独立。</summary>
    public void AbortBash()
    {
        CancellationTokenSource[] cancellations;
        lock (_backgroundGate) cancellations = _bashCancellations.ToArray();
        foreach (var cancellation in cancellations)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>【CodingAgent】【命令记录提交】当前模型边界已完成时按完成顺序提交延迟命令结果。</summary>
    /// <param name="finishRun">是否同时释放当前模型运行状态。</param>
    private void FlushPendingBashMessages(bool finishRun = false)
    {
        var appended = false;
        lock (_backgroundGate)
        {
            while (_pendingBashMessages.TryDequeue(out var message)) { _runtime.AddMessage(message); appended = true; }
            if (finishRun) _currentRunCancellation = null;
        }
        // 1. 【CodingAgent】【持久化锁序】持久化不持有运行状态锁，允许会话替换先锁存储再清理旧命令
        if (appended) _boundarySession?.Snapshot();
    }

    /// <summary>【CodingAgent】【命令消息组装】在运行状态锁内追加或暂存结果，持久化由锁外调用方负责。</summary>
    /// <param name="command">原始命令。</param><param name="result">执行结果。</param><param name="excludeFromContext">是否排除模型上下文。</param>
    /// <returns>是否已追加消息，需要同步存储。</returns>
    private bool RecordBashResultCore(string command, CodingAgentShellResult result, bool excludeFromContext)
    {
        var message = new AgentBashExecutionMessage(command, result.Output, result.ExitCode, result.Cancelled,
            result.Truncated, result.FullOutputPath, DateTimeOffset.UtcNow, excludeFromContext);
        if (_currentRunCancellation is not null || IsCompacting) { _pendingBashMessages.Enqueue(message); return false; }
        _runtime.AddMessage(message);
        return true;
    }

    /// <summary>【CodingAgent】【命令上下文失效】会话替换清空延迟记录，并让旧命令结果失效。</summary>
    private void ClearBashContext()
    {
        lock (_backgroundGate) { _bashContextGeneration++; _pendingBashMessages.Clear(); }
        AbortBash();
    }
}
