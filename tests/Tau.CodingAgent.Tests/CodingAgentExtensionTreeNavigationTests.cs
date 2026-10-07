// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>树导航保留同一会话全部历史，用户输入返回编辑器，原始父链和扩展状态仍可查询。</summary>
    /// <param name="persistent">是否使用持久化树。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigation_UserTargetMovesToParentAndPreservesOtherBranch(bool persistent)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let event;
              pi.on('session_tree',e=>{event=e;});
              pi.registerCommand('navigate',{handler:async(_,ctx)=>{
                const id=ctx.sessionManager.getSessionId(), entries=ctx.sessionManager.getEntries();
                const target=entries.find(e=>e.type==='message'&&e.message.role==='user'&&JSON.stringify(e.message.content).includes('second'));
                const result=await ctx.navigateTree(target.id);
                if(result.cancelled||result.editorText!=='second'||ctx.sessionManager.getSessionId()!==id)throw Error('navigation failed');
                if(ctx.sessionManager.getLeafId()!==target.parentId||event.newLeafId!==target.parentId)throw Error('wrong leaf');
                if(ctx.sessionManager.getEntries().length!==entries.length)throw Error('navigation added entries');
                if(ctx.sessionManager.buildSessionProjection().messages.some(m=>m.role==='user'&&JSON.stringify(m.content).includes('second')))throw Error('wrong projection');
                pi.appendEntry('new-branch',{value:true});
                if(ctx.sessionManager.getLeafEntry().parentId!==target.parentId)throw Error('wrong parent');
                return 'navigated';
              }});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        if (persistent)
        {
            var root = Path.GetDirectoryName(fixture.Files[0])!;
            commands.BindSession(runner, new(new CodingAgentTreeSessionStore(Path.Combine(root, "tree.jsonl"), root)));
        }
        await commands.EnsureSessionStartedAsync();
        await foreach (var ignored in runner.RunAsync("first")) { }
        await foreach (var ignored in runner.RunAsync("second")) { }
        var result = fixture.Runtime.Invoke(fixture.Files[0], "navigate", "");
        Assert.True(result.Success, result.Error);
        Assert.Equal("navigated", result.StatusMessage);
        Assert.Equal("first", Assert.Single(runner.Messages.OfType<UserMessage>()).Content.OfType<TextContent>().Single().Text);
    }

    /// <summary>取消导航没有副作用；扩展摘要保存真实旧叶节点、详情、用量和摘要标签，事件读取提交后的树。</summary>
    /// <param name="persistent">是否使用持久化树。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigation_CancellationAndExtensionSummary(bool persistent)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let cancel=true, after;
              pi.on('session_before_tree',e=>{
                if(!e.signal||!Array.isArray(e.preparation.entriesToSummarize))throw Error('invalid preparation');
                if(cancel)return {cancel:true};
                return {summary:{summary:'kept summary',details:{custom:42},usage:{input:4,output:2,totalTokens:6}},label:'summary label'};
              });
              pi.on('session_tree',(e,ctx)=>{after=e;if(!ctx.sessionManager.getEntry(e.summaryEntry.id))throw Error('not committed');});
              pi.registerCommand('navigate',{handler:async(_,ctx)=>{
                const target=ctx.sessionManager.getEntries().find(e=>e.type==='message'&&e.message.role==='user');
                const leaf=ctx.sessionManager.getLeafId();
                if(!(await ctx.navigateTree(target.id,{summarize:true})).cancelled||ctx.sessionManager.getLeafId()!==leaf)throw Error('cancel changed leaf');
                cancel=false;
                const result=await ctx.navigateTree(target.id,{summarize:true});
                const entry=result.summaryEntry;
                if(entry.fromId!==leaf||entry.details.custom!==42||entry.usage.totalTokens!==6||!after.fromExtension)throw Error('summary metadata missing');
                if(ctx.sessionManager.getLabel(entry.id)!=='summary label')throw Error('label missing');
                return entry.summary;
              }});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        if (persistent)
        {
            var root = Path.GetDirectoryName(fixture.Files[0])!;
            commands.BindSession(runner, new(new CodingAgentTreeSessionStore(Path.Combine(root, "tree.jsonl"), root)));
        }
        await commands.EnsureSessionStartedAsync();
        await foreach (var ignored in runner.RunAsync("first")) { }
        var result = fixture.Runtime.Invoke(fixture.Files[0], "navigate", "");
        Assert.True(result.Success, result.Error);
        Assert.Equal("kept summary", result.StatusMessage);
        Assert.Single(provider.Contexts);
        Assert.False(runner.IsCompacting);
    }
}
