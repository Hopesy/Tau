using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.AgentCore.Runtime;

/// <summary>
/// Executes tool calls sequentially or in parallel.
/// Mirrors pi-main's tool execution in agent-loop.ts.
/// </summary>
internal static partial class ToolExecutor
{
    /// <summary>【AgentCore】【工具执行】验证并执行工具批次，截断响应仅产生失败结果</summary>
    /// <param name="toolCalls">模型返回的工具调用</param>
    /// <param name="tools">可用工具实现</param>
    /// <param name="interceptors">正常调用使用的拦截器</param>
    /// <param name="conversationHistory">当前对话历史</param>
    /// <param name="defaultMode">默认执行模式</param>
    /// <param name="logSink">日志接收器</param>
    /// <param name="logContext">日志关联信息</param>
    /// <param name="ct">取消信号</param>
    /// <param name="responseTruncated">响应是否因输出长度限制而截断</param>
    /// <returns>按生命周期顺序产生的工具事件</returns>
    public static async IAsyncEnumerable<AgentEvent> ExecuteToolCallsAsync(
        IReadOnlyList<ToolCallContent> toolCalls,
        IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> conversationHistory,
        ToolExecutionMode defaultMode,
        ITauLogSink logSink,
        TauRuntimeLogContext? logContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        bool responseTruncated = false)
    {
        // 1. 【AgentCore】【截断响应】在参数解析、准备和拦截器运行前拒绝整批调用
        if (responseTruncated)
        {
            foreach (var call in toolCalls)
            {
                var startedAt = Stopwatch.GetTimestamp();
                LogToolStart(logSink, call, defaultMode, logContext);
                yield return new ToolExecutionStartEvent(call.Id, call.Name, call.Arguments);
                var result = new ToolResult([new TextContent(
                    $"Tool call \"{call.Name}\" was not executed: the response hit the output token limit, so its arguments may be truncated. Re-issue the tool call with complete arguments.")], IsError: true);
                LogToolEnd(logSink, call, startedAt, result, "response-truncated", logContext);
                yield return new ToolExecutionEndEvent(call.Id, result, call.Name);
            }

            yield break;
        }

        var toolMap = tools.ToDictionary(t => t.Name);

        var hasSequential = toolCalls.Any(tc =>
            toolMap.TryGetValue(tc.Name, out var tool) &&
            tool.ExecutionMode == ToolExecutionMode.Sequential);

        if (hasSequential || defaultMode == ToolExecutionMode.Sequential)
        {
            await foreach (var evt in ExecuteSequentialAsync(toolCalls, toolMap, interceptors, conversationHistory, logSink, logContext, ct))
                yield return evt;
        }
        else
        {
            await foreach (var evt in ExecuteParallelAsync(toolCalls, toolMap, interceptors, conversationHistory, logSink, logContext, ct))
                yield return evt;
        }
    }

    /// <summary>【AgentCore】【顺序工具】逐个运行工具，每个调用仍通过异步通道即时输出进度。</summary>
    /// <param name="toolCalls">待执行的工具调用。</param>
    /// <param name="toolMap">可用工具映射。</param>
    /// <param name="interceptors">调用前后拦截器。</param>
    /// <param name="conversationHistory">当前会话消息。</param>
    /// <param name="logSink">日志接收器。</param>
    /// <param name="logContext">日志上下文。</param>
    /// <param name="ct">取消信号。</param>
    /// <returns>当前工具的开始、实时进度及结束事件。</returns>
    private static async IAsyncEnumerable<AgentEvent> ExecuteSequentialAsync(
        IReadOnlyList<ToolCallContent> toolCalls,
        Dictionary<string, IAgentTool> toolMap,
        IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> conversationHistory,
        ITauLogSink logSink,
        TauRuntimeLogContext? logContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var tc in toolCalls)
        {
            await foreach (var evt in ExecuteParallelAsync([tc], toolMap, interceptors, conversationHistory, logSink, logContext, ct))
                yield return evt;
        }
    }

    private static async IAsyncEnumerable<AgentEvent> ExecuteParallelAsync(
        IReadOnlyList<ToolCallContent> toolCalls,
        Dictionary<string, IAgentTool> toolMap,
        IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> conversationHistory,
        ITauLogSink logSink,
        TauRuntimeLogContext? logContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var preparedCalls = new List<ParallelPreparedToolCall>();

        foreach (var tc in toolCalls)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var found = toolMap.TryGetValue(tc.Name, out var tool);
            LogToolStart(logSink, tc, found ? tool!.ExecutionMode : null, logContext);

            yield return new ToolExecutionStartEvent(tc.Id, tc.Name, tc.Arguments);

            var preparation = await PrepareParallelToolCallAsync(
                tc, tool, found, startedAt, interceptors, conversationHistory, logSink, logContext, ct)
                .ConfigureAwait(false);

            if (preparation.ImmediateEnd is not null)
            {
                yield return preparation.ImmediateEnd;
            }
            else if (preparation.Prepared is not null)
            {
                preparedCalls.Add(preparation.Prepared);
            }
        }

        if (preparedCalls.Count == 0)
        {
            yield break;
        }

        var updateChannel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var runningCalls = preparedCalls
            .Select(prepared => new ParallelRunningToolCall(
                prepared,
                ExecutePreparedParallelToolCallAsync(prepared, update => updateChannel.Writer.WriteAsync(
                    new ToolExecutionUpdateEvent(prepared.ToolCall.Id, update, prepared.ToolCall.Name,
                        prepared.Args.GetRawText(), update.ToPartialResult()), ct).AsTask(), ct)))
            .ToList();

        foreach (var running in runningCalls)
        {
            while (!running.Execution.IsCompleted)
            {
                var waitForUpdate = updateChannel.Reader.WaitToReadAsync().AsTask();
                var completed = await Task.WhenAny(running.Execution, waitForUpdate).ConfigureAwait(false);
                if (completed == waitForUpdate && await waitForUpdate.ConfigureAwait(false))
                {
                    while (updateChannel.Reader.TryRead(out var updateEvent))
                    {
                        yield return updateEvent;
                    }
                }
            }

            while (updateChannel.Reader.TryRead(out var updateEvent))
            {
                yield return updateEvent;
            }

            var executed = await running.Execution.ConfigureAwait(false);
            yield return await FinalizeParallelToolCallAsync(
                running.Prepared, executed, interceptors, logSink, logContext, ct)
                .ConfigureAwait(false);
        }

        while (updateChannel.Reader.TryRead(out var updateEvent))
        {
            yield return updateEvent;
        }
    }

    private static async Task<ParallelToolPreparation> PrepareParallelToolCallAsync(
        ToolCallContent tc,
        IAgentTool? tool,
        bool found,
        long startedAt,
        IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> conversationHistory,
        ITauLogSink logSink,
        TauRuntimeLogContext? logContext,
        CancellationToken ct,
        string? parentToolCallId = null)
    {
        if (!found || tool is null)
        {
            var missingResult = new ToolResult([new TextContent($"Tool '{tc.Name}' not found.")], IsError: true);
            LogToolEnd(logSink, tc, startedAt, missingResult, "not-found", logContext);
            return new ParallelToolPreparation(
                Prepared: null,
                ImmediateEnd: new ToolExecutionEndEvent(tc.Id, missingResult, tc.Name));
        }

        JsonElement args = default;
        ToolCallContext? callContext = null;
        ToolResult? terminalResult = null;
        string? terminalFailureKind = null;
        string? terminalExceptionType = null;
        string? terminalReason = null;

        try
        {
            var rawArgs = ParseArgs(tc.Arguments);
            args = await tool.PrepareArgumentsAsync(rawArgs, ct).ConfigureAwait(false);
            args = ToolArgumentValidator.ValidateToolArguments(ToAiTool(tool), tc, args);
            callContext = new ToolCallContext(tc.Id, tc.Name, args, conversationHistory) { ParentToolCallId = parentToolCallId };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            terminalResult = CreateCancelledToolResult();
            terminalFailureKind = "cancelled";
            terminalExceptionType = nameof(OperationCanceledException);
        }
        catch (JsonException ex)
        {
            terminalResult = CreateErrorToolResult($"Invalid arguments for tool \"{tc.Name}\": {ex.Message}");
            terminalFailureKind = "invalid-arguments";
            terminalExceptionType = ex.GetType().Name;
        }
        catch (ToolArgumentValidationException ex)
        {
            terminalResult = CreateErrorToolResult(ex.Message);
            terminalFailureKind = "invalid-arguments";
            terminalExceptionType = ex.GetType().Name;
        }
        catch (Exception ex)
        {
            terminalResult = CreateErrorToolResult(ex.Message);
            terminalFailureKind = "prepare-exception";
            terminalExceptionType = ex.GetType().Name;
        }

        if (terminalResult is not null)
        {
            LogToolEnd(logSink, tc, startedAt, terminalResult, terminalFailureKind!, logContext, terminalReason, terminalExceptionType);
            return new ParallelToolPreparation(
                Prepared: null,
                ImmediateEnd: new ToolExecutionEndEvent(tc.Id, terminalResult, tc.Name));
        }

        var effectiveCallContext = callContext ?? throw new InvalidOperationException("Tool call context was not prepared.");

        foreach (var interceptor in interceptors)
        {
            ToolCallDecision decision;
            try
            {
                decision = await interceptor.BeforeToolCallAsync(effectiveCallContext, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                terminalResult = CreateCancelledToolResult();
                terminalFailureKind = "cancelled";
                terminalExceptionType = nameof(OperationCanceledException);
                break;
            }
            catch (Exception ex)
            {
                terminalResult = CreateErrorToolResult(ex.Message);
                terminalFailureKind = "before-exception";
                terminalExceptionType = ex.GetType().Name;
                break;
            }

            if (decision.Blocked)
            {
                var blockedMessage = string.IsNullOrWhiteSpace(decision.Reason)
                    ? "Tool call blocked."
                    : $"Tool call blocked: {decision.Reason}";
                terminalResult = CreateErrorToolResult(blockedMessage) with { Terminate = decision.Terminate };
                terminalFailureKind = "blocked";
                terminalReason = decision.Reason;
                break;
            }

            if (decision.Arguments.HasValue)
            {
                args = decision.Arguments.Value.Clone();
                effectiveCallContext = effectiveCallContext with { Arguments = args };
            }
        }

        if (terminalResult is not null)
        {
            LogToolEnd(logSink, tc, startedAt, terminalResult, terminalFailureKind!, logContext, terminalReason, terminalExceptionType);
            return new ParallelToolPreparation(
                Prepared: null,
                ImmediateEnd: new ToolExecutionEndEvent(tc.Id, terminalResult, tc.Name));
        }

        return new ParallelToolPreparation(
            new ParallelPreparedToolCall(tc, tool, args, effectiveCallContext, startedAt),
            ImmediateEnd: null);
    }

    /// <summary>【AgentCore】【工具执行】执行已准备调用，排空有效进度并忽略工具返回后的延迟更新。</summary>
    /// <param name="prepared">已校验调用。</param>
    /// <param name="onUpdate">中间结果接收器。</param>
    /// <param name="ct">取消信号。</param>
    /// <returns>归一化的执行结果及错误分类。</returns>
    private static async Task<ParallelExecutedToolCall> ExecutePreparedParallelToolCallAsync(
        ParallelPreparedToolCall prepared,
        Func<ToolUpdate, Task> onUpdate,
        CancellationToken ct)
    {
        ToolResult result;
        var failureKind = "none";
        string? exceptionType = null;
        var updateGate = new object();
        var updates = new List<Task>();
        var acceptingUpdates = true;

        try
        {
            result = await prepared.Tool.ExecuteAsync(prepared.ToolCall.Id, prepared.Args, ct,
                update =>
                {
                    lock (updateGate)
                    {
                        if (!acceptingUpdates) return Task.CompletedTask;
                        var pending = onUpdate(update);
                        updates.Add(pending);
                        return pending;
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = CreateCancelledToolResult();
            failureKind = "cancelled";
            exceptionType = nameof(OperationCanceledException);
        }
        catch (Exception ex)
        {
            result = CreateErrorToolResult(ex.Message);
            failureKind = "exception";
            exceptionType = ex.GetType().Name;
        }

        // 1. 【AgentCore】【进度边界】先关闭接收窗口，再等待工具返回前已经提交的异步进度
        Task[] pendingUpdates;
        lock (updateGate) { acceptingUpdates = false; pendingUpdates = updates.ToArray(); }
        try { await Task.WhenAll(pendingUpdates).ConfigureAwait(false); }
        catch (Exception ex)
        {
            result = ex is OperationCanceledException && ct.IsCancellationRequested ? CreateCancelledToolResult() : CreateErrorToolResult(ex.Message);
            failureKind = ex is OperationCanceledException ? "cancelled" : "exception";
            exceptionType = ex.GetType().Name;
        }

        return new ParallelExecutedToolCall(result, failureKind, exceptionType);
    }

    private static async Task<ToolExecutionEndEvent> FinalizeParallelToolCallAsync(
        ParallelPreparedToolCall prepared,
        ParallelExecutedToolCall executed,
        IReadOnlyList<IToolInterceptor> interceptors,
        ITauLogSink logSink,
        TauRuntimeLogContext? logContext,
        CancellationToken ct)
    {
        var result = executed.Result;
        var failureKind = executed.FailureKind;
        var exceptionType = executed.ExceptionType;

        foreach (var interceptor in interceptors)
        {
            try
            {
                result = await interceptor.AfterToolCallAsync(prepared.Context, result, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = CreateCancelledToolResult();
                failureKind = "cancelled";
                exceptionType = nameof(OperationCanceledException);
                break;
            }
            catch (Exception ex)
            {
                result = CreateErrorToolResult(ex.Message);
                failureKind = "after-exception";
                exceptionType = ex.GetType().Name;
                break;
            }
        }

        if (failureKind == "none" && result.IsError)
        {
            failureKind = "tool-result-error";
        }

        LogToolEnd(logSink, prepared.ToolCall, prepared.StartedAt, result, failureKind, logContext, exceptionType: exceptionType);

        return new ToolExecutionEndEvent(prepared.ToolCall.Id, result, prepared.ToolCall.Name);
    }

    private static void LogToolStart(
        ITauLogSink logSink,
        ToolCallContent toolCall,
        ToolExecutionMode? executionMode,
        TauRuntimeLogContext? logContext)
    {
        var fields = new Dictionary<string, string?>
        {
            ["toolCallId"] = toolCall.Id,
            ["toolName"] = toolCall.Name,
            ["argumentBytes"] = System.Text.Encoding.UTF8.GetByteCount(toolCall.Arguments).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        fields["logMessage"] = "【AgentCore】【ToolExecution】开始执行工具";
        if (executionMode is not null)
        {
            fields["executionMode"] = executionMode.Value.ToString().ToLowerInvariant();
        }

        logContext?.AddTo(fields);
        logSink.Log(new TauLogEvent("tool", "execution.start", DateTimeOffset.UtcNow, fields));
    }

    private static void LogToolEnd(
        ITauLogSink logSink,
        ToolCallContent toolCall,
        long startedAt,
        ToolResult? result,
        string failureKind,
        TauRuntimeLogContext? logContext,
        string? reason = null,
        string? exceptionType = null)
    {
        var fields = new Dictionary<string, string?>
        {
            ["toolCallId"] = toolCall.Id,
            ["toolName"] = toolCall.Name,
            ["success"] = result is not null && !result.IsError ? "true" : "false",
            ["isError"] = result?.IsError == true ? "true" : "false",
            ["failureKind"] = failureKind,
            ["durationMs"] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
        };
        fields["logMessage"] = "【AgentCore】【ToolExecution】工具执行结束";

        if (result is not null)
        {
            fields["contentBlockCount"] = result.Content.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["textBytes"] = CountTextBytes(result.Content).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (result.Details is not null)
            {
                fields["detailType"] = result.Details.GetType().Name;
            }
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            fields["reason"] = reason;
        }

        if (!string.IsNullOrWhiteSpace(exceptionType))
        {
            fields["exceptionType"] = exceptionType;
        }

        logContext?.AddTo(fields);
        logSink.Log(new TauLogEvent("tool", "execution.end", DateTimeOffset.UtcNow, fields));
    }

    private static int CountTextBytes(IReadOnlyList<ContentBlock> content) =>
        content.OfType<TextContent>().Sum(block => System.Text.Encoding.UTF8.GetByteCount(block.Text));

    private static JsonElement ParseArgs(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return JsonDocument.Parse("{}").RootElement.Clone();
        return JsonDocument.Parse(arguments).RootElement.Clone();
    }

    private static Tool ToAiTool(IAgentTool tool) =>
        new(tool.Name, tool.Description, tool.ParameterSchema);

    private static ToolResult CreateErrorToolResult(string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "Tool execution failed." : message;
        return new ToolResult([new TextContent(text)], IsError: true);
    }

    private static ToolResult CreateCancelledToolResult() =>
        CreateErrorToolResult("Operation canceled.");

    private sealed record ParallelToolPreparation(
        ParallelPreparedToolCall? Prepared,
        ToolExecutionEndEvent? ImmediateEnd);

    private sealed record ParallelPreparedToolCall(
        ToolCallContent ToolCall,
        IAgentTool Tool,
        JsonElement Args,
        ToolCallContext Context,
        long StartedAt);

    private sealed record ParallelExecutedToolCall(
        ToolResult Result,
        string FailureKind,
        string? ExceptionType);

    private sealed record ParallelRunningToolCall(
        ParallelPreparedToolCall Prepared,
        Task<ParallelExecutedToolCall> Execution);
}
