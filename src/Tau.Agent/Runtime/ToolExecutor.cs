using System.Diagnostics;
using System.Text.Json;
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
        var tasks = toolCalls.Select(tc =>
            CollectEventsAsync(ExecuteSingleAsync(tc, toolMap, interceptors, conversationHistory, logSink, logContext, ct))).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var events in results)
        foreach (var evt in events)
            yield return evt;
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

        yield return new ToolExecutionStartEvent(tc.Id, tc.Name);

        if (!found)
        {
            var missingResult = new ToolResult([new TextContent($"Tool '{tc.Name}' not found.")], IsError: true);
            LogToolEnd(logSink, tc, startedAt, missingResult, "not-found", logContext);
            yield return new ToolExecutionEndEvent(tc.Id, missingResult);
            yield break;
        }

        ToolCallContext callContext;
        try
        {
            callContext = new ToolCallContext(tc.Id, tc.Name, ParseArgs(tc.Arguments), conversationHistory);
        }
        catch (JsonException)
        {
            LogToolEnd(logSink, tc, startedAt, result: null, "invalid-arguments", logContext);
            throw;
        }

        foreach (var interceptor in interceptors)
        {
            var decision = await interceptor.BeforeToolCallAsync(callContext, ct).ConfigureAwait(false);
            if (decision.Blocked)
            {
                var blockedResult = new ToolResult([new TextContent($"Tool call blocked: {decision.Reason}")], IsError: true);
                LogToolEnd(logSink, tc, startedAt, blockedResult, "blocked", logContext, decision.Reason);
                yield return new ToolExecutionEndEvent(tc.Id, blockedResult);
                yield break;
            }
        }

        ToolResult result;
        try
        {
            var args = await tool!.PrepareArgumentsAsync(callContext.Arguments, ct).ConfigureAwait(false);

            result = await tool.ExecuteAsync(tc.Id, args, ct,
                update => Task.CompletedTask).ConfigureAwait(false);

            foreach (var interceptor in interceptors)
            {
                result = await interceptor.AfterToolCallAsync(callContext, result, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogToolEnd(logSink, tc, startedAt, result: null, "cancelled", logContext);
            throw;
        }
        catch (Exception ex)
        {
            LogToolEnd(logSink, tc, startedAt, result: null, "exception", logContext, exceptionType: ex.GetType().Name);
            throw;
        }

        LogToolEnd(logSink, tc, startedAt, result, result.IsError ? "tool-result-error" : "none", logContext);

        yield return new ToolExecutionEndEvent(tc.Id, result);
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

    private static async Task<List<AgentEvent>> CollectEventsAsync(IAsyncEnumerable<AgentEvent> source)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in source)
            events.Add(evt);
        return events;
    }
}
