// 作者：xxx
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【用量合计】不重复计入推理或一小时写缓存子集的累计用量。</summary>
public sealed record CodingAgentUsageTotals(long Input, long Output, long CacheRead, long CacheWrite, decimal Cost);

/// <summary>【CodingAgent】【费用归属】按实际响应模型或工具/摘要汇总的费用和 token。</summary>
public sealed record CodingAgentUsageCostBreakdownEntry(string Key, decimal Cost, long Tokens);

/// <summary>【CodingAgent】【用量核算】共享用量合并、原始历史分组和实际模型归属规则。</summary>
public static class CodingAgentUsageAccounting
{
    /// <summary>【CodingAgent】【用量初始化】创建无可变共享状态的零用量。</summary>
    /// <returns>零用量。</returns>
    public static CodingAgentUsageTotals CreateUsageTotals() => new(0, 0, 0, 0, 0);

    /// <summary>【CodingAgent】【用量累计】把计费分量加入已有合计，保留原输入供其他统计使用。</summary>
    /// <param name="totals">已有合计。</param><param name="usage">新增请求用量。</param><returns>新的合计。</returns>
    public static CodingAgentUsageTotals AddUsageToTotals(CodingAgentUsageTotals totals, Usage usage) =>
        new(totals.Input + usage.InputTokens, totals.Output + usage.OutputTokens,
            totals.CacheRead + usage.CacheReadTokens.GetValueOrDefault(), totals.CacheWrite + usage.CacheWriteTokens.GetValueOrDefault(),
            totals.Cost + (usage.Cost?.Total ?? 0));

    /// <summary>【CodingAgent】【用量合并】合并两次请求，任一方报告可选子计数时才保留对应字段。</summary>
    /// <param name="first">第一份用量。</param><param name="second">第二份用量。</param><returns>不修改输入的合并用量。</returns>
    public static Usage CombineUsage(Usage first, Usage second)
    {
        var a = first.Cost.GetValueOrDefault();
        var b = second.Cost.GetValueOrDefault();
        return new(first.InputTokens + second.InputTokens, first.OutputTokens + second.OutputTokens,
            first.CacheReadTokens.GetValueOrDefault() + second.CacheReadTokens.GetValueOrDefault(),
            first.CacheWriteTokens.GetValueOrDefault() + second.CacheWriteTokens.GetValueOrDefault(),
            Cost: first.Cost is null && second.Cost is null ? null
                : new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite))
        {
            TotalTokens = (first.TotalTokens ?? TotalTokens(first)) + (second.TotalTokens ?? TotalTokens(second)),
            CacheWrite1hTokens = AddOptional(first.CacheWrite1hTokens, second.CacheWrite1hTokens),
            ReasoningTokens = AddOptional(first.ReasoningTokens, second.ReasoningTokens)
        };
    }

    /// <summary>【CodingAgent】【费用归属】从原生 JSON 条目计算按费用降序排列的模型明细。</summary>
    /// <param name="entries">包含全部分支的原始条目。</param><returns>费用相同时保持首次出现顺序的明细。</returns>
    public static IReadOnlyList<CodingAgentUsageCostBreakdownEntry> GetUsageCostBreakdown(IReadOnlyList<JsonElement> entries) =>
        GetUsageCostBreakdown(entries.Select(entry => entry.Deserialize(CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)
            ?? throw new JsonException("Session entry must be an object.")).ToArray());

    /// <summary>【CodingAgent】【消息费用】为没有会话存储的运行器生成相同归属明细。</summary>
    /// <param name="messages">当前消息。</param><returns>费用明细。</returns>
    internal static IReadOnlyList<CodingAgentUsageCostBreakdownEntry> FromMessages(IReadOnlyList<ChatMessage> messages) =>
        GetUsageCostBreakdown(messages.Select(message => new CodingAgentTreeSessionEntry
        { Type = "message", Message = CodingAgentSessionStore.FromMessage(message) }).ToArray());

    /// <summary>【CodingAgent】【历史费用】实际响应模型优先于选择模型，独立用量沿用自身归属，工具与摘要单独成组。</summary>
    /// <param name="entries">原始条目。</param><returns>不含零费用且零 token 空组的明细。</returns>
    internal static IReadOnlyList<CodingAgentUsageCostBreakdownEntry> GetUsageCostBreakdown(IReadOnlyList<CodingAgentTreeSessionEntry> entries)
    {
        var groups = new Dictionary<string, CodingAgentUsageCostBreakdownEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // 1. 【CodingAgent】【计费归属】上下文编辑不参与计费，路由结果按 responseModel 合并
            var key = entry.Type switch
            {
                "message" when entry.Message is { Role: "assistant", Usage: not null } message =>
                    $"{message.Provider ?? "undefined"}/{message.ResponseModel ?? message.Model ?? "undefined"}",
                "message" when entry.Message is { Role: "toolResult", Usage: not null } => "Tools/summaries",
                "usage" when entry.Usage is not null => $"{entry.Provider ?? "undefined"}/{entry.Model ?? "undefined"}",
                "compaction" or "branch_summary" when entry.Usage is not null => "Tools/summaries",
                _ => null
            };
            if (key is null) continue;
            var summary = CodingAgentSessionUsageSummary.FromEntries([entry]);
            var tokens = (long)summary.Tokens.Input + summary.Tokens.Output + summary.Tokens.CacheRead + summary.Tokens.CacheWrite;
            var previous = groups.GetValueOrDefault(key);
            groups[key] = new(key, (previous?.Cost ?? 0) + summary.Cost, (previous?.Tokens ?? 0) + tokens);
        }
        // 2. 【CodingAgent】【计费排序】稳定排序保持同价组的首次出现顺序，未定价但有用量的模型仍保留
        return groups.Values.Where(entry => entry.Cost > 0 || entry.Tokens > 0).OrderByDescending(entry => entry.Cost).ToArray();
    }

    /// <summary>【CodingAgent】【可选计数】两边均未报告时保持未知，显式零仍是已报告值。</summary>
    /// <param name="first">第一计数。</param><param name="second">第二计数。</param><returns>可选合计。</returns>
    private static int? AddOptional(int? first, int? second) => first is null && second is null ? null : first.GetValueOrDefault() + second.GetValueOrDefault();

    /// <summary>【CodingAgent】【总量回退】服务端未提供总数时由互斥计费分量计算。</summary>
    /// <param name="usage">请求用量。</param><returns>总 token。</returns>
    private static int TotalTokens(Usage usage) => usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens.GetValueOrDefault() + usage.CacheWriteTokens.GetValueOrDefault();
}
