using Tau.Ai;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSessionStats(
    string Provider,
    string Model,
    int TotalMessages,
    int UserMessages,
    int AssistantMessages,
    int ToolResultMessages,
    int ToolCalls,
    int EstimatedTokens,
    int? ContextWindowTokens,
    string? SessionName,
    string? SessionFile)
{
    public string? SessionId { get; init; }
    public CodingAgentContextUsage? ContextUsage { get; init; }
    public CodingAgentSessionUsageTotals Tokens { get; init; } = CodingAgentSessionUsageTotals.Empty;
    public decimal Cost { get; init; }
    public int CostRecords { get; init; }
    public CodingAgentCacheWaste CacheWaste { get; init; } = new(0, 0, 0);
    public IReadOnlyList<CodingAgentUsageCostBreakdownEntry> UsageBreakdown { get; init; } = [];

    public CodingAgentSessionStats WithUsage(CodingAgentSessionUsageSummary usage) =>
        this with
        {
            Tokens = usage.Tokens,
            Cost = usage.Cost,
            CostRecords = usage.CostRecords
        };
}

/// <summary>【CodingAgent】【当前上下文】保存当前模型窗口及有效用量，压缩后尚无新响应时用量未知。</summary>
/// <param name="Tokens">当前上下文 token 数，未知时为空。</param>
/// <param name="ContextWindow">当前模型上下文窗口。</param>
/// <param name="Percent">窗口使用百分比，未知时为空。</param>
public sealed record CodingAgentContextUsage(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Tokens,
    int ContextWindow,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Percent);

public sealed record CodingAgentSessionUsageTotals(
    int Input,
    int Output,
    int CacheRead,
    int CacheWrite)
{
    public static CodingAgentSessionUsageTotals Empty { get; } = new(0, 0, 0, 0);

    public int Total => Input + Output + CacheRead + CacheWrite;
}

public sealed record CodingAgentSessionUsageSummary(
    CodingAgentSessionUsageTotals Tokens,
    decimal Cost,
    int CostRecords)
{
    public static CodingAgentSessionUsageSummary Empty { get; } = new(CodingAgentSessionUsageTotals.Empty, 0m, 0);

    public static CodingAgentSessionUsageSummary FromMessages(IReadOnlyList<ChatMessage> messages)
    {
        var input = 0;
        var output = 0;
        var cacheRead = 0;
        var cacheWrite = 0;
        var cost = 0m;
        var costRecords = 0;

        foreach (var usage in messages.Select(static message => message switch { AssistantMessage assistant => assistant.Usage, ToolResultMessage tool => tool.Usage, _ => null }))
        {
            if (usage is not { } value)
            {
                continue;
            }

            input += value.InputTokens;
            output += value.OutputTokens;
            cacheRead += value.CacheReadTokens.GetValueOrDefault();
            cacheWrite += value.CacheWriteTokens.GetValueOrDefault();

            if (value.Cost is { } usageCost)
            {
                cost += usageCost.Total;
                costRecords++;
            }
        }

        return new CodingAgentSessionUsageSummary(
            new CodingAgentSessionUsageTotals(input, output, cacheRead, cacheWrite),
            cost,
            costRecords);
    }

    /// <summary>【CodingAgent】【历史总用量】汇总原始消息、独立用量与摘要条目，压缩或分支选择不会抹除已计费记录。</summary>
    /// <param name="entries">要统计的原始条目。</param>
    /// <returns>合计 token、费用和费用记录数。</returns>
    internal static CodingAgentSessionUsageSummary FromEntries(IEnumerable<CodingAgentTreeSessionEntry> entries)
    {
        var total = Empty;
        foreach (var entry in entries)
        {
            CodingAgentSessionUsageSummary value;
            if (entry.Type == "message" && entry.Message is { } stored && CodingAgentSessionStore.ToMessage(stored) is { } message)
                value = FromMessages([message]);
            else if (entry.Type is "usage" or "compaction" or "branch_summary" && entry.Usage is { ValueKind: JsonValueKind.Object } usage)
            {
                var cost = usage.TryGetProperty("cost", out var costs) && costs.ValueKind == JsonValueKind.Object;
                var amount = cost ? costs.TryGetProperty("total", out var price) && price.TryGetDecimal(out var sum) ? sum
                    : ReadDecimal(costs, "input") + ReadDecimal(costs, "output") + ReadDecimal(costs, "cacheRead") + ReadDecimal(costs, "cacheWrite") : 0;
                value = new(new(ReadInt(usage, "input", "inputTokens"), ReadInt(usage, "output", "outputTokens"),
                    ReadInt(usage, "cacheRead", "cacheReadTokens"), ReadInt(usage, "cacheWrite", "cacheWriteTokens")), amount, cost ? 1 : 0);
            }
            else continue;
            total = new(new(total.Tokens.Input + value.Tokens.Input, total.Tokens.Output + value.Tokens.Output,
                total.Tokens.CacheRead + value.Tokens.CacheRead, total.Tokens.CacheWrite + value.Tokens.CacheWrite),
                total.Cost + value.Cost, total.CostRecords + value.CostRecords);
        }
        return total;
    }

    /// <summary>【CodingAgent】【计数读取】读取上游或旧格式 token 字段。</summary>
    /// <param name="usage">用量对象。</param>
    /// <param name="name">上游字段名。</param>
    /// <param name="legacy">旧格式字段名。</param>
    /// <returns>有效整数，缺少时为零。</returns>
    private static int ReadInt(JsonElement usage, string name, string legacy) =>
        (usage.TryGetProperty(name, out var value) || usage.TryGetProperty(legacy, out value)) && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>【CodingAgent】【费用读取】读取可选费用分量。</summary>
    /// <param name="cost">费用对象。</param>
    /// <param name="name">分量名称。</param>
    /// <returns>有效十进制费用，缺少时为零。</returns>
    private static decimal ReadDecimal(JsonElement cost, string name) => cost.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : 0;
}
