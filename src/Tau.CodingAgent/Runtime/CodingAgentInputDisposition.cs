// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【提示接收】提示已由扩展处理、已排队或已开始一次运行。</summary>
public enum CodingAgentPromptDisposition { Handled, Queued, Started }
/// <summary>【CodingAgent】【队列接收】输入已由扩展处理或已进入队列。</summary>
public enum CodingAgentQueuedInputDisposition { Handled, Queued }

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【提示预检】在扩展处理或模型运行准备完成后报告一次接收结果。</summary>
    /// <param name="input">用户文本与图片内容。</param><param name="preflightResult">接收结果回调。</param>
    /// <param name="logContext">可选日志上下文。</param><param name="cancellationToken">运行取消信号。</param>
    /// <returns>本次提示及其后续动作的事件流。</returns>
    public IAsyncEnumerable<AgentEvent> RunWithDispositionAsync(IReadOnlyList<ContentBlock> input,
        Func<CodingAgentPromptDisposition, Task> preflightResult, TauRuntimeLogContext? logContext = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preflightResult);
        if (input.Count == 0) throw new ArgumentException("Input content must not be empty.", nameof(input));
        var completed = 0;
        return RunWithCommandsAsync([new UserMessage(input)], input.OfType<TextContent>().Sum(block => block.Text.Length),
            CreateRunLogContext(logContext), cancellationToken, disposition => Interlocked.Exchange(ref completed, 1) == 0
                ? preflightResult(disposition) : Task.CompletedTask);
    }

    /// <summary>【CodingAgent】【有结果的引导】处理扩展输入后排入引导队列，空闲时也允许预先排队。</summary>
    /// <param name="input">完整输入消息。</param><param name="cancellationToken">输入处理取消信号。</param><returns>处理或排队结果。</returns>
    public CodingAgentQueuedInputDisposition SteerWithDisposition(ChatMessage input, CancellationToken cancellationToken = default) => QueueInput(input, "steer", cancellationToken);

    /// <summary>【CodingAgent】【有结果的跟进】处理扩展输入后排入跟进队列，空闲时也允许预先排队。</summary>
    /// <param name="input">完整输入消息。</param><param name="cancellationToken">输入处理取消信号。</param><returns>处理或排队结果。</returns>
    public CodingAgentQueuedInputDisposition FollowUpWithDisposition(ChatMessage input, CancellationToken cancellationToken = default) => QueueInput(input, "followUp", cancellationToken);

    /// <summary>【CodingAgent】【流中提示】先允许扩展接管，再验证运行中提示必须指定的投递方式。</summary>
    /// <param name="input">原始用户消息。</param><param name="behavior">steer 或 followUp，可空。</param>
    /// <param name="token">输入处理取消信号。</param><returns>处理或排队结果。</returns>
    internal CodingAgentQueuedInputDisposition QueuePromptInput(ChatMessage input, string? behavior, CancellationToken token)
    {
        if (IsCompacting) throw new InvalidOperationException("Cannot submit a prompt while compaction is in progress.");
        var prepared = PrepareInputMessage(input, behavior, token);
        if (prepared is null) return CodingAgentQueuedInputDisposition.Handled;
        if (behavior is null) throw new InvalidOperationException("Agent is already running; use steer, follow_up, or prompt.streamingBehavior.");
        if (behavior.Equals("steer", StringComparison.OrdinalIgnoreCase)) EnqueuePreparedInput(prepared, false);
        else if (behavior.Equals("followUp", StringComparison.OrdinalIgnoreCase) || behavior.Equals("follow_up", StringComparison.OrdinalIgnoreCase)) EnqueuePreparedInput(prepared, true);
        else throw new ArgumentException("streamingBehavior must be steer or followUp.", nameof(behavior));
        return CodingAgentQueuedInputDisposition.Queued;
    }
}
