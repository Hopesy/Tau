// 作者：xxx
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private bool? _autoCompactionEnabledOverride;

    /// <summary>【CodingAgent】【压缩开关】读取宿主显式开关或原生设置。</summary>
    public bool AutoCompactionEnabled => _autoCompactionEnabledOverride ?? CompactionSettings.Enabled;

    /// <summary>【CodingAgent】【压缩开关】更新宿主开关；空值重新采用原生配置。</summary>
    /// <param name="enabled">自动压缩开关或空值。</param>
    public void SetAutoCompactionEnabled(bool? enabled) => _autoCompactionEnabledOverride = enabled;

    /// <summary>【CodingAgent】【会话绑定】独立创建宿主时也提供原生恢复记录，已有扩展绑定保持有效。</summary>
    /// <param name="tree">持久树控制器。</param>
    /// <param name="flat">旧式平面存储。</param>
    internal void EnsureSessionContext(CodingAgentTreeSessionController? tree, CodingAgentSessionStore? flat)
    {
        if (_readCompactionContext is null) _ = new CodingAgentExtensionSessionBridge(this, tree, flat, Environment.CurrentDirectory);
    }

    /// <summary>【CodingAgent】【恢复状态】一次用户运行共享一次溢出恢复预算。</summary>
    private sealed class RecoveryState
    {
        public bool OverflowAttempted { get; set; }
        public bool Continue { get; set; }
    }

    /// <summary>【CodingAgent】【路由压缩】发送前按物理模型窗口检查当前投影，并把自动压缩事件合并入运行事件流。</summary>
    /// <param name="model">本次已经选定的物理模型。</param><param name="token">原请求取消信号。</param>
    /// <returns>达到阈值并执行压缩流程时返回真，调用者随后重建请求上下文。</returns>
    private async Task<bool> CompactBeforeRoutedRequestAsync(Model model, CancellationToken token)
    {
        if (!AutoCompactionEnabled || model.ContextWindow is not > 0 || _readCompactionContext?.Invoke() is not { Preparation: not null } context)
            return false;
        var projection = CodingAgentTreeSessionStore.ProjectBranch(context.Branch);
        var estimate = CodingAgentProjectedCompaction.EstimateContextUsage(projection, context.Branch);
        if (!AgentCompaction.ShouldCompact(estimate.Tokens, model.ContextWindow.Value, CompactionSettings with { Enabled = true })) return false;
        await foreach (var evt in RunAutomaticCompactionAsync("threshold", false, new RecoveryState(), token).ConfigureAwait(false))
            _nestedToolEvents?.TryWrite(evt);
        return true;
    }

    /// <summary>【CodingAgent】【自动压缩检查】按当前分支、模型窗口、有效用量和可见性决定是否压缩。</summary>
    /// <param name="assistant">最近一次助手响应。</param>
    /// <param name="toolResults">该响应对应的工具结果。</param>
    /// <param name="state">此次用户运行的恢复预算和继续标记。</param>
    /// <param name="token">运行取消信号。</param>
    /// <param name="skipAborted">响应被取消时是否跳过；新输入前允许检查旧取消响应。</param>
    /// <returns>上下文编辑和压缩生命周期事件。</returns>
    private async IAsyncEnumerable<AgentEvent> CheckAutomaticCompactionAsync(AssistantMessage? assistant,
        IReadOnlyList<ToolResultMessage> toolResults, RecoveryState state, [EnumeratorCancellation] CancellationToken token,
        bool skipAborted = true)
    {
        state.Continue = false;
        var settings = CompactionSettings with { Enabled = AutoCompactionEnabled };
        if (assistant is null || !settings.Enabled || token.IsCancellationRequested ||
            skipAborted && assistant.StopReason == StopReason.Aborted || _readCompactionContext is null) yield break;
        var branch = _readCompactionContext().Branch;
        var assistantIndex = FindRecoveryMessageIndex(branch, assistant);
        var compactionIndex = Enumerable.Range(0, branch.Count).LastOrDefault(index => branch[index].Type == "compaction", -1);
        // 1. 【CodingAgent】【陈旧用量】按持久条目顺序判断边界，避免同毫秒时间戳误判新响应
        if (assistantIndex >= 0 && assistantIndex <= compactionIndex) yield break;
        var projection = CodingAgentTreeSessionStore.ProjectBranch(branch);
        var sourceId = assistantIndex >= 0 ? branch[assistantIndex].Id : null;
        var visible = sourceId is null || projection.Entries.Any(entry => entry.SourceEntry.GetProperty("id").GetString() == sourceId &&
            entry.Messages.OfType<AssistantMessage>().Any());
        var after = assistantIndex >= 0 ? branch.Skip(assistantIndex + 1).ToArray() : [];
        var latestEdit = after.LastOrDefault(entry => entry.Type == "context_edit" && entry.TargetId == sourceId);
        var retained = sourceId is null || !after.Any(entry => entry.Type == "compaction") &&
            (latestEdit is null || latestEdit.Replacement is { ValueKind: not JsonValueKind.Null });
        var limits = GetEffectiveContextModel(assistant);
        var sameModel = assistant.Provider == limits.Provider && assistant.Model == limits.Id;
        var window = limits.ContextWindow ?? 0;
        var overflow = sameModel &&
            (assistant.StopReason == StopReason.Error && retained && ContextOverflowDetector.IsContextOverflowError(assistant.ErrorMessage) ||
                visible && !after.Any(entry => entry.Type == "context_edit") && ContextOverflowDetector.IsContextOverflow(assistant, window));
        var length = sameModel && visible && ContextOverflowDetector.IsRecoverableLength(assistant, limits.MaxOutputTokens ?? 0);
        if (overflow || length)
        {
            var willRetry = assistant.StopReason != StopReason.EndTurn;
            if (willRetry && state.OverflowAttempted)
            {
                var error = overflow
                    ? "Context overflow recovery failed after one compact-and-retry attempt. Try reducing context or switching to a larger-context model."
                    : "Truncated response recovery failed after one compact-and-retry attempt.";
                yield return new CodingAgentCompactionEndEvent("overflow", null, false, false, error);
                EmitCompactionHook(JsonSerializer.SerializeToElement(new
                {
                    type = "session_compact_failed", reason = "overflow", errorMessage = error, aborted = false, willRetry = false, fromExtension = false
                }), CancellationToken.None, _extensionLifecycleEventSink?.CompactionGeneration ?? 0);
                yield break;
            }
            // 2. 【CodingAgent】【溢出省略】先保留原文并省略失败助手与关联工具结果，再根据新投影准备摘要
            if (willRetry)
            {
                state.OverflowAttempted = true;
                foreach (var evt in OmitRecoveryMessages([assistant, .. toolResults])) yield return evt;
            }
            await foreach (var evt in RunAutomaticCompactionAsync("overflow", willRetry, state, token).ConfigureAwait(false)) yield return evt;
            if (state.Continue && willRetry) _failedVirtualResponse = assistant;
            yield break;
        }
        // 3. 【CodingAgent】【阈值检查】上下文编辑后的旧用量作废，压缩后的保留响应也不重复触发阈值
        if (window <= 0) yield break;
        var estimate = CodingAgentProjectedCompaction.EstimateContextUsage(projection, branch);
        if (!branch.Any(entry => entry.Type == "context_edit") && compactionIndex >= 0 &&
            AgentCompaction.EstimateContextTokens(projection.Messages).LastUsageIndex is { } usageIndex &&
            projection.Messages[usageIndex] is AssistantMessage usageMessage && FindRecoveryMessageIndex(branch, usageMessage) <= compactionIndex)
            yield break;
        if (AgentCompaction.ShouldCompact(estimate.Tokens, window, settings))
            await foreach (var evt in RunAutomaticCompactionAsync("threshold", false, state, token).ConfigureAwait(false)) yield return evt;
    }

    /// <summary>【CodingAgent】【原生自动压缩】在当前运行锁内生成与保存摘要，不取消自身或重复取得运行锁。</summary>
    /// <param name="reason">threshold 或 overflow。</param>
    /// <param name="willRetry">完成后是否继续中断的请求。</param>
    /// <param name="state">恢复继续标记。</param>
    /// <param name="token">原运行取消信号。</param>
    /// <returns>压缩开始和结束事件。</returns>
    private async IAsyncEnumerable<AgentEvent> RunAutomaticCompactionAsync(string reason, bool willRetry, RecoveryState state,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (_readCompactionContext?.Invoke().Preparation is null || Interlocked.CompareExchange(ref _compactionRequested, 1, 0) != 0) yield break;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_backgroundGate) _compactionCancellation = cancellation;
        var invocation = new CodingAgentCompactionInvocation(reason, willRetry, _extensionLifecycleEventSink?.CompactionGeneration ?? 0);
        CodingAgentCompactionResult? result = null;
        Exception? failure = null;
        try
        {
            yield return new CodingAgentCompactionStartEvent(reason);
            try { result = await CompactPreparedSessionAsync(null, invocation, cancellation.Token).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
        }
        finally
        {
            lock (_backgroundGate) _compactionCancellation = null;
            Interlocked.Exchange(ref _compactionRequested, 0);
        }
        var aborted = failure is OperationCanceledException || failure is not null && cancellation.IsCancellationRequested;
        var error = failure is null || aborted ? null : (reason == "overflow" ? "Context overflow recovery failed: " : "Auto-compaction failed: ") + failure.Message;
        yield return new CodingAgentCompactionEndEvent(reason, result, aborted, failure is null && willRetry, error);
        if (failure is not null)
            EmitCompactionHook(JsonSerializer.SerializeToElement(new
            {
                type = "session_compact_failed", reason, willRetry = false, fromExtension = invocation.FromExtension, aborted, errorMessage = error
            }), CancellationToken.None, invocation.Generation);
        state.Continue = failure is null && !token.IsCancellationRequested && (willRetry || PendingMessageCount > 0);
    }

    /// <summary>【CodingAgent】【消息定位】使用 pi 规范字段定位当前分支中的原始消息。</summary>
    /// <param name="branch">当前分支。</param>
    /// <param name="message">待定位的消息。</param>
    /// <returns>最后一个匹配来源的索引；找不到时为负一。</returns>
    private static int FindRecoveryMessageIndex(IReadOnlyList<CodingAgentTreeSessionEntry> branch, ChatMessage message)
    {
        var expected = JsonSerializer.SerializeToElement(CodingAgentSessionStore.FromMessage(message), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage);
        for (var index = branch.Count - 1; index >= 0; index--)
            if (branch[index].Message is { } stored && JsonElement.DeepEquals(expected,
                JsonSerializer.SerializeToElement(stored, CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage))) return index;
        return -1;
    }
}
