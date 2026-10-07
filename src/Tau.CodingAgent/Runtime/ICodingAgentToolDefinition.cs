// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【工具目录】会话层的工具访问策略，不改变底层 Agent 的执行接口。</summary>
public interface ICodingAgentToolDefinition : IAgentTool
{
    /// <summary>direct、model-only、codemode、deferred 或 hidden，默认 direct。</summary>
    string Exposure => "direct";
    /// <summary>direct 和 model-only 工具是否在注册时启用，空值等同启用。</summary>
    bool? DefaultActive => null;
    /// <summary>工具所属命名组，包含 name、description 和 instructions。</summary>
    JsonElement? Namespace => null;
    /// <summary>工具作者提供的行为提示，不代表宿主验证结果。</summary>
    JsonElement? Annotations => null;
    /// <summary>工具所属资源的来源信息，普通 SDK 工具可省略。</summary>
    CodingAgentSourceInfo? SourceInfo => null;

    /// <summary>【CodingAgent】【工具组合】调整模型看到的工具说明和声明。</summary>
    /// <param name="loadout">原始工具组合。</param>
    /// <returns>呈现变更，无变更时为空。</returns>
    CodingAgentToolLoadoutChanges? PrepareLoadout(CodingAgentToolLoadout loadout) => null;
}

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【工具访问】读取当前工具的访问策略，普通 Agent 工具使用 direct。</summary>
    /// <param name="tool">已注册工具。</param>
    /// <returns>工具访问策略。</returns>
    internal static string GetToolExposure(IAgentTool tool) => tool is ICodingAgentToolDefinition definition ? definition.Exposure : "direct";

    /// <summary>【CodingAgent】【默认启用】仅默认启用未明确关闭的 direct 和 model-only 工具。</summary>
    /// <param name="tool">已注册工具。</param>
    /// <returns>是否默认启用。</returns>
    internal static bool IsToolActiveOnRegistration(IAgentTool tool) =>
        GetToolExposure(tool) is "direct" or "model-only" && (tool as ICodingAgentToolDefinition)?.DefaultActive != false;

    /// <summary>【CodingAgent】【程序调用】返回活动 direct 工具和全部 codemode、deferred 工具，排除 model-only 与 hidden。</summary>
    /// <returns>按注册顺序排列的程序可调用工具。</returns>
    public IReadOnlyList<IAgentTool> GetCallableTools()
    {
        var active = GetActiveToolNames().ToHashSet(StringComparer.Ordinal);
        return _registeredTools.Where(tool => GetToolExposure(tool) is "codemode" or "deferred" ||
            GetToolExposure(tool) == "direct" && active.Contains(tool.Name)).ToArray();
    }
}
