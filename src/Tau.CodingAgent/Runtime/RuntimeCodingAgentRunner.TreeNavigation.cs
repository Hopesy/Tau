// 作者：xxx
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【树导航选项】控制离开分支时的摘要及目标标签。</summary>
/// <param name="Summarize">是否生成分支摘要。</param><param name="CustomInstructions">摘要附加指令。</param>
/// <param name="ReplaceInstructions">是否替换默认摘要指令。</param><param name="Label">目标或摘要标签。</param>
public sealed record CodingAgentTreeNavigationOptions(bool Summarize = false, string? CustomInstructions = null, bool ReplaceInstructions = false, string? Label = null);

/// <summary>【CodingAgent】【树导航结果】返回扩展取消、摘要中断、编辑器文本及提交后的摘要条目。</summary>
/// <param name="Cancelled">操作是否取消。</param><param name="EditorText">用户或自定义消息的待编辑文本。</param>
/// <param name="SummaryEntry">已保存的原生摘要条目。</param><param name="Aborted">是否收到摘要取消信号。</param>
public sealed record CodingAgentTreeNavigationResult(
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("editorText")] string? EditorText = null,
    [property: JsonPropertyName("summaryEntry")] JsonElement? SummaryEntry = null,
    [property: JsonPropertyName("aborted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Aborted = false);

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【统一树导航】SDK 和交互命令使用扩展相同的准备、摘要、提交和通知事务。</summary>
    /// <param name="targetId">原始选中条目的完整标识；用户条目由事务负责回退到父节点。</param>
    /// <param name="options">摘要和标签选项。</param><param name="cancellationToken">导航取消信号。</param><returns>提交或取消结果。</returns>
    public Task<CodingAgentTreeNavigationResult> NavigateTreeAsync(string targetId, CodingAgentTreeNavigationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        EnsureSessionContext(null, null);
        return _boundarySession!.NavigateTreeAsync(JsonSerializer.SerializeToElement(new
        {
            targetId, summarize = options?.Summarize == true, customInstructions = options?.CustomInstructions,
            replaceInstructions = options?.ReplaceInstructions == true, label = options?.Label
        }), cancellationToken);
    }
}
