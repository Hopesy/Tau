// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentUsageAccountingTests
{
    /// <summary>【CodingAgent】【用量合并】未知、显式零与已报告的子计数保持区别，不重复加入主计数。</summary>
    /// <param name="left">首份可选计数。</param><param name="right">次份可选计数。</param><param name="expected">预期合计。</param>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, 0, 0)]
    [InlineData(0, null, 0)]
    [InlineData(2, 3, 5)]
    [InlineData(null, 4, 4)]
    public void CombineUsage_PreservesOptionalCounters(int? left, int? right, int? expected)
    {
        var first = new Usage(10, 20, 30, 40, Cost: new(.1m, .2m, .3m, .4m))
            { TotalTokens = 150, ReasoningTokens = left, CacheWrite1hTokens = left };
        var second = new Usage(1, 2, 3, 4, Cost: new(.01m, .02m, .03m, .04m))
            { TotalTokens = 25, ReasoningTokens = right, CacheWrite1hTokens = right };
        var actual = CodingAgentUsageAccounting.CombineUsage(first, second);
        Assert.Equal(11, actual.InputTokens);
        Assert.Equal(22, actual.OutputTokens);
        Assert.Equal(33, actual.CacheReadTokens);
        Assert.Equal(44, actual.CacheWriteTokens);
        Assert.Equal(175, actual.TotalTokens);
        Assert.Equal(expected, actual.ReasoningTokens);
        Assert.Equal(expected, actual.CacheWrite1hTokens);
        Assert.Equal(1.1m, actual.Cost?.Total);
        Assert.Equal(.44m, actual.Cost?.CacheWrite);
        Assert.Equal(left, first.ReasoningTokens);
        Assert.Equal(right, second.CacheWrite1hTokens);
    }

    /// <summary>【CodingAgent】【工具累计】未定价保持未知，空工具用量不产生额外记录，缺失总数从互斥分量计算。</summary>
    [Fact]
    public void CombineToolUsage_PreservesUnknownCostAndMissingUsage()
    {
        var usage = new Usage(10, 2, 3, 4);
        Assert.Null(RuntimeCodingAgentRunner.CombineToolUsage(null, null));
        Assert.Equal(usage, RuntimeCodingAgentRunner.CombineToolUsage(null, usage));
        Assert.Equal(usage, RuntimeCodingAgentRunner.CombineToolUsage(usage, null));
        var combined = RuntimeCodingAgentRunner.CombineToolUsage(usage, usage)!.Value;
        Assert.Equal(38, combined.TotalTokens);
        Assert.Null(combined.ReasoningTokens);
        Assert.Null(combined.CacheWrite1hTokens);
        Assert.Null(combined.Cost);
        var reportedZero = CodingAgentUsageAccounting.CombineUsage(usage, new(0, 0, Cost: new(0, 0)));
        Assert.NotNull(reportedZero.Cost);
        Assert.Equal(0, reportedZero.Cost.Value.Total);
    }

    /// <summary>【CodingAgent】【累计维度】累计超过单次整数范围仍正确，子计数和服务端总数不会被重复加入。</summary>
    [Fact]
    public void AddUsageToTotals_UsesDisjointCountersAndWideAccumulator()
    {
        var zero = CodingAgentUsageAccounting.CreateUsageTotals();
        var usage = new Usage(int.MaxValue, 2, 3, 4, Cost: new(.5m, 0))
            { ReasoningTokens = 2, CacheWrite1hTokens = 4, TotalTokens = 999 };
        var actual = CodingAgentUsageAccounting.AddUsageToTotals(CodingAgentUsageAccounting.AddUsageToTotals(zero, usage), usage);
        Assert.Equal(2L * int.MaxValue, actual.Input);
        Assert.Equal(4, actual.Output);
        Assert.Equal(6, actual.CacheRead);
        Assert.Equal(8, actual.CacheWrite);
        Assert.Equal(1m, actual.Cost);
        Assert.Equal(new(0, 0, 0, 0, 0), zero);
    }

    /// <summary>【CodingAgent】【费用归属】实际响应模型、独立用量和工具摘要分别归类，零计费但有 token 的分组保留。</summary>
    [Fact]
    public void Breakdown_GroupsActualModelsAndSummariesWithStableSorting()
    {
        var entries = new CodingAgentTreeSessionEntry[]
        {
            Message(new AssistantMessage([]) { Provider = "p", Model = "virtual", ResponseModel = "actual", Usage = new(10, 2, 3, 4, Cost: new(.2m, 0)) }),
            Standalone("usage", new(5, 0, Cost: new(.1m, 0)), "p", "actual"),
            Message(new ToolResultMessage("tool", []) { ToolName = "read", Usage = new(2, 1, Cost: new(.1m, 0)) }),
            Standalone("compaction", new(4, 1, Cost: new(.2m, 0))),
            Standalone("branch_summary", new(5, 1, Cost: new(.2m, 0))),
            Message(new AssistantMessage([]) { Provider = "p", Model = "equal", Usage = new(1, 0, Cost: new(.3m, 0)) }),
            Message(new AssistantMessage([]) { Provider = "q", Model = "actual", Usage = new(7, 0) }),
            Standalone("usage", new(0, 0), "ignored", "zero"),
            new() { Type = "custom", CustomType = "metadata", Usage = Standalone("usage", new(100, 0, Cost: new(9, 0))).Usage },
            Message(new UserMessage("not counted"))
        };
        var raw = entries.Select(entry => JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)).ToArray();
        var actual = CodingAgentUsageAccounting.GetUsageCostBreakdown(raw);
        Assert.Equal(new[]
        {
            new CodingAgentUsageCostBreakdownEntry("Tools/summaries", .5m, 14),
            new CodingAgentUsageCostBreakdownEntry("p/actual", .3m, 24),
            new CodingAgentUsageCostBreakdownEntry("p/equal", .3m, 1),
            new CodingAgentUsageCostBreakdownEntry("q/actual", 0, 7)
        }, actual);
        Assert.Equal(CodingAgentSessionUsageSummary.FromEntries(entries).Cost, actual.Sum(item => item.Cost));
        Assert.Equal(CodingAgentSessionUsageSummary.FromEntries(entries).Tokens.Total, actual.Sum(item => item.Tokens));
    }

    /// <summary>【CodingAgent】【费用归属】消息转换后仍保留 responseModel，只有费用没有 token 的收费记录仍然显示。</summary>
    [Fact]
    public void Breakdown_KeepsChargeOnlyRecordsAndFallbackMessages()
    {
        var messages = new ChatMessage[]
        {
            new AssistantMessage([]) { Provider = "p", Model = "selected", ResponseModel = "response", Usage = new(0, 0, Cost: new(.2m, 0)) },
            new AssistantMessage([]) { Provider = "p", Model = "selected", Usage = new(0, 0) },
            new ToolResultMessage("tool", [])
        };
        Assert.Equal(new("p/response", .2m, 0), Assert.Single(CodingAgentUsageAccounting.FromMessages(messages)));
        Assert.Empty(CodingAgentUsageAccounting.GetUsageCostBreakdown(Array.Empty<JsonElement>()));
    }

    /// <summary>【CodingAgent】【测试消息】构造原生消息条目。</summary>
    /// <param name="message">测试消息。</param><returns>消息条目。</returns>
    private static CodingAgentTreeSessionEntry Message(ChatMessage message) => new() { Type = "message", Message = CodingAgentSessionStore.FromMessage(message) };

    /// <summary>【CodingAgent】【测试用量】使用生产序列化构造独立或摘要用量。</summary>
    /// <param name="type">条目类型。</param><param name="usage">用量。</param><param name="provider">提供方。</param><param name="model">模型。</param>
    /// <returns>携带原生用量的条目。</returns>
    private static CodingAgentTreeSessionEntry Standalone(string type, Usage usage, string? provider = null, string? model = null)
    {
        var message = JsonSerializer.SerializeToElement(CodingAgentSessionStore.FromMessage(new AssistantMessage([]) { Usage = usage }),
            CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage);
        return new() { Type = type, Provider = provider, Model = model, Usage = message.GetProperty("usage").Clone() };
    }
}

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【费用展示】真实会话统计和 session 命令使用实际模型费用，JSONL 重读后保持一致。</summary>
    /// <param name="persistent">是否使用持久树会话。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsageBreakdown_ActualResponseModelAppearsInSessionCommand(bool persistent)
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunner(new AssistantMessage([new TextContent("answer")])
        { Provider = "synthetic", Model = "test", ResponseModel = "routed", Usage = new(10, 2, Cost: new(.12m, 0)) });
        CodingAgentTreeSessionController? tree = null;
        if (persistent)
        {
            tree = new(new CodingAgentTreeSessionStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "costs.jsonl")));
            fixture.Runtime.BindSession(runner, tree);
        }
        var terminal = new FakeTerminal();
        terminal.QueueInput("question"); terminal.QueueInput("/session"); terminal.QueueInput("exit");
        await new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, treeSessionController: tree).RunAsync();
        var expected = new CodingAgentUsageCostBreakdownEntry("synthetic/routed", .12m, 12);
        Assert.Equal(expected, Assert.Single(runner.GetSessionStats().UsageBreakdown));
        Assert.Contains("synthetic/routed: " + "$0.120 (12 tokens)", terminal.FlattenedText());
        if (tree is not null)
        {
            var entries = new CodingAgentTreeSessionStore(tree.Store.Path).ReadExtensionSnapshot().Entries;
            Assert.Equal(expected, Assert.Single(CodingAgentUsageAccounting.GetUsageCostBreakdown(entries)));
        }
    }
}
