// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【历史恢复】恢复原文、工具结果与有序缓存费用，不泄漏系统声明、隐藏消息或模型上下文编辑。</summary>
    /// <param name="showCosts">是否显示缓存费用。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryRendering_RestoresOriginalEntriesAndCacheNotices(bool showCosts)
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner();
        var directory = Path.GetDirectoryName(fixture.Files[0])!;
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(directory, "history.jsonl")));
        fixture.Runtime.BindSession(runner, tree);
        runner.AppendMessage(new UserMessage("original visible input"));
        runner.AppendMessage(new AssistantMessage([new ThinkingContent("hidden reasoning"),
            new TextContent("checking file"), new ToolCallContent("history-call", "read", """{"path":"file.txt"}""")])
        { Provider = "synthetic", Model = "test", Usage = new(0, 1, 30000), StopReason = StopReason.ToolUse });
        runner.AppendMessage(new ToolResultMessage("history-call", [new TextContent("historical file contents")]) { ToolName = "read" });
        tree.SyncFromRunner(runner);
        tree.Store.AppendUsage("cache_warm", "synthetic", "test", new(0, 1, 30000, Cost: new(0, 0, .003m)), "extension override");
        runner.AppendMessage(new AssistantMessage([new TextContent("final historical answer")])
        { Provider = "synthetic", Model = "test", Usage = new(40000, 1, Cost: new(.12m, 0)), StopReason = StopReason.EndTurn });
        runner.AppendMessage(new AgentCustomMessage("hidden", "private extension context", false));
        runner.AppendMessage(new AgentCustomMessage("shown", "visible extension content", true));
        tree.SyncFromRunner(runner);
        var userId = tree.Store.ReadExtensionSnapshot().Entries.First(entry => entry.Message?.Role == "user").Id;
        using var replacement = JsonDocument.Parse("""{"content":"model-only edited input"}""");
        tree.Store.AppendContextEdit(userId, replacement.RootElement);
        runner.RestoreSession(tree.LoadSnapshot().ToFlatSnapshot());
        var settings = new CodingAgentSettingsStore(Path.Combine(directory, "settings.json"));
        settings.SetShowCacheMissNotices(showCosts);
        var terminal = new FakeTerminal(); terminal.QueueInput("exit");
        var ui = new InteractiveConsoleSession(terminal);
        var host = new CodingAgentHost(ui, runner, settingsStore: settings, treeSessionController: tree, hideThinkingBlock: true);
        await host.RunAsync();
        var output = terminal.FlattenedText();
        Assert.Contains("original visible input", output);
        Assert.DoesNotContain("model-only edited input", output);
        Assert.DoesNotContain("hidden reasoning", output);
        Assert.DoesNotContain("private extension context", output);
        Assert.Contains("visible extension content", output);
        Assert.Contains("Thinking...", output);
        var tool = Assert.Single(ui.Transcript, item => item.Key == "history-call");
        Assert.Contains("historical file contents", tool.Text);
        Assert.Equal(showCosts, output.Contains("Cache warmed (extension override): $0.003", StringComparison.Ordinal));
        Assert.Equal(showCosts, output.Contains("Cache miss: 30k tokens re-billed (~$0.09)", StringComparison.Ordinal));
        if (showCosts)
        {
            Assert.True(output.IndexOf("historical file contents", StringComparison.Ordinal) < output.IndexOf("Cache warmed", StringComparison.Ordinal));
            Assert.True(output.IndexOf("Cache warmed", StringComparison.Ordinal) < output.IndexOf("final historical answer", StringComparison.Ordinal));
        }
        Assert.Equal("model-only edited input", Assert.IsType<TextContent>(runner.Messages.OfType<UserMessage>().Single().Content.Single()).Text);
        Assert.Empty(provider.Contexts);
    }

    /// <summary>【CodingAgent】【压缩窗口】恢复时省略旧前缀，保留摘要、保留尾部和摘要计费提示。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task HistoryRendering_UsesCompactionWindowAndSummaryUsage()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunner();
        var directory = Path.GetDirectoryName(fixture.Files[0])!;
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(directory, "compacted.jsonl")));
        fixture.Runtime.BindSession(runner, tree);
        runner.AppendMessage(new UserMessage("removed prefix"));
        runner.AppendMessage(new AssistantMessage([new TextContent("removed answer")]));
        runner.AppendMessage(new UserMessage("kept tail"));
        tree.SyncFromRunner(runner);
        var kept = tree.Store.ReadExtensionSnapshot().Entries.Last(entry => entry.Message?.Role == "user").Id;
        using var usage = JsonDocument.Parse("""{"input":12000,"output":1000,"cacheRead":0,"cacheWrite":0,"cost":{"input":0.02,"output":0.01,"total":0.03}}""");
        tree.Store.AppendCompaction("saved summary", kept, 45000, usage: usage.RootElement);
        runner.RestoreSession(tree.LoadSnapshot().ToFlatSnapshot());
        var settings = new CodingAgentSettingsStore(Path.Combine(directory, "settings.json"));
        settings.SetShowCacheMissNotices(true);
        var terminal = new FakeTerminal(); terminal.QueueInput("exit");
        var ui = new InteractiveConsoleSession(terminal);
        await new CodingAgentHost(ui, runner, treeSessionController: tree, settingsStore: settings).RunAsync();
        Assert.DoesNotContain(ui.Transcript, entry => entry.Text.Contains("removed prefix", StringComparison.Ordinal));
        Assert.DoesNotContain(ui.Transcript, entry => entry.Text.Contains("removed answer", StringComparison.Ordinal));
        Assert.Contains(ui.Transcript, entry => entry.Text.Contains("Compacted from 45,000 tokens", StringComparison.Ordinal));
        Assert.Contains(ui.Transcript, entry => entry.Text == "Compaction: 13k tokens billed (~$0.03)");
        Assert.Contains(ui.Transcript, entry => entry.Text == "kept tail");
    }

    /// <summary>【CodingAgent】【会话替换】新会话和恢复分支会重建显示，旧工具、费用和正文不会留在当前画面。</summary>
    /// <param name="reset">是否通过 new 命令清空，否则通过扩展恢复上下文。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryRendering_ReplacesVisibleTranscriptAfterSessionChange(bool reset)
    {
        using var fixture = new Fixture("""
            export default pi=>pi.registerCommand('restore-history',{handler:async(_,ctx)=>{
              await ctx.newSession({setup:sm=>sm.appendMessage({role:'user',content:'new visible message',timestamp:Date.now()})});
            }});
            """);
        var (runner, _) = fixture.CreateRunner();
        runner.AppendMessage(new UserMessage("old visible message"));
        var terminal = new FakeTerminal();
        terminal.QueueInput(reset ? "/new" : "/restore-history"); terminal.QueueInput("exit");
        var ui = new InteractiveConsoleSession(terminal);
        var commands = fixture.BindCommands(runner);
        await new CodingAgentHost(ui, runner, extensionCommandStore: commands).RunAsync();
        Assert.DoesNotContain(ui.Transcript, item => item.Text.Contains("old visible message", StringComparison.Ordinal));
        if (!reset) Assert.Contains(ui.Transcript, item => item.Text == "new visible message");
        Assert.Contains("old visible message", terminal.FlattenedText());
    }
}

public sealed class CodingAgentHistoryUsageTests
{
    /// <summary>【CodingAgent】【未知价格】缺失价格或只有旧费用分量时，预热历史仍可显示。</summary>
    /// <param name="json">用量条目。</param><param name="expected">显示费用。</param>
    [Theory]
    [InlineData("""{"usage":{"input":1}}""", "Cache warmed: $0.000")]
    [InlineData("""{"usage":{"cost":{"input":0.001,"cacheRead":0.002}}}""", "Cache warmed: $0.003")]
    public void CacheWarmHistoryAcceptsUnknownAndLegacyCosts(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, CodingAgentCacheWarmingFormatter.FormatUsage(document.RootElement));
    }
}
