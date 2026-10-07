// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>压缩保留全历史计费总量，上下文在收到压缩后的有效模型用量之前保持未知。</summary>
    /// <param name="persistent">是否使用 JSONL 持久化。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionAccounting_RetainsUsageAcrossCompaction(bool persistent)
    {
        using var fixture = new Fixture("export default pi=>pi.registerCommand('usage',{handler:(_,ctx)=>JSON.stringify(ctx.getContextUsage())});");
        var (runner, _) = fixture.CreateRunnerWithWindow(1000,
            new([new TextContent("original answer")]) { Usage = new(100, 20, Cost: new(0.1m, 0.2m, 0, 0)) { TotalTokens = 400 } },
            new([new TextContent("summary")]) { Usage = new(10, 3, Cost: new(0.01m, 0.03m, 0, 0)) },
            new([new TextContent("next answer")]) { Usage = new(30, 5) });
        runner.CompactionSettings = new(Enabled: false, KeepRecentTokens: 0);
        CodingAgentTreeSessionController? tree = null;
        if (persistent)
        {
            tree = new(new CodingAgentTreeSessionStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "session.jsonl")), new(KeepRecentTokens: 0));
            fixture.Runtime.BindSession(runner, tree);
        }
        await foreach (var ignored in runner.RunAsync("first")) { }
        var before = runner.GetSessionStats();
        Assert.Equal(400, before.ContextUsage!.Tokens);
        var result = await runner.CompactAsync();
        var after = runner.GetSessionStats();
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.TotalMessages, after.TotalMessages);
        Assert.Equal(110, after.Tokens.Input);
        Assert.Equal(23, after.Tokens.Output);
        Assert.Equal(0.34m, after.Cost);
        Assert.Equal(2, after.CostRecords);
        Assert.Null(after.ContextUsage!.Tokens);
        Assert.Null(after.ContextUsage.Percent);
        Assert.True(after.EstimatedTokens < 400);
        Assert.NotNull(result.Usage);
        using var ctx = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "usage", "").StatusMessage!);
        Assert.Equal(JsonValueKind.Null, ctx.RootElement.GetProperty("tokens").ValueKind);
        Assert.Equal(1000, ctx.RootElement.GetProperty("contextWindow").GetInt32());
        await foreach (var ignored in runner.RunAsync("next")) { }
        var latest = runner.GetSessionStats();
        Assert.Equal(35, latest.ContextUsage!.Tokens);
        Assert.Equal(3.5, latest.ContextUsage.Percent);
        Assert.Equal(140, latest.Tokens.Input);
        Assert.Equal(2, latest.AssistantMessages);
        if (tree is not null)
        {
            var reloaded = new CodingAgentTreeSessionStore(tree.Store.Path);
            Assert.Equal(140, reloaded.GetSessionUsageSummary().Tokens.Input);
            Assert.Equal(0.34m, reloaded.GetSessionUsageSummary().Cost);
        }
    }

    /// <summary>独立用量使用原生字段持久化，不进入上下文或改选模型；全会话统计包含其他分支费用。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task SessionAccounting_IncludesStandaloneToolAndOtherBranchUsage()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunnerWithWindow(1000);
        var path = Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "session.jsonl");
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(path));
        fixture.Runtime.BindSession(runner, tree);
        await foreach (var ignored in runner.RunAsync("original")) { }
        var baseline = runner.GetSessionStats();
        var leaf = tree.Store.ReadExtensionSnapshot().LeafId;
        var id = tree.Store.AppendUsage("cache_warm", "different", "other-model", new(5, 1, Cost: new(0.01m, 0, 0, 0)), "prewarm");
        tree.Store.AppendMessages([new ToolResultMessage("call", [new TextContent("tool")]) { ToolName = "read", Usage = new(7, 2, Cost: new(0.02m, 0, 0, 0)) }], 0);
        var branch = tree.BranchTo(leaf);
        runner.RestoreSession(branch.ToFlatSnapshot());
        var stats = runner.GetSessionStats();
        Assert.Equal(12, stats.Tokens.Input);
        Assert.Equal(3, stats.Tokens.Output);
        Assert.Equal(0.03m, stats.Cost);
        Assert.Equal(baseline.TotalMessages + 1, stats.TotalMessages);
        Assert.Equal(1, stats.ToolResultMessages);
        Assert.Equal("test", stats.Model);
        Assert.Equal(0, tree.GetCurrentBranchUsageSummary().Tokens.Input);
        Assert.Equal(12, tree.GetSessionUsageSummary().Tokens.Input);
        Assert.Equal(baseline.EstimatedTokens, stats.EstimatedTokens);
        var stored = File.ReadLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var usage = Assert.Single(stored, entry => entry.RootElement.TryGetProperty("id", out var entryId) && entryId.GetString() == id).RootElement;
            Assert.Equal("usage", usage.GetProperty("type").GetString());
            Assert.Equal("other-model", usage.GetProperty("model").GetString());
            Assert.False(usage.TryGetProperty("modelId", out _));
            Assert.Equal("cache_warm", usage.GetProperty("kind").GetString());
            Assert.Equal("prewarm", usage.GetProperty("note").GetString());
            Assert.Equal(5, usage.GetProperty("usage").GetProperty("input").GetInt32());
        }
        finally { foreach (var document in stored) document.Dispose(); }
    }

    /// <summary>分支摘要将提供方用量带到持久条目，统计同时保留离开分支的历史用量。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task SessionAccounting_BranchSummaryPersistsUsage()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunner(new([new TextContent("answer")]) { Usage = new(20, 4) },
            new([new TextContent("branch summary")]) { Usage = new(10, 2) });
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "session.jsonl")));
        fixture.Runtime.BindSession(runner, tree);
        await foreach (var ignored in runner.RunAsync("original")) { }
        tree.SyncFromRunner(runner);
        var summary = await runner.SummarizeBranchAsync(runner.Messages);
        var branch = tree.SummarizeCurrentBranchToRoot(runner, summary);
        runner.RestoreSession(branch.ToFlatSnapshot());
        Assert.Equal(30, runner.GetSessionStats().Tokens.Input);
        Assert.Equal(6, runner.GetSessionStats().Tokens.Output);
        Assert.Equal(10, Assert.Single(tree.Store.ReadExtensionSnapshot().Entries, entry => entry.Type == "branch_summary").Usage!.Value.GetProperty("input").GetInt32());
    }
}

public sealed class CodingAgentProjectedUsageTests
{
    /// <summary>后续编辑使先前 usage 失效，新模型响应恢复可用 usage；零总量回退各分量。</summary>
    [Fact]
    public void EstimateUsage_InvalidatesEditedContextAndTrustsNewResponse()
    {
        var entries = new List<CodingAgentTreeSessionEntry>
        {
            new() { Type = "message", Id = "u", Message = CodingAgentSessionStore.FromMessage(new UserMessage(new string('x', 400))) },
            new() { Type = "message", Id = "a", Message = CodingAgentSessionStore.FromMessage(new AssistantMessage([new TextContent("answer")]) { Usage = new(1000, 20) { TotalTokens = 4000 } }) }
        };
        Assert.Equal(4000, CodingAgentProjectedCompaction.EstimateContextUsage(CodingAgentTreeSessionStore.ProjectBranch(entries), entries).Tokens);
        using var edit = JsonDocument.Parse("""{"content":"short"}""");
        entries.Add(new() { Type = "context_edit", Id = "edit", TargetId = "u", Replacement = edit.RootElement.Clone() });
        var estimated = CodingAgentProjectedCompaction.EstimateContextUsage(CodingAgentTreeSessionStore.ProjectBranch(entries), entries);
        Assert.Null(estimated.LastUsageIndex);
        Assert.Equal(4, estimated.Tokens);
        entries.Add(new() { Type = "message", Id = "new", Message = CodingAgentSessionStore.FromMessage(new AssistantMessage([new TextContent("latest")]) { Usage = new(20, 3) { TotalTokens = 0 } }) });
        var latest = CodingAgentProjectedCompaction.EstimateContextUsage(CodingAgentTreeSessionStore.ProjectBranch(entries), entries);
        Assert.NotNull(latest.LastUsageIndex);
        Assert.Equal(23, latest.Tokens);
    }
}
