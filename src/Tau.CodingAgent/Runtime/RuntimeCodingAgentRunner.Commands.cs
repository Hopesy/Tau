// 作者：xxx
using System.Runtime.CompilerServices;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【扩展诊断】命令错误事件，不把扩展失败伪装为模型失败。</summary>
/// <param name="ExtensionPath">命令或扩展标识。</param>
/// <param name="Event">失败操作类型。</param>
/// <param name="Error">错误说明。</param>
public sealed record CodingAgentExtensionErrorEvent(string ExtensionPath, string Event, string Error) : AgentEvent("extension_error");

public sealed partial class RuntimeCodingAgentRunner
{
    private CodingAgentExtensionCommandStore? _inputCommands;

    /// <summary>【CodingAgent】【命令绑定】为 SDK、打印和 RPC 配置与交互终端相同的扩展命令目录。</summary>
    /// <param name="commands">绑定本会话的扩展命令存储。</param>
    internal void ConfigureExtensionCommands(CodingAgentExtensionCommandStore commands) => _inputCommands = commands;

    /// <summary>【CodingAgent】【命令识别】判断原始文本是否匹配当前注册的扩展命令。</summary>
    /// <param name="text">原始输入。</param>
    /// <returns>是否为可执行扩展命令。</returns>
    internal bool IsExtensionCommand(string text)
    {
        if (!text.StartsWith('/')) return false;
        var space = text.IndexOf(' ');
        var name = space < 0 ? text[1..] : text[1..space];
        return _inputCommands?.Load().Any(command => command.InvocationName == name) == true;
    }

    /// <summary>【CodingAgent】【命令顺序】在取得模型运行锁和执行 input 钩子之前处理扩展命令。</summary>
    /// <param name="input">原始用户或自定义消息。</param>
    /// <param name="inputBytes">原始输入大小。</param>
    /// <param name="logContext">日志上下文。</param>
    /// <param name="token">调用取消信号。</param>
    /// <param name="preflightResult">可选的单次接收回调。</param>
    /// <returns>命令投递和剩余输入产生的完整事件序列。</returns>
    private async IAsyncEnumerable<AgentEvent> RunWithCommandsAsync(IReadOnlyList<ChatMessage> input, int inputBytes,
        TauRuntimeLogContext logContext, [EnumeratorCancellation] CancellationToken token,
        Func<CodingAgentPromptDisposition, Task>? preflightResult = null)
    {
        var pending = new List<ChatMessage>();
        var deliveries = new List<CodingAgentExtensionMessageDelivery>();
        foreach (var message in input)
        {
            token.ThrowIfCancellationRequested();
            var text = message is UserMessage user ? string.Join("\n", user.Content.OfType<TextContent>().Select(block => block.Text)) : null;
            if (text is null || _inputCommands?.TryInvoke(text, out var invocation, token, preserveMessageActions: true) != true || invocation is null)
            {
                pending.Add(message);
                continue;
            }
            if (invocation.IsError)
            {
                var path = "command:" + invocation.Command.InvocationName;
                LogExtensionEventError(new(path, invocation.Command.Scope, invocation.Command.Runtime, "command", invocation.Message), logContext);
                yield return new CodingAgentExtensionErrorEvent(path, "command", invocation.Message);
            }
            if (invocation.MessageActions is { } actions) deliveries.AddRange(actions);
            else if (invocation.SendToRunner) deliveries.Add(new(new UserMessage(invocation.Message), null, null, false, invocation.Command.FilePath));
        }
        if (pending.Count == 0 && preflightResult is not null) await preflightResult(CodingAgentPromptDisposition.Handled).ConfigureAwait(false);
        if (pending.Count == 0 && IsStreaming)
        {
            foreach (var delivery in deliveries) EnqueueExtensionMessage(delivery);
            yield break;
        }
        // 1. 【CodingAgent】【命令等待】命令可以等待先前回合空闲，消息提交后才占用运行锁
        if (pending.Count > 0 && IsCompacting) throw new InvalidOperationException("Cannot submit a prompt while compaction is in progress.");
        await foreach (var evt in InstrumentRun(RunPreparedAsync(pending, logContext, token, deliveries: deliveries, preflightResult: preflightResult),
            inputBytes, logContext, token).ConfigureAwait(false))
        {
            // 2. 【CodingAgent】【接收与取消】建立运行生命周期后才确认 started，响应写入期间的取消仍能产生结算事件
            if (evt is AgentStartEvent && preflightResult is not null) await preflightResult(CodingAgentPromptDisposition.Started).ConfigureAwait(false);
            yield return evt;
        }
    }

    /// <summary>【CodingAgent】【命令投递】空闲时枚举自动回合，运行时按动作选项加入当前回合。</summary>
    /// <param name="deliveries">命令或快捷键生成的有序消息动作。</param>
    /// <param name="token">调用取消信号。</param>
    /// <returns>空闲投递的事件序列；运行中只排队，不重复发布模型事件。</returns>
    internal async IAsyncEnumerable<AgentEvent> DeliverExtensionMessagesAsync(IReadOnlyList<CodingAgentExtensionMessageDelivery> deliveries,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (deliveries.Count == 0) yield break;
        if (IsStreaming)
        {
            foreach (var delivery in deliveries) EnqueueExtensionMessage(delivery);
            yield break;
        }
        var context = CreateRunLogContext();
        await foreach (var evt in InstrumentRun(RunPreparedAsync([], context, token, deliveries: deliveries), 0, context, token).ConfigureAwait(false)) yield return evt;
    }
}
