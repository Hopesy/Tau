// 作者：xxx
using System.Diagnostics;
using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.AgentCore.Runtime;

/// <summary>【AgentCore】【单工具执行】经过校验、执行及拦截器后的最终结果。</summary>
/// <param name="ToolCall">原始工具调用。</param>
/// <param name="Result">最终内容及工具元数据。</param>
/// <param name="IsError">是否执行失败或被阻止。</param>
public sealed record AgentToolCallOutcome(ToolCallContent ToolCall, ToolResult Result, bool IsError);

/// <summary>【AgentCore】【单工具执行】复用模型调用流水线，不写入历史或发布生命周期事件。</summary>
public static class AgentToolCalls
{
    /// <summary>【AgentCore】【单工具执行】依次准备参数、校验、拦截、执行并转换工具结果，工具失败返回错误结果。</summary>
    /// <param name="toolCall">需要执行的调用。</param>
    /// <param name="tools">本次允许访问的工具。</param>
    /// <param name="interceptors">与模型直接调用相同的拦截器。</param>
    /// <param name="history">供拦截器读取的会话历史。</param>
    /// <param name="token">调用取消信号。</param>
    /// <param name="onUpdate">实时进度回调。</param>
    /// <param name="parentToolCallId">可选直接父工具标识。</param>
    /// <param name="logSink">可选日志接收器。</param>
    /// <param name="logContext">可选日志关联上下文。</param>
    /// <returns>工具失败也正常完成的结果任务。</returns>
    public static Task<AgentToolCallOutcome> RunAsync(ToolCallContent toolCall, IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<IToolInterceptor> interceptors, IReadOnlyList<ChatMessage> history,
        CancellationToken token = default, Func<ToolUpdate, Task>? onUpdate = null, string? parentToolCallId = null,
        ITauLogSink? logSink = null, TauRuntimeLogContext? logContext = null) =>
        ToolExecutor.ExecuteToolCallAsync(toolCall, tools, interceptors, history, token, onUpdate,
            parentToolCallId, logSink ?? NullTauLogSink.Instance, logContext);
}

internal static partial class ToolExecutor
{
    /// <summary>【AgentCore】【单工具执行】与批次执行共享三个阶段，避免嵌套调用绕过检查。</summary>
    /// <param name="call">原始调用。</param>
    /// <param name="tools">可用工具。</param>
    /// <param name="interceptors">调用拦截器。</param>
    /// <param name="history">会话历史。</param>
    /// <param name="token">取消信号。</param>
    /// <param name="onUpdate">实时更新回调。</param>
    /// <param name="parentToolCallId">父调用标识。</param>
    /// <param name="logSink">日志接收器。</param>
    /// <param name="logContext">日志上下文。</param>
    /// <returns>最终工具调用结果。</returns>
    internal static async Task<AgentToolCallOutcome> ExecuteToolCallAsync(ToolCallContent call,
        IReadOnlyList<IAgentTool> tools, IReadOnlyList<IToolInterceptor> interceptors,
        IReadOnlyList<ChatMessage> history, CancellationToken token, Func<ToolUpdate, Task>? onUpdate,
        string? parentToolCallId, ITauLogSink logSink, TauRuntimeLogContext? logContext)
    {
        // 1. 【AgentCore】【单工具执行】校验和前置拦截可以直接产生失败结果
        var tool = tools.FirstOrDefault(candidate => candidate.Name == call.Name);
        var started = Stopwatch.GetTimestamp();
        LogToolStart(logSink, call, tool?.ExecutionMode, logContext);
        var preparation = await PrepareParallelToolCallAsync(call, tool, tool is not null, started,
            interceptors, history, logSink, logContext, token, parentToolCallId).ConfigureAwait(false);
        if (preparation.ImmediateEnd is { } immediate) return new(call, immediate.Result, immediate.IsError);

        // 2. 【AgentCore】【单工具执行】执行后沿用同一结果拦截和错误归一化路径
        var prepared = preparation.Prepared!;
        var executed = await ExecutePreparedParallelToolCallAsync(prepared, onUpdate ?? (_ => Task.CompletedTask), token).ConfigureAwait(false);
        var end = await FinalizeParallelToolCallAsync(prepared, executed, interceptors, logSink, logContext, token).ConfigureAwait(false);
        return new(call, end.Result, end.IsError);
    }
}
