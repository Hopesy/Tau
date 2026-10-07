// 作者：xxx
using System.Globalization;
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【缓存损耗】一次请求重复计费的 token、额外费用、空闲时长和模型切换情况。</summary>
public sealed record CodingAgentCacheMiss(long MissedTokens, decimal MissedCost, double IdleMs, bool ModelChanged);

/// <summary>【CodingAgent】【缓存损耗】原始会话中的累计重复计费用量。</summary>
public sealed record CodingAgentCacheWaste(long MissedTokens, decimal MissedCost, int MissCount);

/// <summary>【CodingAgent】【缓存统计】按上游规则从真实请求用量推导缓存损耗，不把新上下文计为浪费。</summary>
public static class CodingAgentCacheStats
{
    public const double CacheTtlMs = 5 * 60 * 1000;
    private const int NoiseFloorTokens = 1024;
    private sealed record PreviousRequest(long PromptTokens, string ModelKey, DateTimeOffset? Timestamp, bool ReportedCache);
    private sealed record ScanResult(PreviousRequest? Previous, CodingAgentCacheWaste Totals, Dictionary<string, CodingAgentCacheMiss> Misses);

    /// <summary>【CodingAgent】【缓存总量】扫描原生会话条目，包含预热记录和摘要重置。</summary>
    /// <param name="entries">按写入顺序排列的原始条目。</param><param name="models">缓存读取单价查询；未知模型返回空。</param>
    /// <returns>累计缓存损耗。</returns>
    public static CodingAgentCacheWaste ComputeCacheWaste(IReadOnlyList<JsonElement> entries, Func<string, string, Model?> models) =>
        Scan(ReadEntries(entries), models).Totals;

    /// <summary>【CodingAgent】【缓存历史】按原始助手条目标识返回每次计入统计的缓存未命中。</summary>
    /// <param name="entries">原始会话条目。</param><param name="models">模型价格查询。</param>
    /// <returns>条目标识到损耗的独立映射。</returns>
    public static IReadOnlyDictionary<string, CodingAgentCacheMiss> CollectCacheMisses(IReadOnlyList<JsonElement> entries, Func<string, string, Model?> models) =>
        Scan(ReadEntries(entries), models).Misses;

    /// <summary>【CodingAgent】【缓存实时】检测尚未追加到条目列表的新助手消息。</summary>
    /// <param name="entries">不包含新消息的既有条目。</param><param name="message">新完成的模型回复。</param>
    /// <param name="models">模型价格查询。</param><returns>有效未命中；首次请求、无缓存或低于噪声阈值时为空。</returns>
    public static CodingAgentCacheMiss? DetectCacheMiss(IReadOnlyList<JsonElement> entries, AssistantMessage message, Func<string, string, Model?> models) =>
        DetectMiss(Scan(ReadEntries(entries), models).Previous, message, models);

    /// <summary>【CodingAgent】【缓存总量】使用已解析条目汇总，避免会话统计重复序列化。</summary>
    /// <param name="entries">已解析条目。</param><param name="models">模型价格查询。</param><returns>累计损耗。</returns>
    internal static CodingAgentCacheWaste ComputeCacheWaste(IReadOnlyList<CodingAgentTreeSessionEntry> entries, Func<string, string, Model?> models) =>
        Scan(entries, models).Totals;

    /// <summary>【CodingAgent】【缓存历史】复用已解析原始条目，为终端重建提供逐条损耗。</summary>
    /// <param name="entries">原始条目。</param><param name="models">模型价格查询。</param><returns>按条目标识索引的损耗。</returns>
    internal static IReadOnlyDictionary<string, CodingAgentCacheMiss> CollectCacheMisses(IReadOnlyList<CodingAgentTreeSessionEntry> entries, Func<string, string, Model?> models) =>
        Scan(entries, models).Misses;

    /// <summary>【CodingAgent】【缓存末次】查询已经保存的末次助手回复，供 message_end 之后的终端使用。</summary>
    /// <param name="entries">包含末次回复的条目。</param><param name="models">模型价格查询。</param><returns>末次回复对应的损耗。</returns>
    internal static CodingAgentCacheMiss? GetLastCacheMiss(IReadOnlyList<CodingAgentTreeSessionEntry> entries, Func<string, string, Model?> models)
    {
        var last = entries.LastOrDefault(entry => entry.Type == "message" && entry.Message?.Role == "assistant");
        return last is null ? null : Scan(entries, models).Misses.GetValueOrDefault(last.Id);
    }

    /// <summary>【CodingAgent】【缓存提示】仅显示较大的损耗，并只陈述可观测的模型切换或空闲时间。</summary>
    /// <param name="miss">一次缓存损耗。</param><returns>提示文本；低于显示阈值时为空。</returns>
    public static string? FormatNotice(CodingAgentCacheMiss miss)
    {
        if (miss.MissedTokens < 20000 && miss.MissedCost < 0.1m) return null;
        var label = miss.ModelChanged ? "Cache miss after model switch" : miss.IdleMs >= CacheTtlMs
            ? $"Cache miss after {Math.Floor(miss.IdleMs / 60000 + 0.5).ToString(CultureInfo.InvariantCulture)}m idle" : "Cache miss";
        var cost = miss.MissedCost >= 0.01m ? " (~$" + miss.MissedCost.ToString("F2", CultureInfo.InvariantCulture) + ")" : "";
        var tokens = miss.MissedTokens <= int.MaxValue ? CodingAgentFooterFormatter.FormatTokens((int)miss.MissedTokens)
            : (miss.MissedTokens / 1000000.0).ToString("0", CultureInfo.InvariantCulture) + "M";
        return $"{label}: {tokens} tokens re-billed{cost}";
    }

    /// <summary>【CodingAgent】【缓存条目】解析公开 JSON 输入，保留上游和旧用量字段的兼容读取。</summary>
    /// <param name="entries">JSON 条目。</param><returns>已解析的条目数组。</returns>
    private static IReadOnlyList<CodingAgentTreeSessionEntry> ReadEntries(IReadOnlyList<JsonElement> entries) =>
        entries.Select(entry => entry.Deserialize(CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)
            ?? throw new JsonException("Session entry must be an object.")).ToArray();

    /// <summary>【CodingAgent】【缓存扫描】沿写入顺序追踪最近请求，预热更新基线，压缩和分支摘要清空基线。</summary>
    /// <param name="entries">原始条目。</param><param name="models">价格查询。</param><returns>扫描状态、总量及逐条损耗。</returns>
    private static ScanResult Scan(IReadOnlyList<CodingAgentTreeSessionEntry> entries, Func<string, string, Model?> models)
    {
        PreviousRequest? previous = null;
        var totals = new CodingAgentCacheWaste(0, 0, 0);
        var misses = new Dictionary<string, CodingAgentCacheMiss>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // 1. 【CodingAgent】【缓存边界】摘要后上下文已经改变，模型切换本身不豁免重复计费
            if (entry.Type is "compaction" or "branch_summary") { previous = null; continue; }
            if (entry.Type == "usage" && entry.Kind == "cache_warm" && entry.Usage is { } warm)
            {
                var prompt = ReadTokens(warm, "input", "inputTokens") + ReadTokens(warm, "cacheRead", "cacheReadTokens") + ReadTokens(warm, "cacheWrite", "cacheWriteTokens");
                if (prompt > 0) previous = new(prompt, entry.Provider + "/" + entry.Model, entry.Timestamp, true);
            }
            else if (entry.Type == "message" && entry.Message is { Role: "assistant" } stored &&
                CodingAgentSessionStore.ToMessage(stored) is AssistantMessage message && message.Usage is { } usage)
            {
                message = message with { Timestamp = message.Timestamp ?? entry.Timestamp };
                if (DetectMiss(previous, message, models) is { } miss)
                {
                    totals = new(totals.MissedTokens + miss.MissedTokens, totals.MissedCost + miss.MissedCost, totals.MissCount + 1);
                    misses[entry.Id] = miss;
                }
                // 2. 【CodingAgent】【缓存证据】只读缓存提供方的全失效仍计数，零用量错误不能抹除此前证据
                var prompt = PromptTokens(usage);
                if (prompt > 0) previous = new(prompt, message.Provider + "/" + message.Model, message.Timestamp,
                    previous?.ReportedCache == true || usage.CacheReadTokens.GetValueOrDefault() + (long)usage.CacheWriteTokens.GetValueOrDefault() > 0);
            }
        }
        return new(previous, totals, misses);
    }

    /// <summary>【CodingAgent】【缓存检测】比较相邻提示量，按实际输入和写缓存费用推导重复计费差价。</summary>
    /// <param name="previous">此前有效请求。</param><param name="message">待检测回复。</param>
    /// <param name="models">无缓存读取费用时的模型价格回退。</param><returns>超过噪声阈值的未命中。</returns>
    private static CodingAgentCacheMiss? DetectMiss(PreviousRequest? previous, AssistantMessage message, Func<string, string, Model?> models)
    {
        if (previous is null || message.Usage is not { } usage) return null;
        var prompt = PromptTokens(usage);
        var read = usage.CacheReadTokens.GetValueOrDefault();
        var write = usage.CacheWriteTokens.GetValueOrDefault();
        if (prompt <= 0 || read + (long)write == 0 && !previous.ReportedCache) return null;
        var missed = Math.Min(previous.PromptTokens, prompt) - read;
        if (missed <= NoiseFloorTokens) return null;
        var paidTokens = (long)usage.InputTokens + write;
        var paidRate = paidTokens > 0 ? ((usage.Cost?.Input ?? 0) + (usage.Cost?.CacheWrite ?? 0)) / paidTokens : 0;
        var readRate = read > 0 ? (usage.Cost?.CacheRead ?? 0) / read
            : (models(message.Provider ?? "", message.Model ?? "")?.Cost?.CacheReadPerMillion ?? 0) / 1000000;
        var idle = message.Timestamp is { } now && previous.Timestamp is { } before ? Math.Max(0, (now - before).TotalMilliseconds) : 0;
        return new(missed, missed * Math.Max(0, paidRate - readRate), idle, message.Provider + "/" + message.Model != previous.ModelKey);
    }

    /// <summary>【CodingAgent】【提示量】只统计输入和缓存分量，不把输出或 reasoning token 加入提示。</summary>
    /// <param name="usage">回复用量。</param><returns>提示 token 数。</returns>
    private static long PromptTokens(Usage usage) => (long)usage.InputTokens + usage.CacheReadTokens.GetValueOrDefault() + usage.CacheWriteTokens.GetValueOrDefault();

    /// <summary>【CodingAgent】【预热用量】兼容读取原生和旧格式计数。</summary>
    /// <param name="usage">用量对象。</param><param name="name">原生名称。</param><param name="legacy">旧名称。</param><returns>有效计数。</returns>
    private static long ReadTokens(JsonElement usage, string name, string legacy) =>
        usage.ValueKind == JsonValueKind.Object && (usage.TryGetProperty(name, out var value) || usage.TryGetProperty(legacy, out value)) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? count : 0;
}

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【缓存统计】统计当前会话全部原始条目，不受上下文编辑或分支选择抹除。</summary>
    /// <returns>会话缓存损耗。</returns>
    public CodingAgentCacheWaste GetCacheWaste() => CalculateCacheWaste(ReadCacheEntries());

    /// <summary>【CodingAgent】【缓存提示】查询已提交末次回复的缓存损耗。</summary>
    /// <returns>末次回复的损耗或空值。</returns>
    internal CodingAgentCacheMiss? GetLastCacheMiss() => CodingAgentCacheStats.GetLastCacheMiss(ReadCacheEntries(), _modelCatalog.TryGetModel);

    /// <summary>【CodingAgent】【缓存统计】复用已读取快照与当前目录价格。</summary>
    /// <param name="entries">完整原始条目。</param><returns>会话累计损耗。</returns>
    internal CodingAgentCacheWaste CalculateCacheWaste(IReadOnlyList<CodingAgentTreeSessionEntry> entries) =>
        CodingAgentCacheStats.ComputeCacheWaste(entries, _modelCatalog.TryGetModel);

    /// <summary>【CodingAgent】【缓存来源】优先使用持久或内存会话，独立运行器回退到消息列表。</summary>
    /// <returns>按写入顺序排列的条目。</returns>
    private IReadOnlyList<CodingAgentTreeSessionEntry> ReadCacheEntries() => _boundarySession?.Snapshot().Entries ??
        Messages.Select((message, index) => new CodingAgentTreeSessionEntry
        {
            Type = message is Tau.AgentCore.Harness.AgentCompactionSummaryMessage ? "compaction"
                : message is Tau.AgentCore.Harness.AgentBranchSummaryMessage ? "branch_summary" : "message",
            Id = index.ToString(CultureInfo.InvariantCulture), Message = CodingAgentSessionStore.FromMessage(message)
        }).ToArray();
}
