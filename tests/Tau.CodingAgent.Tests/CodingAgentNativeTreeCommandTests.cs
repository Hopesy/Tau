// 作者：xxx
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>【CodingAgent】【导航摘要中断】取消信号送达扩展，未提交的摘要不改变树，随后可以立即重新导航。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task NativeNavigation_AbortDiscardsSummaryAndReleasesTransaction()
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';import path from 'node:path';
            export default pi=>pi.on('session_before_tree',async(event,ctx)=>{
              if(!event.preparation.userWantsSummary)return;
              fs.writeFileSync(path.join(ctx.cwd,'summary-entered'),'');
              await new Promise(resolve=>event.signal.addEventListener('abort',resolve,{once:true}));
              return {summary:{summary:'must not commit'}};
            });
            """);
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!; var store = new CodingAgentTreeSessionStore(Path.Combine(root, "tree.jsonl"), root);
        var tree = new CodingAgentTreeSessionController(store); commands.BindSession(runner, tree);
        runner.AppendMessage(new UserMessage("target")); runner.AppendMessage(new AssistantMessage([new TextContent("answer")])); tree.SyncFromRunner(runner);
        var snapshot = store.ReadExtensionSnapshot(); var target = snapshot.Entries.First(entry => entry.Message?.Role == "user").Id;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var navigating = runner.NavigateTreeAsync(target, new(Summarize: true), deadline.Token);
        while (!File.Exists(Path.Combine(root, "summary-entered"))) await Task.Delay(5, deadline.Token);
        Assert.True(runner.IsCompacting); runner.Abort(); var cancelled = await navigating.WaitAsync(deadline.Token);
        Assert.True(cancelled.Aborted); Assert.True(cancelled.Cancelled); Assert.False(runner.IsCompacting);
        Assert.Equal(snapshot.LeafId, store.ReadExtensionSnapshot().LeafId);
        Assert.DoesNotContain(store.ReadExtensionSnapshot().Entries, entry => entry.Type == "branch_summary");
        var completed = await runner.NavigateTreeAsync(target, cancellationToken: deadline.Token);
        Assert.False(completed.Cancelled); Assert.Equal("target", completed.EditorText); Assert.Empty(runner.Messages.OfType<UserMessage>());
        Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【交互导航扩展】命令入口发送原始目标，遵守扩展取消或摘要接管，保留同一会话及历史。</summary>
    /// <param name="cancel">扩展是否取消导航。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TreeCommand_UsesNativeLifecycleAndSummaryTransaction(bool cancel)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let before=false,after=false;
              pi.on('session_before_tree',(event,ctx)=>{
                const target=ctx.sessionManager.getEntry(event.preparation.targetId);
                if(target.message?.role!=='user'||!event.preparation.userWantsSummary)throw Error('wrong target');
                before=true;
                return __RESULT__;
              });
              pi.on('session_tree',(event,ctx)=>{
                if(!event.fromExtension||event.summaryEntry.summary!=='hook summary'||!ctx.sessionManager.getEntry(event.summaryEntry.id))throw Error('not committed');
                after=true;
              });
              pi.registerCommand('verify',{handler:async()=>String(before)+'|'+String(after)});
            };
            """.Replace("__RESULT__", cancel ? "{cancel:true}" : "{summary:{summary:'hook summary',details:{custom:42},usage:{totalTokens:6}},label:'hook label'}", StringComparison.Ordinal));
        var (runner, provider) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!; var store = new CodingAgentTreeSessionStore(Path.Combine(root, "tree.jsonl"), root);
        var tree = new CodingAgentTreeSessionController(store); commands.BindSession(runner, tree);
        runner.AppendMessage(new UserMessage("first")); runner.AppendMessage(new AssistantMessage([new TextContent("answer")])); tree.SyncFromRunner(runner);
        var originalId = runner.SessionId; var originalLeaf = store.ReadExtensionSnapshot().LeafId; var draft = "kept draft";
        var router = new CodingAgentCommandRouter(runner, treeSessionController: tree, extensionCommandStore: commands,
            treeNavigator: (items, _, _) => Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(items.First(item => item.MessageRole == "user").EntryId, 0, 1)),
            treeNavigationPrompt: (_, _) => Task.FromResult(CodingAgentTreeNavigationDecision.SummarizeWith()),
            inputDraftSetter: value => draft = value ?? "", inputDraftGetter: () => draft);
        var result = await router.TryHandleAsync("/tree --interactive");
        Assert.False(result.IsError, result.Message); Assert.Equal(originalId, runner.SessionId); Assert.Equal("kept draft", draft);
        var verified = fixture.Runtime.Invoke(fixture.Files[0], "verify", ""); Assert.True(verified.Success, verified.Error);
        Assert.Equal(cancel ? "true|false" : "true|true", verified.StatusMessage); Assert.Empty(provider.Contexts); Assert.False(runner.IsCompacting);
        if (cancel) { Assert.Equal("Navigation cancelled", result.Message); Assert.Equal(originalLeaf, store.ReadExtensionSnapshot().LeafId); }
        else
        {
            Assert.DoesNotContain(runner.Messages.OfType<UserMessage>(), message => message.Content.OfType<TextContent>().Any(text => text.Text == "first"));
            var entry = Assert.Single(store.ReadExtensionSnapshot().Entries, entry => entry.Type == "branch_summary");
            Assert.Equal("hook summary", entry.Summary); Assert.Equal(originalLeaf, entry.FromId); Assert.True(entry.FromHook);
            Assert.Equal(42, entry.Details!.Value.GetProperty("custom").GetInt32()); Assert.Equal(6, entry.Usage!.Value.GetProperty("totalTokens").GetInt32());
            Assert.Equal("hook label", tree.GetLabel(entry.Id));
        }
    }

    /// <summary>【CodingAgent】【导航草稿】用户节点回退父节点，空编辑器恢复选中文本，已有草稿保持完整。</summary>
    /// <param name="existing">原有草稿。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("")] [InlineData("existing")]
    public async Task TreeCommand_OnlyRestoresSelectedTextIntoEmptyDraft(string existing)
    {
        using var fixture = new Fixture("export default ()=>{};");
        var (runner, _) = fixture.CreateRunner(); using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!; var tree = new CodingAgentTreeSessionController(new(Path.Combine(root, "tree.jsonl"), root));
        commands.BindSession(runner, tree); runner.AppendMessage(new UserMessage("selected text")); runner.AppendMessage(new AssistantMessage([new TextContent("answer")])); tree.SyncFromRunner(runner);
        var draft = existing;
        var router = new CodingAgentCommandRouter(runner, treeSessionController: tree, extensionCommandStore: commands,
            treeNavigator: (items, _, _) => Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(items.First(item => item.MessageRole == "user").EntryId, 0, 1)),
            inputDraftSetter: value => draft = value ?? "", inputDraftGetter: () => draft);
        var result = await router.TryHandleAsync("/tree --interactive");
        Assert.False(result.IsError, result.Message); Assert.Equal(existing.Length == 0 ? "selected text" : existing, draft);
        Assert.Empty(runner.Messages.OfType<UserMessage>());
    }
}
