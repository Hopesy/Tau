using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.Agent.Runtime;

/// <summary>
/// Executes tool calls sequentially or in parallel.
/// Mirrors pi-mono's tool execution in agent-loop.ts.
/// </summary>
internal static class ToolExecutor
{
    public static async IAsyncEnumerable<AgentEvent> ExecuteToolCallsAsync(
        IReadOnlyList<ToolCallContent> toolCalls,
        IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> conversationHistory,
        ToolExecutionMode defaultMode,
        ITauLogSink logSink,
        TauRuntimeLogContext? logContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
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
            await foreach (var evt in ExecuteSingleAsync(tc, toolMap, interceptors, conversationHistory, logSink, logContext, ct))
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
                ExecutePreparedParallelToolCallAsync(prepared, updateChannel.Writer, logSink, logContext, ct)))
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
        CancellationToken ct)
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
            callContext = new ToolCallContext(tc.Id, tc.Name, args, conversationHistory);
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
                terminalResult = CreateErrorToolResult(blockedMessage);
                terminalFailureKind = "blocked";
                terminalReason = decision.Reason;
                break;
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

    private static async Task<ParallelExecutedToolCall> ExecutePreparedParallelToolCallAsync(
        ParallelPreparedToolCall prepared,
        ChannelWriter<AgentEvent> updateWriter,
        ITauLogSink logSink,
        TauRuntimeLogContext? logContext,
        CancellationToken ct)
    {
        ToolResult result;
        var failureKind = "none";
        string? exceptionType = null;

        try
        {
            result = await prepared.Tool.ExecuteAsync(prepared.ToolCall.Id, prepared.Args, ct,
                update =>
                {
                    var updateEvent = new ToolExecutionUpdateEvent(
                        prepared.ToolCall.Id,
                        update,
                        prepared.ToolCall.Name,
                        prepared.Args.GetRawText(),
                        update.ToPartialResult());
                    return updateWriter.WriteAsync(updateEvent, ct).AsTask();
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

    private static async IAsyncEnumerable<AgentEvent> ExecuteSingleAsync(
        ToolCallContent tc,
        Dictionary<string, IAgentTool> toolMap,
        IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> conversationHistory,
        ITauLogSink logSink,
        TauRuntimeLogContext? logContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var found = toolMap.TryGetValue(tc.Name, out var tool);
        LogToolStart(logSink, tc, found ? tool!.ExecutionMode : null, logContext);

        yield return new ToolExecutionStartEvent(tc.Id, tc.Name, tc.Arguments);

        if (!found)
        {
            var missingResult = new ToolResult([new TextContent($"Tool '{tc.Name}' not found.")], IsError: true);
            LogToolEnd(logSink, tc, startedAt, missingResult, "not-found", logContext);
            yield return new ToolExecutionEndEvent(tc.Id, missingResult, tc.Name);
            yield break;
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
            args = await tool!.PrepareArgumentsAsync(rawArgs, ct).ConfigureAwait(false);
            args = ToolArgumentValidator.ValidateToolArguments(ToAiTool(tool), tc, args);
            callContext = new ToolCallContext(tc.Id, tc.Name, args, conversationHistory);
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
            yield return new ToolExecutionEndEvent(tc.Id, terminalResult, tc.Name);
            yield break;
        }

        var preparedArgs = args;
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
                terminalResult = CreateErrorToolResult(blockedMessage);
                terminalFailureKind = "blocked";
                terminalReason = decision.Reason;
                break;
            }
        }

        if (terminalResult is not null)
        {
            LogToolEnd(logSink, tc, startedAt, terminalResult, terminalFailureKind!, logContext, terminalReason, terminalExceptionType);
            yield return new ToolExecutionEndEvent(tc.Id, terminalResult, tc.Name);
            yield break;
        }

        var updateEvents = new List<ToolExecutionUpdateEvent>();
        ToolResult result;
        var failureKind = "none";
        string? exceptionType = null;
        try
        {
            result = await tool!.ExecuteAsync(tc.Id, preparedArgs, ct,
                update =>
                {
                    updateEvents.Add(new ToolExecutionUpdateEvent(
                        tc.Id,
                        update,
                        tc.Name,
                        preparedArgs.GetRawText(),
                        update.ToPartialResult()));
                    return Task.CompletedTask;
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

        foreach (var updateEvent in updateEvents)
        {
            yield return updateEvent;
        }

        foreach (var interceptor in interceptors)
        {
            try
            {
                result = await interceptor.AfterToolCallAsync(effectiveCallContext, result, ct).ConfigureAwait(false);
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

        LogToolEnd(logSink, tc, startedAt, result, failureKind, logContext, exceptionType: exceptionType);

        yield return new ToolExecutionEndEvent(tc.Id, result, tc.Name);
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
