// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentCacheStatsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    /// <summary>【CodingAgent】【缓存证据】无缓存提供方、首次请求和噪声边界不会虚报损耗。</summary>
    /// <param name="read">当前缓存读取量。</param><param name="evidence">此前是否报告缓存。</param><param name="expected">预期未命中数。</param>
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(0, true, 1)]
    [InlineData(8976, true, 0)]
    [InlineData(8975, true, 1)]
    [InlineData(10000, true, 0)]
    public void CacheEvidenceAndNoiseFloor(int read, bool evidence, int expected)
    {
        var first = Message(evidence ? new(0, 1, 10000) : new(10000, 1));
        var second = Message(new(10000 - read, 1, read), Start.AddMinutes(1));
        Assert.Null(CodingAgentCacheStats.DetectCacheMiss([], first, Lookup));
        var entries = new[] { Entry("first", first), Entry("second", second) };
        var result = CodingAgentCacheStats.ComputeCacheWaste(entries, Lookup);
        Assert.Equal(expected, result.MissCount);
        Assert.Equal(expected == 0 ? 0 : 10000 - read, result.MissedTokens);
        Assert.Equal(expected, CodingAgentCacheStats.CollectCacheMisses(entries, Lookup).Count);
    }

    /// <summary>【CodingAgent】【实际费率】损耗使用本次输入/写入混合费率及实际读取折扣，不使用目录输入标价。</summary>
    [Fact]
    public void WeightedPaidRateAndActualReadRateDeterminePenalty()
    {
        var first = Entry("first", Message(new(0, 1, 10000)));
        var next = Message(new(3000, 1, 4000, 3000, Cost: new(.006m, 0, .001m, .012m)), Start.AddMinutes(7));
        var miss = CodingAgentCacheStats.DetectCacheMiss([first], next, Lookup)!;
        Assert.Equal(6000, miss.MissedTokens);
        Assert.Equal(.0165m, miss.MissedCost);
        Assert.Equal(420000, miss.IdleMs);
        Assert.False(miss.ModelChanged);
        Assert.Equal(miss, CodingAgentCacheStats.CollectCacheMisses([first, Entry("next", next)], Lookup)["next"]);
    }

    /// <summary>【CodingAgent】【全量失效】全失效读取目录折扣；无价格不伪造费用，时间回退不产生负空闲时长。</summary>
    /// <param name="knownPrice">是否有目录缓存价格。</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FullMissUsesFallbackPricingAndClampsTime(bool knownPrice)
    {
        var first = Entry("first", Message(new(0, 1, 10000)));
        var next = Message(new(10000, 1, Cost: new(.03m, 0)), Start.AddMinutes(-1)) with { Model = "other" };
        var miss = CodingAgentCacheStats.DetectCacheMiss([first], next, (provider, model) => knownPrice ? Lookup(provider, model) : null)!;
        Assert.Equal(knownPrice ? .0275m : .03m, miss.MissedCost);
        Assert.Equal(0, miss.IdleMs);
        Assert.True(miss.ModelChanged);
    }

    /// <summary>【CodingAgent】【扫描重置】摘要切断缓存证据，其他元数据或零用量错误保留上一次有效请求。</summary>
    /// <param name="type">插入条目类型。</param><param name="expected">预期未命中数。</param>
    [Theory]
    [InlineData("compaction", 0)]
    [InlineData("branch_summary", 0)]
    [InlineData("model_change", 1)]
    [InlineData("context_edit", 1)]
    [InlineData("message", 1)]
    public void SummaryResetsEvidenceButZeroUsageAndMetadataDoNot(string type, int expected)
    {
        var middle = type == "message" ? Entry("middle", Message(new(0, 0)) with { StopReason = StopReason.Error })
            : JsonSerializer.SerializeToElement(new CodingAgentTreeSessionEntry { Id = "middle", Type = type }, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
        var entries = new[] { Entry("first", Message(new(0, 1, 10000))), middle,
            Entry("next", Message(new(10000, 1))), Entry("last", Message(new(10000, 1))) };
        Assert.Equal(expected == 0 ? 0 : 2, CodingAgentCacheStats.ComputeCacheWaste(entries, Lookup).MissCount);
    }

    /// <summary>【CodingAgent】【预热基线】独立预热更新提示量、模型和最近时间，自身不计入缓存损耗。</summary>
    /// <param name="legacy">是否使用旧计数字段。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmingUpdatesBaselineAndSupportsLegacyCounters(bool legacy)
    {
        using var usage = JsonDocument.Parse(legacy ? """{"inputTokens":0,"cacheReadTokens":30000}""" : """{"input":0,"cacheRead":30000}""");
        var warm = JsonSerializer.SerializeToElement(new CodingAgentTreeSessionEntry
        { Type = "usage", Kind = "cache_warm", Id = "warm", Provider = "test", Model = "other", Timestamp = Start.AddMinutes(9), Usage = usage.RootElement },
            CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
        var next = Message(new(40000, 1, Cost: new(.12m, 0)), Start.AddMinutes(10)) with { Model = "other" };
        var miss = CodingAgentCacheStats.DetectCacheMiss([Entry("first", Message(new(50000, 1))), warm], next, Lookup)!;
        Assert.Equal(30000, miss.MissedTokens);
        Assert.Equal(60000, miss.IdleMs);
        Assert.False(miss.ModelChanged);
        Assert.Equal(.0825m, miss.MissedCost);
    }

    /// <summary>【CodingAgent】【收缩提示】提示缩短不把被移除 token 计为失效，已付费率低于缓存费率时差价为零。</summary>
    [Fact]
    public void ShrinkingPromptsAndDiscountsDoNotInventCosts()
    {
        var first = Entry("first", Message(new(0, 1, 50000)));
        var next = Message(new(2000, 1, 5000, Cost: new(.0001m, 0, .1m, 0)));
        var miss = CodingAgentCacheStats.DetectCacheMiss([first], next, Lookup)!;
        Assert.Equal(2000, miss.MissedTokens);
        Assert.Equal(0, miss.MissedCost);
        Assert.Null(CodingAgentCacheStats.FormatNotice(miss));
    }

    /// <summary>【CodingAgent】【显示阈值】大 token 量或高费用才提示，模型切换优先于空闲原因。</summary>
    [Fact]
    public void NoticeThresholdsAndObservableReasons()
    {
        Assert.Null(CodingAgentCacheStats.FormatNotice(new(19999, .099m, 0, false)));
        Assert.Equal("Cache miss: 20k tokens re-billed", CodingAgentCacheStats.FormatNotice(new(20000, 0, 0, false)));
        Assert.Equal("Cache miss: 2.0k tokens re-billed (~$0.10)", CodingAgentCacheStats.FormatNotice(new(2000, .1m, 0, false)));
        Assert.Equal("Cache miss after 6m idle: 20k tokens re-billed", CodingAgentCacheStats.FormatNotice(new(20000, 0, 330000, false)));
        Assert.Equal("Cache miss after model switch: 20k tokens re-billed", CodingAgentCacheStats.FormatNotice(new(20000, 0, 330000, true)));
    }

    /// <summary>【CodingAgent】【无关计费】工具等独立用量不会被误用为预热证据。</summary>
    [Fact]
    public void OtherUsageDoesNotRefreshCacheEvidence()
    {
        using var document = JsonDocument.Parse("""{"type":"usage","id":"tool","kind":"tool","timestamp":"2026-01-01T00:09:00Z","provider":"test","model":"model","usage":{"input":50000,"cacheRead":0}}""");
        Assert.Null(CodingAgentCacheStats.DetectCacheMiss([Entry("first", Message(new(10000, 1))), document.RootElement],
            Message(new(50000, 1), Start.AddMinutes(10)), Lookup));
    }

    /// <summary>【CodingAgent】【历史统计】真实 JSONL 重载后保留预热影响和累计费用，摘要重置不清除已发生损耗。</summary>
    [Fact]
    public void PersistedStatisticsSurviveReloadAndSummaryReset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-cache-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "session.jsonl");
            var store = new CodingAgentTreeSessionStore(path);
            store.AppendMessages([Message(new(0, 1, 10000))], 0);
            store.AppendUsage("cache_warm", "test", "model", new(0, 1, 30000));
            store.AppendMessages([Message(new(40000, 1, Cost: new(.12m, 0)))], 0);
            var before = CodingAgentCacheStats.ComputeCacheWaste(store.ReadExtensionSnapshot().Entries, Lookup);
            Assert.Equal(new CodingAgentCacheWaste(30000, .0825m, 1), before);
            var entries = new CodingAgentTreeSessionStore(path).ReadExtensionSnapshot().Entries;
            Assert.Equal(before, CodingAgentCacheStats.ComputeCacheWaste(entries, Lookup));
            var reset = entries.Concat([new CodingAgentTreeSessionEntry { Type = "compaction", Id = "reset" },
                new CodingAgentTreeSessionEntry { Type = "message", Id = "new", Message = CodingAgentSessionStore.FromMessage(Message(new(50000, 1))) }]).ToArray();
            Assert.Equal(before, CodingAgentCacheStats.ComputeCacheWaste(reset, Lookup));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>【CodingAgent】【统计夹具】构造带原生模型身份的助手回复。</summary>
    /// <param name="usage">用量。</param><param name="timestamp">回复时间。</param><returns>助手消息。</returns>
    private static AssistantMessage Message(Usage usage, DateTimeOffset? timestamp = null) =>
        new([new TextContent("answer")]) { Usage = usage, Provider = "test", Model = "model", Timestamp = timestamp ?? Start, StopReason = StopReason.EndTurn };

    /// <summary>【CodingAgent】【统计夹具】通过实际会话序列化生成公开统计入口输入。</summary>
    /// <param name="id">条目标识。</param><param name="message">回复。</param><returns>原生会话条目。</returns>
    private static JsonElement Entry(string id, AssistantMessage message) => JsonSerializer.SerializeToElement(new CodingAgentTreeSessionEntry
    { Id = id, Type = "message", Timestamp = message.Timestamp ?? Start, Message = CodingAgentSessionStore.FromMessage(message) },
        CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);

    /// <summary>【CodingAgent】【统计夹具】返回固定缓存读取折扣，用于验证实际费用优先。</summary>
    /// <param name="provider">提供方。</param><param name="model">模型标识。</param><returns>固定价格模型。</returns>
    private static Model Lookup(string provider, string model) => new()
    { Provider = provider, Id = model, Name = model, Api = "test", Cost = new(100, 100, .25m, 100) };
}

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【缓存呈现】真实运行器记录损耗，终端按设置显示，session 命令始终保留统计。</summary>
    /// <param name="show">是否开启缓存费用提示。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheMisses_AppearInSessionStatsAndRespectLiveSettings(bool show)
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunner(
            new([new TextContent("first")]) { Provider = "synthetic", Model = "test", Usage = new(0, 1, 40000), StopReason = StopReason.EndTurn },
            new([new TextContent("second")]) { Provider = "synthetic", Model = "test", Usage = new(50000, 1, Cost: new(.2m, 0)), StopReason = StopReason.EndTurn });
        var settings = new CodingAgentSettingsStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "settings.json"));
        Assert.False(settings.GetShowCacheMissNotices());
        settings.SetShowCacheMissNotices(show);
        Assert.Equal(show, settings.GetShowCacheMissNotices());
        var terminal = new FakeTerminal();
        terminal.QueueInput("first"); terminal.QueueInput("second"); terminal.QueueInput("/session"); terminal.QueueInput("exit");
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, settingsStore: settings);
        await host.RunAsync();
        Assert.Equal(new CodingAgentCacheWaste(40000, .16m, 1), runner.GetSessionStats().CacheWaste);
        var output = terminal.FlattenedText();
        Assert.Equal(show, output.Contains("Cache miss: 40k tokens re-billed (~$0.16)", StringComparison.Ordinal));
        Assert.Contains("cache re-billed: $0.160 (40000 tokens, 1 miss)", output);
    }
}
