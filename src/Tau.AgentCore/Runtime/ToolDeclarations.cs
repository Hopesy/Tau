// 作者：xxx
using Tau.Ai;

namespace Tau.AgentCore.Runtime;

/// <summary>【AgentCore】【工具声明】在消息发布前把模型可见集合与可执行集合对齐。</summary>
internal static class ToolDeclarations
{
    /// <summary>合并最后一条待发布系统消息，或在用户输入之前插入工具变化。</summary>
    /// <param name="history">已经提交的会话。</param>
    /// <param name="pending">待发布消息，调用方对象不会被修改。</param>
    /// <param name="tools">当前可执行工具。</param>
    /// <returns>包含必要工具声明的消息列表。</returns>
    internal static IReadOnlyList<ChatMessage> Declare(IReadOnlyList<ChatMessage> history, IReadOnlyList<ChatMessage> pending, IReadOnlyList<IAgentTool> tools)
    {
        // 1. 【AgentCore】【工具声明】最后一条系统消息的工具字段属于意图，以实际可执行集合重新计算
        var index = -1;
        for (var i = pending.Count - 1; i >= 0; i--) if (pending[i] is SystemMessage) { index = i; break; }
        var baseline = pending.ToArray();
        var system = index < 0 ? null : (SystemMessage)pending[index];
        if (system is not null) baseline[index] = system with { ToolsAdded = null, ToolsRemoved = null };
        var changes = Transcript.GetToolStateChanges(Transcript.GetCurrentTools(history.Concat(baseline)),
            tools.Select(tool => new Tool(tool.Name, tool.Description, tool.ParameterSchema) { ConstrainedSampling = tool.ConstrainedSampling }).ToArray());
        var unchanged = changes.ToolsAdded.Count == 0 && changes.ToolsRemoved.Count == 0;
        if (system is not null && unchanged && system.ToolsAdded is not { Count: > 0 } && system.ToolsRemoved is not { Count: > 0 }) return pending;
        if (system is null && unchanged) return pending;

        // 2. 【AgentCore】【工具声明】保留指令、段落和时间，仅替换工具字段
        var update = (system ?? new SystemMessage("")) with
        {
            ToolsAdded = changes.ToolsAdded.Count == 0 ? null : changes.ToolsAdded,
            ToolsRemoved = changes.ToolsRemoved.Count == 0 ? null : changes.ToolsRemoved
        };
        if (system is not null) { baseline[index] = update; return baseline; }
        return [update, .. pending];
    }
}
