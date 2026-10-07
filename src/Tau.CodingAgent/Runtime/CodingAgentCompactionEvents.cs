// 作者：xxx
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【压缩开始】通知宿主压缩的触发原因。</summary>
/// <param name="Reason">manual、threshold 或 overflow。</param>
public sealed record CodingAgentCompactionStartEvent(string Reason) : AgentEvent("compaction_start");

/// <summary>【CodingAgent】【压缩结束】通知宿主已提交的摘要或失败，并保证运行器已退出压缩状态。</summary>
/// <param name="Reason">压缩触发原因。</param>
/// <param name="Result">成功时的摘要。</param>
/// <param name="Aborted">是否被用户、扩展或取消信号中止。</param>
/// <param name="WillRetry">是否准备重试原有模型回合。</param>
/// <param name="ErrorMessage">非取消失败的描述。</param>
public sealed record CodingAgentCompactionEndEvent(string Reason, CodingAgentCompactionResult? Result,
    bool Aborted, bool WillRetry, string? ErrorMessage = null) : AgentEvent("compaction_end");

/// <summary>【CodingAgent】【压缩作用域】保存单次压缩的原因、扩展代次和结果来源，防止结束回调重入覆盖状态。</summary>
/// <param name="Reason">触发原因。</param>
/// <param name="WillRetry">是否重试原回合。</param>
/// <param name="Generation">扩展重载代次。</param>
internal sealed record CodingAgentCompactionInvocation(string Reason, bool WillRetry, long Generation)
{
    public bool FromExtension { get; set; }
}
