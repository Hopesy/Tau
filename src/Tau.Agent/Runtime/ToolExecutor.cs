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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var toolMap = tools.ToDictionary(t => t.Name);

        var hasSequential = toolCalls.Any(tc =>
            toolMap.TryGetValue(tc.Name, out var tool) &&
            tool.ExecutionMode == ToolExecutionMode.Sequential);

        if (hasSequential || defaultMode == ToolExecutionMode.Sequential)
        {
            await foreach (var evt in ExecuteSequentialAsync(toolCalls, toolMap, interceptors, conversationHistory, logSink, ct))
                yield return evt;
        }
        else
        {
            await foreach (var evt in ExecuteParallelAsync(toolCalls, toolMap, interceptors, conversationHistory, logSink, ct))
                yield return evt;
        }
    }

    private static async IAsyncEnumerable<AgentEvent> ExecuteSequentialAsync(
        IReadOnlyList<ToolCallContent> toolCalls,
        Dictionary<string, IAgentTool> toolMap,
        IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> conversationHistory,
        ITauLogSink logSink,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var tc in toolCalls)
        {
            await foreach (var evt in ExecuteSingleAsync(tc, toolMap, interceptors, conversationHistory, logSink, ct))
                yield return evt;
        }
    }

    private static async IAsyncEnumerable<AgentEvent> ExecuteParallelAsync(
        IReadOnlyList<ToolCallContent> toolCalls,
        Dictionary<string, IAgentTool> toolMap,
        IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> conversationHistory,
        ITauLogSink logSink,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var tasks = toolCalls.Select(tc =>
            CollectEventsAsync(ExecuteSingleAsync(tc, toolMap, interceptors, conversationHistory, logSink, ct))).ToList();

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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        logSink.Log(new TauLogEvent(
            "tool",
            "execution.start",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["toolCallId"] = tc.Id, ["toolName"] = tc.Name }));

        yield return new ToolExecutionStartEvent(tc.Id, tc.Name);

        if (!toolMap.TryGetValue(tc.Name, out var tool))
        {
            logSink.Log(new TauLogEvent(
                "tool",
                "execution.end",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?> { ["toolCallId"] = tc.Id, ["toolName"] = tc.Name, ["error"] = "not_found" }));
            yield return new ToolExecutionEndEvent(tc.Id, new ToolResult(
                [new TextContent($"Tool '{tc.Name}' not found.")], IsError: true));
            yield break;
        }

        var callContext = new ToolCallContext(tc.Id, tc.Name, ParseArgs(tc.Arguments), conversationHistory);

        foreach (var interceptor in interceptors)
        {
            var decision = await interceptor.BeforeToolCallAsync(callContext, ct).ConfigureAwait(false);
            if (decision.Blocked)
            {
                logSink.Log(new TauLogEvent(
                    "tool",
                    "execution.end",
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string?> { ["toolCallId"] = tc.Id, ["toolName"] = tc.Name, ["error"] = "blocked", ["reason"] = decision.Reason }));
                yield return new ToolExecutionEndEvent(tc.Id, new ToolResult(
                    [new TextContent($"Tool call blocked: {decision.Reason}")], IsError: true));
                yield break;
            }
        }

        var args = await tool.PrepareArgumentsAsync(callContext.Arguments, ct).ConfigureAwait(false);

        var result = await tool.ExecuteAsync(tc.Id, args, ct,
            update => Task.CompletedTask).ConfigureAwait(false);

        foreach (var interceptor in interceptors)
        {
            result = await interceptor.AfterToolCallAsync(callContext, result, ct).ConfigureAwait(false);
        }

        logSink.Log(new TauLogEvent(
            "tool",
            "execution.end",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["toolCallId"] = tc.Id,
                ["toolName"] = tc.Name,
                ["isError"] = result.IsError ? "true" : "false"
            }));

        yield return new ToolExecutionEndEvent(tc.Id, result);
    }

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
