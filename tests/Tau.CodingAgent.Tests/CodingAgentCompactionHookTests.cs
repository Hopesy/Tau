// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>扩展摘要绕过模型，完成钩子能读取已保存详情，前置钩子的消息不会被压缩恢复清空。</summary>
    /// <param name="persistent">是否写入 JSONL。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactionHooks_CustomResultCommitsOnceAndPreservesDelivery(bool persistent)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let observed;
              pi.on('session_before_compact',(e,ctx)=>{
                if (!(e.signal instanceof AbortSignal) || !(e.preparation.fileOps.read instanceof Set)) throw Error('native types');
                pi.sendMessage({customType:'before',content:'queued before compaction'}, {deliverAs:'nextTurn'});
                return {compaction:{summary:'hook summary',firstKeptEntryId:e.preparation.firstKeptEntryId,tokensBefore:123,
                  details:{marker:'custom'},usage:{input:4,output:2,totalTokens:6}}};
              });
              pi.on('session_compact',(e,ctx)=>{
                const saved=ctx.sessionManager.getEntries().find(entry=>entry.id===e.compactionEntry.id);
                observed={saved,fromExtension:e.fromExtension,reason:e.reason,willRetry:e.willRetry};
              });
              pi.registerCommand('observed',{handler:()=>JSON.stringify(observed)});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        CodingAgentTreeSessionController? tree = null;
        if (persistent)
        {
            tree = new(new CodingAgentTreeSessionStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "history.jsonl")), new(KeepRecentTokens: 0));
            fixture.Runtime.BindSession(runner, tree);
        }
        await foreach (var _ in runner.RunAsync("original")) { }
        var result = await runner.CompactAsync();
        Assert.Equal("hook summary", result.Summary);
        Assert.Equal(123, result.TokensBefore);
        Assert.Single(provider.Contexts);
        using var observed = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "observed", "").StatusMessage!);
        var saved = observed.RootElement.GetProperty("saved");
        Assert.Equal(result.StoredEntryId, saved.GetProperty("id").GetString());
        Assert.Equal("custom", saved.GetProperty("details").GetProperty("marker").GetString());
        Assert.Equal(6, saved.GetProperty("usage").GetProperty("totalTokens").GetInt32());
        Assert.True(saved.GetProperty("fromHook").GetBoolean());
        Assert.True(observed.RootElement.GetProperty("fromExtension").GetBoolean());
        Assert.Equal("manual", observed.RootElement.GetProperty("reason").GetString());
        Assert.False(observed.RootElement.GetProperty("willRetry").GetBoolean());
        if (tree is not null)
        {
            Assert.Equal(result.StoredEntryId, tree.RecordCompaction(runner, result));
            Assert.Single(tree.Store.ReadExtensionSnapshot().Entries, entry => entry.Type == "compaction");
        }
        await foreach (var _ in runner.RunAsync("next")) { }
        Assert.Single(runner.Messages.OfType<AgentCustomMessage>(), message => message.CustomType == "before");
    }

    /// <summary>取消结果停止后续模块，并通过失败钩子报告取消且保留完整历史。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CompactionHooks_CancelStopsChainAndReportsFailure()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let failure;
              pi.on('session_before_compact',()=>({cancel:true}));
              pi.on('session_compact_failed',e=>{failure=e;});
              pi.registerCommand('failure',{handler:()=>JSON.stringify(failure)});
            };
            """, """
            export default pi=>{
              let called=false;
              pi.on('session_before_compact',()=>{called=true;});
              pi.registerCommand('called',{handler:()=>String(called)});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        await foreach (var _ in runner.RunAsync("original")) { }
        var original = runner.Messages.ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.CompactAsync());
        Assert.Equal(original, runner.Messages);
        Assert.Single(provider.Contexts);
        Assert.Equal("false", fixture.Runtime.Invoke(fixture.Files[1], "called", "").StatusMessage);
        using var failure = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "failure", "").StatusMessage!);
        Assert.True(failure.RootElement.GetProperty("aborted").GetBoolean());
        Assert.False(failure.RootElement.GetProperty("fromExtension").GetBoolean());
        Assert.False(runner.IsCompacting);
    }

    /// <summary>准备数据跨模块传递，处理器错误隔离，默认生成器使用修改内容和文件集合。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CompactionHooks_DefaultSummaryUsesEditedPreparation()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.on('session_before_compact',e=>{
                e.preparation.isSplitTurn=false;
                e.preparation.turnPrefixMessages=[];
                e.preparation.messagesToSummarize=[{role:'user',content:'edited summary input',timestamp:1}];
                e.preparation.fileOps.read.add('read.txt');
                throw Error('isolated handler');
              });
            };
            """, """
            export default pi=>pi.on('session_before_compact',e=>{
              if (!e.preparation.fileOps.read.has('read.txt')) throw Error('lost edits');
              e.preparation.fileOps.edited.add('changed.txt');
            });
            """);
        var (runner, provider) = fixture.CreateRunner(
            new AssistantMessage([new TextContent("reply")]),
            new AssistantMessage([new TextContent("summary")]) { StopReason = StopReason.EndTurn, Usage = new(11, 7) { CacheReadTokens = 3, TotalTokens = 21 } });
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        await foreach (var _ in runner.RunAsync("original secret")) { }
        var result = await runner.CompactAsync("retain decisions");
        var summaryInput = string.Join("\n", provider.Contexts[1].Messages.OfType<UserMessage>().SelectMany(message => message.Content.OfType<TextContent>()).Select(block => block.Text));
        Assert.Contains("edited summary input", summaryInput);
        Assert.Contains("retain decisions", summaryInput);
        Assert.DoesNotContain("original secret", summaryInput);
        Assert.Equal(21, result.Usage!.Value.GetProperty("totalTokens").GetInt32());
        Assert.Equal("read.txt", result.Details!.Value.GetProperty("readFiles")[0].GetString());
        Assert.Equal("changed.txt", result.Details.Value.GetProperty("modifiedFiles")[0].GetString());
        Assert.Contains("<modified-files>", result.Summary);
    }

    /// <summary>不足保留窗口的会话不发送摘要请求，也不生成无内容的摘要检查点。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CompactionPreparation_SmallConversationDoesNotSummarize()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner();
        await foreach (var _ in runner.RunAsync("short")) { }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompactAsync());
        Assert.Contains("Nothing to compact", error.Message);
        Assert.Single(provider.Contexts);
        Assert.DoesNotContain(runner.Messages, CodingAgentCompactionMessages.IsSummaryMessage);
    }
}

public sealed class CodingAgentProjectedCompactionTests
{
    /// <summary>上下文替换进入摘要，省略消息和系统声明不会泄漏到摘要输入。</summary>
    [Fact]
    public void Prepare_UsesVisibleEditsAndStableBoundary()
    {
        var entries = new List<CodingAgentTreeSessionEntry>
        {
            Message("s", new SystemMessage("system secret")), Message("u", new UserMessage("original secret")),
            Message("a", new AssistantMessage([new TextContent("omitted secret")])),
            new() { Type = "context_edit", Id = "edit-u", TargetId = "u", Replacement = Json("""{"content":"edited input"}""") },
            new() { Type = "context_edit", Id = "edit-a", TargetId = "a", Replacement = Json("null") },
            Message("recent", new UserMessage("recent turn")), Message("answer", new AssistantMessage([new TextContent("recent answer")]))
        };
        var prepared = Assert.IsType<AgentCompactionPreparation>(CodingAgentProjectedCompaction.Prepare(entries, new(KeepRecentTokens: 0)));
        Assert.Equal("answer", prepared.FirstKeptEntryId);
        Assert.Equal("edited input", Assert.Single(Assert.IsType<UserMessage>(Assert.Single(prepared.MessagesToSummarize)).Content.OfType<TextContent>()).Text);
        Assert.Equal("recent turn", Assert.Single(Assert.IsType<UserMessage>(Assert.Single(prepared.TurnPrefixMessages)).Content.OfType<TextContent>()).Text);
        Assert.True(prepared.IsSplitTurn);
    }

    /// <summary>重复压缩只处理上次保留尾部，旧摘要与文件记录完整传递。</summary>
    [Fact]
    public void Prepare_RespectsPriorCompactionAndRetainedTail()
    {
        var entries = new List<CodingAgentTreeSessionEntry>
        {
            Message("old", new UserMessage("discarded")), Message("kept", new UserMessage("kept input")),
            new() { Type = "compaction", Id = "previous", Summary = "previous summary", FirstKeptEntryId = "kept", Details = Json("""{"readFiles":["read.txt"],"modifiedFiles":["edit.txt"]}""") },
            Message("recent", new UserMessage("recent input")), Message("answer", new AssistantMessage([new TextContent("answer")]))
        };
        var prepared = Assert.IsType<AgentCompactionPreparation>(CodingAgentProjectedCompaction.Prepare(entries, new(KeepRecentTokens: 0)));
        Assert.Equal("previous summary", prepared.PreviousSummary);
        Assert.Single(prepared.MessagesToSummarize);
        Assert.Equal("kept input", Assert.Single(Assert.IsType<UserMessage>(prepared.MessagesToSummarize[0]).Content.OfType<TextContent>()).Text);
        Assert.Contains("read.txt", prepared.FileOperations.Read);
        Assert.Contains("edit.txt", prepared.FileOperations.Edited);
        Assert.Null(CodingAgentProjectedCompaction.Prepare(entries.Take(3).ToArray(), new(KeepRecentTokens: 0)));
    }

    /// <summary>被省略的失败尝试允许切分最后输入，普通元数据不允许越过尚未发送的输入。</summary>
    /// <param name="omittedAttempt">是否存在被省略的助手尝试。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepare_AdvancesOnlyAcrossRecoveryOmissions(bool omittedAttempt)
    {
        var entries = new List<CodingAgentTreeSessionEntry> { Message("user", new UserMessage("large input")) };
        if (omittedAttempt)
        {
            entries.Add(Message("failed", new AssistantMessage([]) { StopReason = StopReason.Error }));
            entries.Add(new() { Type = "context_edit", Id = "omit", TargetId = "failed", Replacement = Json("null") });
        }
        else entries.Add(new() { Type = "custom", Id = "metadata", CustomType = "state" });
        var prepared = CodingAgentProjectedCompaction.Prepare(entries, new(KeepRecentTokens: 0));
        if (omittedAttempt) { Assert.NotNull(prepared); Assert.Equal("failed", prepared.FirstKeptEntryId); Assert.Single(prepared.TurnPrefixMessages); }
        else Assert.Null(prepared);
    }

    /// <summary>构造带稳定标识的原始消息条目。</summary>
    /// <param name="id">条目标识。</param>
    /// <param name="message">原始消息。</param>
    /// <returns>会话消息条目。</returns>
    private static CodingAgentTreeSessionEntry Message(string id, ChatMessage message) => new() { Type = "message", Id = id, Message = CodingAgentSessionStore.FromMessage(message) };

    /// <summary>解析不依赖反射的测试 JSON。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>独立 JSON 元素。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
