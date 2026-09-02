using Tau.Ai;
using Tau.AgentCore.Harness.Session;

namespace Tau.AgentCore.Harness;

/// <summary>
/// 可持久化 Harness record-log 的错误类别。
/// </summary>
public enum RecordLogCorruptionReason
{
    MultipleOpenOperations,
    UnknownOperation,
    RecordAfterFinish,
    NonConsecutiveAttempt,
    InvalidCompactionReason,
    QueueAfterAbort,
    InvalidQueueCancellation,
    InconsistentStep,
    ToolCallMismatch,
    DuplicateToolInvocation,
    ProvisionedEntryMismatch,
    InvalidDeferredHandle,
    InvalidRecord
}

/// <summary>
/// 表示 record-log 与单写入协议矛盾的错误。
/// </summary>
public sealed class RecordLogCorruptionException : Exception
{
    /// <summary>错误分类。</summary>
    public RecordLogCorruptionReason Reason { get; }
    /// <summary>与参考 TypeScript 协议一致的 snake_case 错误码。</summary>
    public string Code => Reason switch
    {
        RecordLogCorruptionReason.MultipleOpenOperations => "multiple_open_operations",
        RecordLogCorruptionReason.UnknownOperation => "unknown_operation",
        RecordLogCorruptionReason.RecordAfterFinish => "record_after_finish",
        RecordLogCorruptionReason.NonConsecutiveAttempt => "non_consecutive_attempt",
        RecordLogCorruptionReason.InvalidCompactionReason => "invalid_compaction_reason",
        RecordLogCorruptionReason.QueueAfterAbort => "queue_after_abort",
        RecordLogCorruptionReason.InvalidQueueCancellation => "invalid_queue_cancellation",
        RecordLogCorruptionReason.InconsistentStep => "inconsistent_step",
        RecordLogCorruptionReason.ToolCallMismatch => "tool_call_mismatch",
        RecordLogCorruptionReason.DuplicateToolInvocation => "duplicate_tool_invocation",
        RecordLogCorruptionReason.ProvisionedEntryMismatch => "provisioned_entry_mismatch",
        RecordLogCorruptionReason.InvalidDeferredHandle => "invalid_deferred_handle",
        _ => "invalid_record"
    };
    /// <summary>创建 record-log 错误。</summary>
    public RecordLogCorruptionException(RecordLogCorruptionReason reason, string message) : base(message) { Reason = reason; }
}

/// <summary>
/// 一条通用 Harness lane 记录。
/// </summary>
public sealed record HarnessRecord(string Id, string Type, string? RunId = null, long Sequence = 0, string? EntryId = null);

/// <summary>参考 Harness 的基础 lane 记录。</summary>
public abstract record LaneRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string Type);

/// <summary>操作开始记录。</summary>
public sealed record OperationStartedRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string OperationKind, string? SourceLeafId = null)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "operation_started");

/// <summary>操作结束记录。</summary>
public sealed record OperationFinishedRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string RunId, string Outcome)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "operation_finished");

/// <summary>操作中止请求记录。</summary>
public sealed record AbortRequestedRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string RunId)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "abort_requested");

/// <summary>步骤尝试记录。</summary>
public sealed record StepAttemptRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string RunId, string Step, int Attempt, string ResultEntryId, string? CompactionReason = null)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "step_attempt");

/// <summary>工具调用开始记录。</summary>
public sealed record ToolStartedRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string RunId, string AssistantEntryId, int ToolIndex, string ToolCallId, string ToolName, string ResultEntryId)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "tool_started");

/// <summary>队列入队记录。</summary>
public sealed record QueueEnqueuedRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string Queue, string EntryId, string? RunId = null)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "queue_enqueued");

/// <summary>队列取消记录。</summary>
public sealed record QueueCancelledRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string EntryId, string? RunId = null)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "queue_cancelled");

/// <summary>延迟写入记录。</summary>
public sealed record WriteDeferredRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string RunId, string EntryId)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "write_deferred");

/// <summary>用量记录。</summary>
public sealed record UsageRecord(string Id, long Sequence, string Lane, DateTimeOffset Timestamp, string Cause, string? RunId, string? EntryId, int Attempt = 0, Usage? Usage = null)
    : LaneRecord(Id, Sequence, Lane, Timestamp, "usage");

/// <summary>用于恢复 lane 状态的有界输入。</summary>
public sealed record HarnessRecordLogSlice(string Lane, IReadOnlyList<HarnessRecord> Records, IReadOnlyList<string> OpenOperationIds);

/// <summary>参考 reducer 的 lane 记录切片。</summary>
public sealed record LaneRecordLogSlice(string Lane, IReadOnlyList<LaneRecord> Records, IReadOnlyList<OperationStartedRecord> OpenOperations);

/// <summary>
/// 包含 session entry 的完整 record-log 恢复切片。
/// </summary>
/// <param name="Lane">lane 名称。</param>
/// <param name="Records">lane 记录。</param>
/// <param name="OpenOperations">恢复时检测到的未结束操作。</param>
/// <param name="Entries">用于校验结果、工具调用和 deferred 句柄的 entry。</param>
public sealed record RecordLogSlice(
    string Lane,
    IReadOnlyList<LaneRecord> Records,
    IReadOnlyList<OperationStartedRecord> OpenOperations,
    IReadOnlyList<SessionTreeEntry> Entries);

/// <summary>参考 reducer 恢复后的 lane 状态快照。</summary>
public sealed record LaneStateSnapshot(string Lane, string? LeafId, bool Aborting, string? OperationId, string? OperationKind, int Attempts, IReadOnlyList<string> PendingSteer, IReadOnlyList<string> PendingFollowUp, IReadOnlyList<string> PendingNextRun);

/// <summary>恢复后的 lane 状态。</summary>
public sealed record HarnessLaneState(string Lane, bool IsRunning, string? OperationId, IReadOnlyList<string> PendingEntryIds);

/// <summary>lane 恢复时使用的有效模型和工具配置。</summary>
/// <param name="Provider">provider 标识。</param>
/// <param name="ModelId">模型标识。</param>
/// <param name="ThinkingLevel">思考级别。</param>
/// <param name="ActiveToolNames">当前启用的工具名称。</param>
public sealed record EffectiveLaneConfiguration(
    string? Provider,
    string? ModelId,
    string ThinkingLevel,
    IReadOnlyList<string> ActiveToolNames);

/// <summary>恢复后仍未完成的终止性 assistant 错误。</summary>
/// <param name="EntryId">错误 assistant entry id。</param>
/// <param name="Source">错误来自步骤还是 deferred 拉取。</param>
/// <param name="Message">错误 assistant 消息。</param>
public sealed record TerminalFailureState(string EntryId, string Source, AssistantMessage Message);

/// <summary>恢复后的操作状态。</summary>
/// <param name="Id">操作 id。</param>
/// <param name="Kind">操作类型。</param>
/// <param name="Aborting">是否已经收到 abort 请求。</param>
/// <param name="Attempts">当前步骤已使用的最大 attempt 序号。</param>
/// <param name="PendingSteer">尚未消费的 steering entry id。</param>
/// <param name="PendingFollowUp">尚未消费的 follow-up entry id。</param>
/// <param name="PendingWrites">尚未完成的 deferred 写入 entry id。</param>
/// <param name="MissingInitialMessages">初始消息中尚未落盘的 entry id。</param>
/// <param name="Deferred">最近 deferred assistant 句柄。</param>
/// <param name="OverflowRecoveryUsed">是否已执行 overflow compaction 恢复。</param>
/// <param name="NewestOwnEntryId">操作自己写入的最新 entry id。</param>
/// <param name="TargetResultExists">操作结果 entry 是否已存在。</param>
/// <param name="TargetSummaryExists">导航摘要 entry 是否已存在。</param>
public sealed record LaneOperationState(
    string Id,
    string Kind,
    bool Aborting,
    int Attempts,
    IReadOnlyList<string> PendingSteer,
    IReadOnlyList<string> PendingFollowUp,
    IReadOnlyList<string> PendingWrites,
    IReadOnlyList<string> MissingInitialMessages,
    DeferredHandle? Deferred,
    bool OverflowRecoveryUsed,
    string? NewestOwnEntryId,
    bool TargetResultExists,
    bool TargetSummaryExists);

/// <summary>完整 lane 状态，供恢复、诊断和 UI 使用。</summary>
/// <param name="Lane">lane 名称。</param>
/// <param name="LeafId">当前分支叶节点。</param>
/// <param name="Operation">未结束操作；lane 空闲时为空。</param>
/// <param name="PendingNextRun">等待下一轮消费的 entry id。</param>
public sealed record LaneState(
    string Lane,
    string? LeafId,
    LaneOperationState? Operation,
    IReadOnlyList<string> PendingNextRun);

/// <summary>完整 lane reduction 输入。</summary>
/// <param name="Slice">record-log 与已加载 entries。</param>
/// <param name="LeafId">当前 lane 叶节点。</param>
/// <param name="OwnEntries">当前操作自己追加的 entries，按旧到新排列。</param>
/// <param name="ConfigurationEntries">用于推导有效配置的 entries，按旧到新排列。</param>
/// <param name="DefaultConfiguration">没有持久化设置时使用的默认配置。</param>
public sealed record LaneReductionInput(
    RecordLogSlice Slice,
    string? LeafId,
    IReadOnlyList<SessionTreeEntry> OwnEntries,
    IReadOnlyList<SessionTreeEntry> ConfigurationEntries,
    EffectiveLaneConfiguration DefaultConfiguration);

/// <summary>完整 lane reduction 结果。</summary>
/// <param name="LaneState">恢复后的 lane 状态。</param>
/// <param name="EffectiveConfiguration">生效配置。</param>
/// <param name="TerminalFailure">需要上层继续处理的终止错误。</param>
public sealed record LaneReductionResult(
    LaneState LaneState,
    EffectiveLaneConfiguration EffectiveConfiguration,
    TerminalFailureState? TerminalFailure);

/// <summary>
/// 校验并纯函数恢复 Harness lane 状态，拒绝无法由单写入协议产生的矛盾。
/// </summary>
public static class HarnessRecordReducer
{
    /// <summary>校验 record-log。</summary>
    /// <param name="input">待校验的记录切片。</param>
    public static void ValidateRecordLog(HarnessRecordLogSlice input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.OpenOperationIds.Count > 1) throw new RecordLogCorruptionException(RecordLogCorruptionReason.MultipleOpenOperations, "A lane cannot have multiple open operations.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in input.Records.OrderBy(item => item.Sequence))
        {
            if (!seen.Add(record.Id)) throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidRecord, $"Duplicate record id '{record.Id}'.");
            if (record.RunId is not null && !input.OpenOperationIds.Contains(record.RunId) && !finished.Contains(record.RunId))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.UnknownOperation, $"Unknown operation '{record.RunId}'.");
            if (record.RunId is not null && finished.Contains(record.RunId))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.RecordAfterFinish, $"Record '{record.Id}' follows operation finish.");
            if (record.Type.Equals("operation_finished", StringComparison.Ordinal) && record.RunId is not null) finished.Add(record.RunId);
        }
    }

    /// <summary>校验并恢复 lane 状态。</summary>
    /// <param name="input">待恢复的记录切片。</param>
    /// <returns>恢复后的状态。</returns>
    public static HarnessLaneState ReduceLaneState(HarnessRecordLogSlice input)
    {
        ValidateRecordLog(input);
        var operation = input.OpenOperationIds.FirstOrDefault();
        var pending = input.Records.Where(record => record.Type.Equals("queue_enqueued", StringComparison.OrdinalIgnoreCase) && record.EntryId is not null).Select(record => record.EntryId!).ToArray();
        return new HarnessLaneState(input.Lane, operation is not null, operation, pending);
    }

    /// <summary>
    /// 校验参考 LaneRecord 切片中的操作、步骤和队列一致性。
    /// </summary>
    /// <param name="input">待校验切片。</param>
    public static void ValidateRecordLog(LaneRecordLogSlice input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.OpenOperations.Count > 1) throw new RecordLogCorruptionException(RecordLogCorruptionReason.MultipleOpenOperations, "A lane cannot have multiple open operations.");
        var records = input.Records.OrderBy(record => record.Sequence).ToArray();
        var started = input.OpenOperations.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var attempts = new Dictionary<(string RunId, string Step), int>();
        var tools = new HashSet<(string AssistantEntryId, int ToolIndex)>();
        var aborted = new HashSet<string>(StringComparer.Ordinal);
        var enqueues = new Dictionary<string, QueueEnqueuedRecord>(StringComparer.Ordinal);
        var seenRecordIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!seenRecordIds.Add(record.Id))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidRecord, $"Duplicate record id '{record.Id}'.");
            if (record is not OperationFinishedRecord && record is not AbortRequestedRecord && record is not OperationStartedRecord && record is not StepAttemptRecord && record is not ToolStartedRecord && record is not QueueEnqueuedRecord && record is not QueueCancelledRecord && record is not WriteDeferredRecord && record is not UsageRecord)
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidRecord, $"Unsupported lane record '{record.Type}'.");
            if (record is OperationFinishedRecord finish)
            {
                if (!started.Contains(finish.RunId))
                    throw new RecordLogCorruptionException(RecordLogCorruptionReason.UnknownOperation, $"Unknown operation '{finish.RunId}'.");
                finished.Add(finish.RunId);
                continue;
            }
            if (record is OperationStartedRecord repeatedStart && finished.Contains(repeatedStart.Id))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.RecordAfterFinish, $"Operation '{repeatedStart.Id}' follows finish.");
            if (record is AbortRequestedRecord abort)
            {
                if (!started.Contains(abort.RunId))
                    throw new RecordLogCorruptionException(RecordLogCorruptionReason.UnknownOperation, $"Unknown operation '{abort.RunId}'.");
                aborted.Add(abort.RunId);
                continue;
            }
            var ownedRunId = record switch
            {
                StepAttemptRecord attemptRecord => attemptRecord.RunId,
                ToolStartedRecord toolRecord => toolRecord.RunId,
                QueueEnqueuedRecord queueRecord => queueRecord.RunId,
                QueueCancelledRecord cancelRecord => cancelRecord.RunId,
                WriteDeferredRecord deferredRecord => deferredRecord.RunId,
                UsageRecord usageRecord => usageRecord.RunId,
                _ => null
            };
            if (ownedRunId is not null && finished.Contains(ownedRunId))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.RecordAfterFinish, $"Record '{record.Id}' follows operation finish.");
            if (ownedRunId is not null && !started.Contains(ownedRunId))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.UnknownOperation, $"Unknown operation '{ownedRunId}'.");
            switch (record)
            {
                case OperationStartedRecord operation:
                    started.Add(operation.Id);
                    break;
                case StepAttemptRecord attempt:
                    if (!started.Contains(attempt.RunId)) throw new RecordLogCorruptionException(RecordLogCorruptionReason.UnknownOperation, $"Unknown operation '{attempt.RunId}'.");
                    var attemptKey = (attempt.RunId, attempt.Step);
                    if (attempt.Attempt < 1 || (attempts.TryGetValue(attemptKey, out var previous) && attempt.Attempt != previous + 1))
                        throw new RecordLogCorruptionException(RecordLogCorruptionReason.NonConsecutiveAttempt, $"Step attempt '{attempt.Id}' is not consecutive.");
                    if (attempt.Step.Equals("compaction", StringComparison.OrdinalIgnoreCase) && attempt.CompactionReason is not ("manual" or "threshold" or "overflow"))
                        throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidCompactionReason, $"Compaction attempt '{attempt.Id}' has an invalid reason.");
                    if (!attempt.Step.Equals("compaction", StringComparison.OrdinalIgnoreCase) && attempt.CompactionReason is not null)
                        throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidCompactionReason, $"Non-compaction attempt '{attempt.Id}' has a compaction reason.");
                    attempts[attemptKey] = attempt.Attempt;
                    break;
                case ToolStartedRecord tool when !tools.Add((tool.AssistantEntryId, tool.ToolIndex)):
                    throw new RecordLogCorruptionException(RecordLogCorruptionReason.DuplicateToolInvocation, $"Tool invocation '{tool.AssistantEntryId}:{tool.ToolIndex}' was started more than once.");
                case ToolStartedRecord tool when !started.Contains(tool.RunId):
                    throw new RecordLogCorruptionException(RecordLogCorruptionReason.UnknownOperation, $"Unknown operation '{tool.RunId}'.");
                case QueueEnqueuedRecord queue when queue.RunId is not null && aborted.Contains(queue.RunId):
                    throw new RecordLogCorruptionException(RecordLogCorruptionReason.QueueAfterAbort, $"Queue entry '{queue.EntryId}' follows abort.");
                case QueueEnqueuedRecord queue:
                    if (string.IsNullOrWhiteSpace(queue.EntryId))
                        throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidRecord, "Queue enqueue requires an entry id.");
                    enqueues[queue.EntryId] = queue;
                    break;
                case QueueCancelledRecord cancellation when string.IsNullOrWhiteSpace(cancellation.EntryId):
                    throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidQueueCancellation, "Queue cancellation requires an entry id.");
            }
            if (record is QueueCancelledRecord cancel)
            {
                if (cancel.RunId is not null && aborted.Contains(cancel.RunId))
                    throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidQueueCancellation, $"Queue cancellation '{cancel.EntryId}' follows abort.");
                if (!enqueues.TryGetValue(cancel.EntryId, out var enqueue) ||
                    enqueue.Sequence >= cancel.Sequence ||
                    !string.Equals(enqueue.RunId, cancel.RunId, StringComparison.Ordinal))
                    throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidQueueCancellation, $"Queue cancellation '{cancel.EntryId}' has no pending matching enqueue.");
            }
            if (record is LaneRecord typed && typed.Type == "operation_finished") finished.Add(typed.Id);
            if (record is WriteDeferredRecord deferred && string.IsNullOrWhiteSpace(deferred.EntryId))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidDeferredHandle, "Deferred write requires an entry id.");
        }
    }

    /// <summary>
    /// 校验包含 session entry 的完整恢复切片。
    /// </summary>
    /// <param name="input">包含 entries 的记录切片。</param>
    public static void ValidateRecordLog(RecordLogSlice input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRecordLog(new LaneRecordLogSlice(input.Lane, input.Records, input.OpenOperations));

        var entries = input.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var entry in input.Entries)
        {
            if (entry is MessageSessionEntry message &&
                message.Message is AssistantMessage assistant &&
                assistant.StopReason == StopReason.Deferred &&
                assistant.Deferred is null)
            {
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.InvalidDeferredHandle, $"Deferred assistant entry {entry.Id} does not carry a handle.");
            }
        }

        foreach (var tool in input.Records.OfType<ToolStartedRecord>())
        {
            if (!entries.TryGetValue(tool.AssistantEntryId, out var assistantEntry) ||
                assistantEntry is not MessageSessionEntry { Message: AssistantMessage assistantMessage })
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.ToolCallMismatch, $"Tool start {tool.Id} does not reference an assistant entry.");

            var toolCalls = assistantMessage.Content.OfType<ToolCallContent>().ToArray();
            if (tool.ToolIndex < 0 || tool.ToolIndex >= toolCalls.Length)
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.ToolCallMismatch, $"Tool start {tool.Id} has an invalid tool ordinal.");
            var call = toolCalls[tool.ToolIndex];
            if (!string.Equals(call.Id, tool.ToolCallId, StringComparison.Ordinal) ||
                !string.Equals(call.Name, tool.ToolName, StringComparison.Ordinal))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.ToolCallMismatch, $"Tool start {tool.Id} does not match its assistant tool-call ordinal.");

            if (entries.TryGetValue(tool.ResultEntryId, out var resultEntry) &&
                (resultEntry is not MessageSessionEntry { Message: ToolResultMessage resultMessage } ||
                 !string.Equals(resultMessage.ToolCallId, tool.ToolCallId, StringComparison.Ordinal) ||
                 !string.Equals(resultMessage.ToolName, tool.ToolName, StringComparison.Ordinal)))
                throw new RecordLogCorruptionException(RecordLogCorruptionReason.ProvisionedEntryMismatch, $"Tool result entry {tool.ResultEntryId} does not match tool invocation.");
        }
    }

    /// <summary>将参考记录切片恢复为可用于 UI/运行时的 lane 状态。</summary>
    /// <param name="input">记录切片。</param>
    /// <param name="leafId">当前分支叶节点。</param>
    /// <returns>恢复状态。</returns>
    public static LaneStateSnapshot ReduceLaneState(LaneRecordLogSlice input, string? leafId = null)
    {
        ValidateRecordLog(input);
        var operation = input.OpenOperations.FirstOrDefault();
        var attempts = input.Records.OfType<StepAttemptRecord>().Where(item => operation is null || item.RunId == operation.Id).Select(item => item.Attempt).DefaultIfEmpty(-1).Max() + 1;
        var steer = input.Records.OfType<QueueEnqueuedRecord>().Where(item => item.Queue.Equals("steer", StringComparison.OrdinalIgnoreCase)).Select(item => item.EntryId).ToArray();
        var follow = input.Records.OfType<QueueEnqueuedRecord>().Where(item => item.Queue.Equals("followUp", StringComparison.OrdinalIgnoreCase)).Select(item => item.EntryId).ToArray();
        var next = input.Records.OfType<QueueEnqueuedRecord>().Where(item => item.Queue.Equals("nextRun", StringComparison.OrdinalIgnoreCase)).Select(item => item.EntryId).ToArray();
        var aborting = operation is not null && input.Records.OfType<AbortRequestedRecord>().Any(item => item.RunId == operation.Id);
        return new LaneStateSnapshot(input.Lane, leafId, aborting, operation?.Id, operation?.OperationKind, attempts, steer, follow, next);
    }

    /// <summary>
    /// 从 bounded record-log、操作自有 entries 和配置 entries 恢复完整 lane 状态。
    /// </summary>
    /// <param name="input">完整 reduction 输入。</param>
    /// <returns>lane 状态、有效配置以及可恢复的终止错误。</returns>
    public static LaneReductionResult ReduceLaneState(LaneReductionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Slice);
        ValidateRecordLog(input.Slice);

        var records = input.Slice.Records.OrderBy(item => item.Sequence).ToArray();
        var entries = input.Slice.Entries
            .Concat(input.OwnEntries)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var cancelled = records.OfType<QueueCancelledRecord>()
            .Select(item => item.EntryId)
            .ToHashSet(StringComparer.Ordinal);
        var pendingQueue = records.OfType<QueueEnqueuedRecord>()
            .Where(item => !cancelled.Contains(item.EntryId) && !entries.ContainsKey(item.EntryId))
            .ToArray();
        var pendingNextRun = pendingQueue
            .Where(item => item.Queue.Equals("nextRun", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.EntryId)
            .ToArray();
        var started = input.Slice.OpenOperations.OrderByDescending(item => item.Sequence).FirstOrDefault();
        var effective = DeriveEffectiveConfiguration(input.ConfigurationEntries, input.DefaultConfiguration);
        if (started is null)
            return new LaneReductionResult(new LaneState(input.Slice.Lane, input.LeafId, null, pendingNextRun), effective, null);

        var operationRecords = records.Where(record => record switch
        {
            OperationStartedRecord operation => operation.Id.Equals(started.Id, StringComparison.Ordinal),
            OperationFinishedRecord finish => finish.RunId.Equals(started.Id, StringComparison.Ordinal),
            AbortRequestedRecord abort => abort.RunId.Equals(started.Id, StringComparison.Ordinal),
            StepAttemptRecord attempt => attempt.RunId.Equals(started.Id, StringComparison.Ordinal),
            ToolStartedRecord tool => tool.RunId.Equals(started.Id, StringComparison.Ordinal),
            QueueEnqueuedRecord queue => queue.RunId?.Equals(started.Id, StringComparison.Ordinal) == true,
            QueueCancelledRecord queue => queue.RunId?.Equals(started.Id, StringComparison.Ordinal) == true,
            WriteDeferredRecord deferred => deferred.RunId.Equals(started.Id, StringComparison.Ordinal),
            UsageRecord usage => usage.RunId?.Equals(started.Id, StringComparison.Ordinal) == true,
            _ => false
        }).ToArray();
        var aborting = operationRecords.OfType<AbortRequestedRecord>().Any();
        var pendingSteer = aborting
            ? []
            : pendingQueue.Where(item => item.RunId == started.Id && item.Queue.Equals("steer", StringComparison.OrdinalIgnoreCase)).Select(item => item.EntryId).ToArray();
        var pendingFollowUp = aborting
            ? []
            : pendingQueue.Where(item => item.RunId == started.Id && item.Queue.Equals("followUp", StringComparison.OrdinalIgnoreCase)).Select(item => item.EntryId).ToArray();
        var pendingWrites = operationRecords.OfType<WriteDeferredRecord>()
            .Where(item => !entries.ContainsKey(item.EntryId))
            .Select(item => item.EntryId)
            .ToArray();
        var attempts = operationRecords.OfType<StepAttemptRecord>()
            .Select(item => item.Attempt)
            .DefaultIfEmpty(0)
            .Max();
        var newestOwn = input.OwnEntries.LastOrDefault();
        var deferred = newestOwn is MessageSessionEntry { Message: AssistantMessage assistant } &&
                       assistant.StopReason == StopReason.Deferred
            ? assistant.Deferred
            : null;
        var consumedIds = operationRecords.OfType<QueueEnqueuedRecord>()
            .Where(item => !item.Queue.Equals("nextRun", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.EntryId)
            .ToHashSet(StringComparer.Ordinal);
        var overflowRecoveryUsed = operationRecords.OfType<StepAttemptRecord>()
            .Any(item => item.Step.Equals("compaction", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(item.CompactionReason, "overflow", StringComparison.OrdinalIgnoreCase) &&
                         consumedIds.Count > 0);
        var targetResultExists = started.OperationKind.Equals("compaction", StringComparison.OrdinalIgnoreCase) && newestOwn is not null;
        var targetSummaryExists = started.OperationKind.Equals("navigation", StringComparison.OrdinalIgnoreCase) && newestOwn is not null;
        var operationState = new LaneOperationState(
            started.Id,
            started.OperationKind,
            aborting,
            attempts,
            pendingSteer,
            pendingFollowUp,
            pendingWrites,
            [],
            deferred,
            overflowRecoveryUsed,
            newestOwn?.Id,
            targetResultExists,
            targetSummaryExists);
        TerminalFailureState? terminalFailure = null;
        if (newestOwn is MessageSessionEntry { Message: AssistantMessage { StopReason: StopReason.Error } errorMessage })
        {
            var producedByStep = operationRecords.OfType<StepAttemptRecord>().Any(item => item.ResultEntryId == newestOwn.Id);
            var producedByDeferred = operationRecords.OfType<UsageRecord>().Any(item => item.Cause.Equals("deferred_fetch", StringComparison.OrdinalIgnoreCase) && item.EntryId == newestOwn.Id);
            if (producedByStep || producedByDeferred)
                terminalFailure = new TerminalFailureState(newestOwn.Id, producedByStep ? "step" : "deferred_fetch", errorMessage);
        }

        return new LaneReductionResult(
            new LaneState(input.Slice.Lane, input.LeafId, operationState, pendingNextRun),
            effective,
            terminalFailure);
    }

    /// <summary>从配置 entries 计算当前生效的模型、思考级别和工具列表。</summary>
    /// <param name="entries">按旧到新排列的配置 entries。</param>
    /// <param name="defaults">默认配置。</param>
    /// <returns>合并后的配置。</returns>
    private static EffectiveLaneConfiguration DeriveEffectiveConfiguration(
        IReadOnlyList<SessionTreeEntry> entries,
        EffectiveLaneConfiguration defaults)
    {
        var provider = defaults.Provider;
        var modelId = defaults.ModelId;
        var thinking = defaults.ThinkingLevel;
        var tools = defaults.ActiveToolNames.ToArray();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ModelChangeSessionEntry model:
                    provider = model.Provider;
                    modelId = model.ModelId;
                    break;
                case ThinkingLevelChangeSessionEntry level:
                    thinking = level.ThinkingLevel;
                    break;
                case ActiveToolsChangeSessionEntry active:
                    tools = active.ActiveToolNames.ToArray();
                    break;
            }
        }
        return new EffectiveLaneConfiguration(provider, modelId, thinking, tools);
    }
}
