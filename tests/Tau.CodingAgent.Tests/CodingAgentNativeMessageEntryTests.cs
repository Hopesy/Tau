// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>扩展消息写成原生 custom_message，投影保留显示标志、图片和详情，导航能恢复自定义输入。</summary>
    /// <param name="persistent">是否使用持久化文件。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCustomMessage_PersistsProjectsAndNavigates(bool persistent)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerCommand('send',{handler:()=>pi.sendMessage({customType:'status',content:[{type:'text',text:'checkpoint text'},{type:'image',data:'YWJj',mimeType:'image/png'}],display:false,details:{private:'metadata'}})});
              pi.registerCommand('read',{handler:async(_,ctx)=>{
                const raw=ctx.sessionManager.getEntries().find(e=>e.type==='custom_message');
                if(!raw||raw.message||raw.customType!=='status'||raw.display!==false||raw.details.private!=='metadata'||raw.content[1].data!=='YWJj')throw Error('invalid native entry');
                const projected=ctx.sessionManager.buildSessionProjection().messages.find(m=>m.role==='custom');
                if(projected.customType!=='status'||projected.display!==false||projected.details.private!=='metadata')throw Error('invalid projection');
                return raw.id;
              }});
              pi.registerCommand('navigate',{handler:async(id,ctx)=>{
                const result=await ctx.navigateTree(id);
                if(result.editorText!=='checkpoint text')throw Error('missing custom editor text');
                if(ctx.sessionManager.buildSessionProjection().messages.some(m=>m.role==='custom'))throw Error('custom was not excluded');
                return 'navigated';
              }});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var tree = persistent ? new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(root, "session.jsonl"), root)) : null;
        if (tree is not null) commands.BindSession(runner, tree);
        await commands.EnsureSessionStartedAsync();
        await foreach (var ignored in runner.RunAsync("/send")) { }
        Assert.Single(runner.Messages.OfType<AgentCustomMessage>());
        Assert.Empty(provider.Contexts);
        var read = fixture.Runtime.Invoke(fixture.Files[0], "read", "");
        Assert.True(read.Success, read.Error);
        if (tree is not null)
        {
            Assert.Single(tree.LoadSnapshot().Messages.OfType<AgentCustomMessage>());
            var exported = Path.Combine(root, "exported.jsonl");
            tree.Store.ExportCurrentBranch(exported);
            Assert.Single(new CodingAgentTreeSessionStore(exported).LoadCurrentBranchSnapshot().Messages.OfType<AgentCustomMessage>());
            Assert.Contains("checkpoint text", tree.Store.FormatTree(new CodingAgentTreeFormatOptions(SearchQuery: "checkpoint")));
        }
        await foreach (var ignored in runner.RunAsync("later")) { }
        var navigation = fixture.Runtime.Invoke(fixture.Files[0], "navigate", read.StatusMessage!);
        Assert.True(navigation.Success, navigation.Error);
        Assert.Empty(runner.Messages.OfType<AgentCustomMessage>());
    }
}
