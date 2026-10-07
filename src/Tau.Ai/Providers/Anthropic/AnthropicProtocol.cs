// 作者：xxx
namespace Tau.Ai.Providers.Anthropic;

/// <summary>【AI】【Anthropic 协议】集中维护 OAuth 工具名称及供应商思考等级映射。</summary>
internal static class AnthropicProtocol
{
    internal const string ClaudeCodeVersion = "2.1.280";
    internal const string ClaudeCodeIdentity = "You are Claude Code, Anthropic's official CLI for Claude.";
    private static readonly Dictionary<string, string> ToolNames = new[]
    {
        "Read", "Write", "Edit", "Bash", "Grep", "Glob", "AskUserQuestion", "EnterPlanMode", "ExitPlanMode",
        "KillShell", "NotebookEdit", "Skill", "Task", "TaskOutput", "TodoWrite", "WebFetch", "WebSearch"
    }.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);

    /// <summary>【AI】【Anthropic OAuth】识别访问令牌，Copilot 始终使用自己的认证协议。</summary>
    /// <param name="apiKey">调用方或环境提供的认证值。</param>
    /// <param name="provider">供应商标识。</param>
    /// <returns>需要 Claude Code OAuth 请求格式时为 true。</returns>
    internal static bool IsOAuthToken(string? apiKey, string provider) =>
        !provider.Equals("github-copilot", StringComparison.OrdinalIgnoreCase) && apiKey?.Contains("sk-ant-oat", StringComparison.Ordinal) == true;

    /// <summary>把已知工具名转换为上游要求的大小写，自定义名称保持原值。</summary>
    /// <param name="name">本地工具名称。</param>
    /// <returns>OAuth 协议工具名称。</returns>
    internal static string ToClaudeCodeName(string name) => ToolNames.GetValueOrDefault(name, name);

    /// <summary>按当前活动工具恢复响应名称，避免删除或重定义后的工具绑定到旧声明。</summary>
    /// <param name="name">服务端返回的名称。</param>
    /// <param name="tools">当前活动工具集合。</param>
    /// <returns>首个忽略大小写匹配的本地名称，没有匹配时保持原值。</returns>
    internal static string FromClaudeCodeName(string name, IReadOnlyList<Tool> tools) =>
        tools.FirstOrDefault(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name ?? name;

    /// <summary>判断历史消息携带的等级是否属于 Anthropic 原生 effort。</summary>
    /// <param name="value">历史供应商等级。</param>
    /// <returns>有效等级时为 true。</returns>
    internal static bool IsEffort(string? value) => value is "low" or "medium" or "high" or "xhigh" or "max";

    /// <summary>【AI】【Anthropic 思考】优先读取模型等级映射，再兼容旧配置，最后使用上游默认等级。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="level">通用思考等级。</param>
    /// <returns>供应商原生 effort。</returns>
    internal static string MapEffort(Model model, ThinkingLevel level)
    {
        var key = StreamOptionHelpers.ToReasoningEffortName(level, allowExtraHigh: true);
        if (model.ThinkingLevelMap?.TryGetValue(key, out var mapped) == true && mapped is not null) return mapped;
        if (model.Compat?.ReasoningEffortMap?.TryGetValue(key, out var legacy) == true && !string.IsNullOrWhiteSpace(legacy)) return legacy;
        return level switch
        {
            ThinkingLevel.Minimal or ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            _ => "high"
        };
    }
}
