// 作者：xxx
namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentCommandRouter
{
    /// <summary>【CodingAgent】【交互树跳转】确认导航后恢复待发送队列并停止当前回答，然后进入原生导航事务。</summary>
    /// <param name="runner">当前原生会话。</param><param name="target">原始目标条目标识。</param>
    /// <param name="decision">用户确认的摘要和标签选项。</param><param name="token">宿主导航取消信号。</param><returns>导航结果。</returns>
    private async Task<CodingAgentTreeNavigationResult> NavigateNativeTreeAsync(RuntimeCodingAgentRunner runner, string target,
        CodingAgentTreeNavigationDecision decision, CancellationToken token)
    {
        if (runner.IsStreaming)
        {
            // 1. 【CodingAgent】【待发送输入】保留引导、跟进及当前草稿顺序，先交回编辑器再取消模型
            var queued = runner.DrainQueuedMessages();
            var messages = queued.Steering.Concat(queued.FollowUp).ToArray();
            if (messages.Length > 0)
                _inputDraftSetter?.Invoke(string.Join("\n\n", messages.Append(_inputDraftGetter?.Invoke()).Where(text => !string.IsNullOrWhiteSpace(text))));
            runner.Abort();
            await runner.WaitForIdleAsync(token).ConfigureAwait(false);
        }
        // 2. 【CodingAgent】【共享导航事务】摘要提示期间状态可能改变，由运行器再次检查互斥条件
        return await runner.NavigateTreeAsync(target,
            new(decision.Summarize, decision.CustomInstructions, decision.ReplaceInstructions, NormalizeTreeLabel(decision.Label)), token).ConfigureAwait(false);
    }
}
