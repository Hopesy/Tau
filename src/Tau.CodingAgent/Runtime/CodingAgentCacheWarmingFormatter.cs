// 作者：xxx
using System.Globalization;
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【预热呈现】格式化会话状态和独立计费用量。</summary>
public static class CodingAgentCacheWarmingFormatter
{
    /// <summary>【CodingAgent】【预热状态】输出决策倒计时、收益与扩展覆盖信息。</summary>
    /// <param name="status">当前状态。</param><param name="now">显示时间；省略时采用系统时间。</param><returns>单行状态文本。</returns>
    public static string FormatStatus(CodingAgentCacheWarmingStatus status, DateTimeOffset? now = null)
    {
        var decision = status.Decision;
        if (decision is null || status.State == "inactive" && !decision.EconomicsAvailable && !status.ExtensionOverride)
            return $"Inactive ({status.Reason ?? "unknown reason"})";
        var economics = decision.EconomicsAvailable ?
            $"{decimal.Round(decision.ContinuationProbability * 100)}% continuation probability{(decision.Phase == "streaming" ? " while agent is running" : "")}, expected savings {Dollars(decision.ExpectedSavings)} {(decision.Action == "warm" ? ">=" : "<")} $0.050"
            : "cache economics unavailable";
        var detail = status.ExtensionOverride ? "extension override, " + economics : economics + " -> " + decision.Action;
        return status.State switch { "inactive" => $"Stopped ({detail})", "refreshing" => $"Warming cache ({detail})",
            _ => $"{DecisionTime(status.NextWarmAt, now ?? DateTimeOffset.UtcNow)} ({detail})" };
    }

    /// <summary>【CodingAgent】【预热费用】读取原生用量事件并保留至少三位、至多六位小数。</summary>
    /// <param name="entry">独立 cache_warm 用量条目。</param><returns>可显示的费用行。</returns>
    public static string FormatUsage(JsonElement entry)
    {
        var note = entry.TryGetProperty("note", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        var amount = ReadUsageCost(entry);
        return $"Cache warmed{(string.IsNullOrEmpty(note) ? "" : " (" + note + ")")}: ${amount.ToString("0.000###", CultureInfo.InvariantCulture)}";
    }

    /// <summary>【CodingAgent】【摘要费用】从持久摘要的用量派生提示，不追加重复的会话记录。</summary>
    /// <param name="entry">压缩或分支摘要条目。</param><returns>摘要 token 和费用提示。</returns>
    public static string FormatSummaryUsage(JsonElement entry)
    {
        var usage = entry.GetProperty("usage");
        var tokens = 0;
        foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite" })
            if (usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var count)) tokens += count;
        var cost = ReadUsageCost(entry);
        var label = entry.GetProperty("type").GetString() == "compaction" ? "Compaction" : "Branch summary";
        return label + ": " + CodingAgentFooterFormatter.FormatTokens(tokens) + " tokens billed" +
            (cost >= 0.01m ? " (~$" + cost.ToString("F2", CultureInfo.InvariantCulture) + ")" : "");
    }

    /// <summary>【CodingAgent】【费用读取】允许缺少价格的合法模型用量，优先总费用并兼容只有费用分量的旧记录。</summary>
    /// <param name="entry">包含可选用量的条目。</param><returns>费用；未知时为零。</returns>
    private static decimal ReadUsageCost(JsonElement entry)
    {
        if (!entry.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object) return 0;
        if (cost.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetDecimal(out var amount)) return amount;
        var sum = 0m;
        foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite" })
            if (cost.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out amount)) sum += amount;
        return sum;
    }

    /// <summary>【CodingAgent】【美元格式】使用固定小数位和负号位置。</summary>
    /// <param name="value">美元金额。</param><returns>金额文本。</returns>
    private static string Dollars(decimal value) => (value < 0 ? "-$" : "$") + Math.Abs(value).ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>【CodingAgent】【决策倒计时】按小时、分钟、秒显示向上取整的剩余时间。</summary>
    /// <param name="next">下次决策时间。</param><param name="now">当前时间。</param><returns>倒计时文本。</returns>
    private static string DecisionTime(DateTimeOffset? next, DateTimeOffset now)
    {
        if (next is null || next <= now) return "Decision now";
        var seconds = (long)Math.Ceiling((next.Value - now).TotalSeconds);
        var parts = new List<string>();
        if (seconds / 3600 > 0) parts.Add($"{seconds / 3600}h");
        if (seconds % 3600 / 60 > 0) parts.Add($"{seconds % 3600 / 60}m");
        if (seconds % 60 > 0 || parts.Count == 0) parts.Add($"{seconds % 60}s");
        return "Decision in " + string.Join(" ", parts);
    }
}
