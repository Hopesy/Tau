// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【扩展运行】供扩展使用的真实运行器控制能力。</summary>
public interface ICodingAgentExtensionRuntimeControl
{
    /// <summary>读取会话允许使用的全部工具。</summary>
    /// <returns>工具注册表。</returns>
    IReadOnlyList<IAgentTool> GetRegisteredTools();
    /// <summary>读取当前活动工具。</summary>
    /// <returns>活动工具名称。</returns>
    IReadOnlyList<string> GetActiveToolNames();
    /// <summary>修改下一回合使用的工具集合。</summary>
    /// <param name="names">工具名称列表。</param>
    void SetActiveTools(IReadOnlyList<string> names);
    /// <summary>读取当前系统提示词。</summary>
    /// <returns>已合并的提示词。</returns>
    string GetSystemPrompt();
    /// <summary>取消当前运行。</summary>
    void Abort();
    /// <summary>等待运行结束。</summary>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>等待任务。</returns>
    Task WaitForIdleAsync(CancellationToken cancellationToken);
    /// <summary>解析模型密钥。</summary>
    /// <param name="model">目标模型。</param>
    /// <returns>密钥或空值。</returns>
    string? ResolveModelApiKey(Model model);
}
