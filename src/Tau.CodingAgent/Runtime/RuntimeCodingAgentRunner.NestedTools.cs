// 作者：xxx
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private readonly object _nestedToolGate = new();
    private readonly Dictionary<string, NestedToolScope> _nestedToolScopes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _nestedToolQueue = new(1, 1);
    private ChannelWriter<AgentEvent>? _nestedToolEvents;

    /// <summary>【CodingAgent】【嵌套调用】分配父子标识、记录原始参数，并通过相同工具流水线执行。</summary>
    /// <param name="callerId">发起调用的父工具标识。</param>
    /// <param name="name">目标工具名称。</param>
    /// <param name="args">原始参数。</param>
    /// <param name="token">调用取消信号。</param>
    /// <param name="onUpdate">扩展调用方的实时更新回调。</param>
    /// <param name="onStarted">调用已登记并进入顺序队列后的通知。</param>
    /// <returns>失败不抛异常的最终工具结果。</returns>
    internal async Task<AgentToolCallOutcome> ExecuteNestedToolAsync(string callerId, string name, JsonElement args,
        CancellationToken token, Func<ToolUpdate, Task>? onUpdate = null, Action? onStarted = null)
    {
        NestedToolScope scope;
        ToolCallContent call;
        int recordIndex;
        var started = Stopwatch.GetTimestamp();
        lock (_nestedToolGate)
        {
            // 1. 【CodingAgent】【嵌套调用】所有后代共享根调用记录，兄弟使用各自父节点的递增序号
            if (!_nestedToolScopes.TryGetValue(callerId, out scope!))
                _nestedToolScopes[callerId] = scope = new(new NestedToolRecorder(), false);
            call = new(callerId + "/" + scope.NextId++, name, args.GetRawText());
            recordIndex = scope.Recorder.Start(call, args);
        }
        await EmitNestedToolEventAsync(new ToolExecutionStartEvent(call.Id, name, call.Arguments) { ParentToolCallId = callerId }).ConfigureAwait(false);
        var tools = GetCallableTools();
        var exclusive = !scope.HoldsQueue && (_config.DefaultExecutionMode == ToolExecutionMode.Sequential ||
            tools.FirstOrDefault(tool => tool.Name == name)?.ExecutionMode == ToolExecutionMode.Sequential);
        var acquired = false;
        AgentToolCallOutcome outcome;
        try
        {
            // 2. 【CodingAgent】【顺序嵌套】持有队列的调用允许自己的后代重入，避免递归互相等待
            var queued = exclusive ? _nestedToolQueue.WaitAsync(token) : Task.CompletedTask;
            onStarted?.Invoke();
            if (exclusive) { await queued.ConfigureAwait(false); acquired = true; }
            lock (_nestedToolGate) _nestedToolScopes[call.Id] = new(scope.Recorder, scope.HoldsQueue || exclusive);
            if (!Messages.OfType<AssistantMessage>().Any())
                outcome = new(call, new ToolResult([new TextContent("No assistant message issued this call")], true), true);
            else
                outcome = await AgentToolCalls.RunAsync(call, tools, _config.Interceptors, Messages, token,
                    async update =>
                    {
                        if (onUpdate is not null) await onUpdate(update).ConfigureAwait(false);
                        await EmitNestedToolEventAsync(new ToolExecutionUpdateEvent(call.Id, update, name, call.Arguments,
                            update.ToPartialResult()) { ParentToolCallId = callerId }).ConfigureAwait(false);
                    }, callerId, _logSink, CreateRunLogContext()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException ? "Tool execution cancelled." : ex.Message;
            outcome = new(call, new ToolResult([new TextContent(error)], true), true);
        }
        finally
        {
            lock (_nestedToolGate) _nestedToolScopes.Remove(call.Id);
            if (acquired) _nestedToolQueue.Release();
        }
        lock (_nestedToolGate) scope.Recorder.Finish(recordIndex, started, outcome.Result);
        await EmitNestedToolEventAsync(new ToolExecutionEndEvent(call.Id, outcome.Result, name, outcome.IsError)
            { ParentToolCallId = callerId }).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>【CodingAgent】【嵌套事件】等待扩展订阅后把事件合入当前运行的流，独立调用则发送后台事件。</summary>
    /// <param name="evt">带父调用标识的事件。</param>
    /// <returns>订阅完成且事件已提交的任务。</returns>
    private async Task EmitNestedToolEventAsync(AgentEvent evt)
    {
        await PublishLifecycleEventAsync(evt, CancellationToken.None).ConfigureAwait(false);
        if (_nestedToolEvents is { } writer) writer.TryWrite(evt);
        else await PublishBackgroundEventAsync(evt, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【嵌套事件】父工具等待期间持续交付子工具进度，保持普通生命周期的拉取顺序。</summary>
    /// <param name="source">核心 Agent 事件流。</param>
    /// <param name="token">运行取消信号。</param>
    /// <returns>包含嵌套工具事件的完整事件流。</returns>
    private async IAsyncEnumerable<AgentEvent> MergeNestedToolEventsAsync(IAsyncEnumerable<AgentEvent> source,
        [EnumeratorCancellation] CancellationToken token)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var iterator = source.GetAsyncEnumerator(lifetime.Token);
        _nestedToolEvents = channel.Writer;
        Task<bool>? next = null;
        Task<bool>? available = null;
        try
        {
            while (true)
            {
                next = iterator.MoveNextAsync().AsTask();
                while (!next.IsCompleted)
                {
                    available ??= channel.Reader.WaitToReadAsync().AsTask();
                    await Task.WhenAny(next, available).ConfigureAwait(false);
                    while (channel.Reader.TryRead(out var nested)) yield return nested;
                    if (available.IsCompleted) available = null;
                }
                var hasNext = await next.ConfigureAwait(false);
                next = null;
                while (channel.Reader.TryRead(out var nested)) yield return nested;
                if (!hasNext) break;
                yield return iterator.Current;
            }
        }
        finally
        {
            _nestedToolEvents = null;
            channel.Writer.TryComplete();
            if (next is not null)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                try { await next.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>【CodingAgent】【嵌套落盘】把有界记录和累加用量附加到父工具消息，不产生独立子消息。</summary>
    /// <param name="message">即将存入历史的消息。</param>
    /// <returns>保留嵌套记录的父工具结果或原消息。</returns>
    private ChatMessage AttachNestedToolRecord(ChatMessage message)
    {
        if (message is not ToolResultMessage result) return message;
        lock (_nestedToolGate)
        {
            if (!_nestedToolScopes.Remove(result.ToolCallId, out var scope)) return message;
            return result with { NestedCalls = scope.Recorder.Snapshot(), Usage = CombineToolUsage(result.Usage, scope.Recorder.Usage) };
        }
    }

    /// <summary>【CodingAgent】【工具用量】汇总计费维度，未提供用量时保留空值。</summary>
    /// <param name="left">已有用量。</param>
    /// <param name="right">新增用量。</param>
    /// <returns>合并后的工具用量。</returns>
    internal static Usage? CombineToolUsage(Usage? left, Usage? right)
    {
        if (left is not { } a) return right;
        if (right is not { } b) return left;
        return CodingAgentUsageAccounting.CombineUsage(a, b);
    }

    /// <summary>【CodingAgent】【调用作用域】保存每个父调用的序号及顺序队列占有状态。</summary>
    private sealed class NestedToolScope(NestedToolRecorder recorder, bool holdsQueue)
    {
        public NestedToolRecorder Recorder { get; } = recorder;
        public bool HoldsQueue { get; } = holdsQueue;
        public int NextId { get; set; } = 1;
    }

    /// <summary>【CodingAgent】【调用记录】限制记录数量和参数字节数，结果正文不会进入记录。</summary>
    private sealed class NestedToolRecorder
    {
        private readonly List<NestedToolCallRecord> _calls = [];
        private bool _complete = true;
        private int _argumentBytes;
        public Usage? Usage { get; private set; }

        /// <summary>【CodingAgent】【调用记录】记录开始顺序并裁剪超限参数。</summary>
        /// <param name="call">原始调用。</param>
        /// <param name="arguments">原始参数。</param>
        /// <returns>记录索引，数量超限时为负值。</returns>
        public int Start(ToolCallContent call, JsonElement arguments)
        {
            if (_calls.Count >= 256) { _complete = false; return -1; }
            var bytes = Encoding.UTF8.GetByteCount(call.Arguments);
            var omit = bytes > 8 * 1024 || _argumentBytes + bytes > 32 * 1024;
            if (omit) _complete = false;
            else _argumentBytes += bytes;
            _calls.Add(new() { Id = call.Id, Name = call.Name, Status = "unfinished",
                Arguments = omit ? null : arguments.Clone(), ArgumentsBytes = omit ? bytes : null });
            return _calls.Count - 1;
        }

        /// <summary>【CodingAgent】【调用记录】完成调用并计入所有子调用用量，包括超限未记录的调用。</summary>
        /// <param name="index">记录索引。</param>
        /// <param name="started">开始时间戳。</param>
        /// <param name="result">最终结果。</param>
        public void Finish(int index, long started, ToolResult result)
        {
            Usage = CombineToolUsage(Usage, result.Usage);
            if (index < 0) return;
            var error = result.IsError ? string.Join("\n", result.Content.OfType<TextContent>().Select(block => block.Text)) : null;
            _calls[index] = _calls[index] with { Status = result.IsError ? "error" : "ok",
                DurationMs = (long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds),
                Error = string.IsNullOrEmpty(error) ? null : error[..Math.Min(500, error.Length)] };
        }

        /// <summary>【CodingAgent】【调用记录】冻结父工具结束时的状态，尚未结束的子调用使记录不完整。</summary>
        /// <returns>不可变的有界调用快照。</returns>
        public NestedToolCalls Snapshot() => new(_calls.ToArray(), _complete && _calls.All(call => call.Status != "unfinished"));
    }
}
