// 作者：xxx
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private int _compactionRequested;
    private CancellationTokenSource? _compactionCancellation;
    private Func<CodingAgentCompactionSessionContext>? _readCompactionContext;
    private Func<CodingAgentCompactionResult, CodingAgentCompactionResult>? _commitPreparedCompaction;
    private Func<string, CodingAgentTreeSessionEntry>? _readCompactionEntry;
    private Tau.AgentCore.Harness.AgentCompactionSettings? _compactionSettingsOverride;

    /// <summary>【CodingAgent】【压缩配置】无持久树会话使用的保留窗口和摘要预算。</summary>
    public Tau.AgentCore.Harness.AgentCompactionSettings CompactionSettings
    {
        get => _compactionSettingsOverride ?? _sessionSettings?.Load().GetCompactionSettings(Model) ?? Tau.AgentCore.Harness.AgentCompactionSettings.Default;
        set => _compactionSettingsOverride = value;
    }

    /// <summary>【CodingAgent】【配置优先级】已绑定原生设置或显式预算时优先于旧树保留参数。</summary>
    internal bool HasCompactionConfiguration => _compactionSettingsOverride is not null || _sessionSettings is not null;

    /// <summary>【CodingAgent】【压缩绑定】共享持久树或内存会话的准备、提交及最终条目读取能力。</summary>
    /// <param name="read">读取当前分支与准备数据。</param>
    /// <param name="commit">提交摘要并恢复保留上下文。</param>
    /// <param name="readEntry">读取已提交摘要条目。</param>
    internal void ConfigureCompactionSession(Func<CodingAgentCompactionSessionContext> read,
        Func<CodingAgentCompactionResult, CodingAgentCompactionResult> commit, Func<string, CodingAgentTreeSessionEntry> readEntry)
    {
        _readCompactionContext = read;
        _commitPreparedCompaction = commit;
        _readCompactionEntry = readEntry;
    }

    /// <summary>【CodingAgent】【手动压缩】终止正在生成的回合，在独占会话期间生成并提交摘要。</summary>
    /// <param name="customInstructions">摘要附加要求。</param>
    /// <param name="cancellationToken">取消压缩的信号。</param>
    /// <returns>压缩结果；失败或取消时不替换原始消息。</returns>
    public Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default) =>
        CompactWithCommitAsync(customInstructions, null, cancellationToken);

    /// <summary>【CodingAgent】【自动压缩】携带真实触发原因执行同一压缩事务。</summary>
    /// <param name="instructions">附加摘要要求。</param>
    /// <param name="reason">threshold 或 overflow。</param>
    /// <param name="willRetry">是否重试原回合。</param>
    /// <param name="token">压缩取消信号。</param>
    /// <returns>完成提交的摘要。</returns>
    internal Task<CodingAgentCompactionResult> CompactForReasonAsync(string? instructions, string reason, bool willRetry, CancellationToken token) =>
        CompactWithCommitAsync(instructions, null, token, reason, willRetry);

    /// <summary>【CodingAgent】【压缩事务】在同一运行锁内保存压缩结果，避免新输入先于 JSONL 提交进入会话。</summary>
    /// <param name="customInstructions">摘要指令。</param>
    /// <param name="commit">摘要生成完成后的同步持久化回调。</param>
    /// <param name="token">压缩取消信号。</param>
    /// <param name="reason">触发原因。</param>
    /// <param name="willRetry">是否重试原回合。</param>
    /// <returns>已提交的摘要结果。</returns>
    internal async Task<CodingAgentCompactionResult> CompactWithCommitAsync(string? customInstructions,
        Action<CodingAgentCompactionResult>? commit, CancellationToken token, string reason = "manual", bool willRetry = false)
    {
        if (Interlocked.CompareExchange(ref _compactionRequested, 1, 0) != 0)
            throw new InvalidOperationException("Compaction is already in progress.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var acquired = false;
        var invocation = new CodingAgentCompactionInvocation(reason, willRetry, _extensionLifecycleEventSink?.CompactionGeneration ?? 0);
        CodingAgentCompactionResult? result = null;
        Exception? failure = null;
        try
        {
            // 1. 【CodingAgent】【压缩取消】先停止原回合，再建立独立的压缩取消源
            Abort();
            lock (_backgroundGate) _compactionCancellation = cancellation;
            await _runGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            acquired = true;
            await PublishBackgroundEventAsync(new CodingAgentCompactionStartEvent(reason), CancellationToken.None).ConfigureAwait(false);
            result = _readCompactionContext is null
                ? await CompactCoreAsync(customInstructions, cancellation.Token).ConfigureAwait(false)
                : await CompactPreparedSessionAsync(customInstructions, invocation, cancellation.Token).ConfigureAwait(false);
            commit?.Invoke(result);
        }
        catch (Exception ex)
        {
            failure = cancellation.IsCancellationRequested && ex is not OperationCanceledException
                ? new OperationCanceledException("Compaction cancelled.", ex, cancellation.Token) : ex;
        }
        finally
        {
            lock (_backgroundGate) _compactionCancellation = null;
            Interlocked.Exchange(ref _compactionRequested, 0);
            if (acquired)
                try { FlushPendingBashMessages(); }
                finally { _runGate.Release(); }
        }
        // 2. 【CodingAgent】【结束重入】先释放运行状态再通知宿主，允许结束订阅者提交下一条输入
        var aborted = failure is OperationCanceledException;
        var error = failure is null || aborted ? null : "Compaction failed: " + failure.Message;
        try
        {
            await PublishBackgroundEventAsync(new CodingAgentCompactionEndEvent(reason, failure is null ? result : null,
                aborted, willRetry, error), CancellationToken.None).ConfigureAwait(false);
            if (failure is not null)
                EmitCompactionHook(JsonSerializer.SerializeToElement(new
                {
                    type = "session_compact_failed", reason, willRetry, fromExtension = invocation.FromExtension, aborted, errorMessage = error
                }), CancellationToken.None, invocation.Generation);
        }
        finally { ScheduleBackgroundDelivery(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
}
