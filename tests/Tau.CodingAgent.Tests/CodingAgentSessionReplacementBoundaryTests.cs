// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>旧平面会话新建和分叉时转为独立 JSONL，原始文件仍可恢复，兼容副本跟随新文件。</summary>
    /// <param name="fork">是否复制当前分支。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_FlatStorageCreatesIndependentNativeSession(bool fork)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerCommand('replace',{handler:async(mode,ctx)=>{
                const old=ctx.sessionManager.getSessionId();
                const options={withSession:async fresh=>{
                  if(fresh.sessionManager.getSessionId()===old||!fresh.sessionManager.getSessionFile().endsWith('.jsonl'))throw Error('not native replacement');
                  await fresh.sendMessage({customType:'replacement',content:'new metadata',display:false});
                }};
                return JSON.stringify(mode==='fork'?await ctx.fork(ctx.sessionManager.getLeafId(),{...options,position:'at'}):await ctx.newSession(options));
              }});
              pi.registerCommand('identity',{handler:(_,ctx)=>JSON.stringify({id:ctx.sessionManager.getSessionId(),file:ctx.sessionManager.getSessionFile()})});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var oldPath = Path.Combine(root, "legacy.json");
        var flat = new CodingAgentSessionStore(oldPath);
        commands.BindSession(runner, flat: flat);
        await foreach (var ignored in runner.RunAsync("original input")) { }
        flat.Save(runner.Messages, runner.Model);
        var result = fixture.Runtime.Invoke(fixture.Files[0], "replace", fork ? "fork" : "new");
        Assert.True(result.Success, result.Error);
        Assert.NotEqual(oldPath, flat.Path);
        Assert.Single(new CodingAgentSessionStore(oldPath).LoadStrict().Messages.OfType<UserMessage>());
        using var identity = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "identity", "").StatusMessage!);
        var nativePath = identity.RootElement.GetProperty("file").GetString()!;
        Assert.Equal(Path.ChangeExtension(nativePath, ".json"), flat.Path);
        Assert.Equal(identity.RootElement.GetProperty("id").GetString(), runner.SessionId);
        var native = new CodingAgentTreeSessionStore(nativePath);
        var snapshot = native.LoadCurrentBranchSnapshot();
        Assert.Equal(fork ? 1 : 0, snapshot.Messages.OfType<UserMessage>().Count());
        Assert.Single(snapshot.Messages.OfType<AgentCustomMessage>());
        Assert.Equal(fork ? oldPath : null, native.GetSessionHeader().ParentSession);
        Assert.DoesNotContain("new metadata", File.ReadAllText(oldPath));
    }

    /// <summary>目标模型失效时仍能完整恢复消息和文件，选择可用回退模型并更新实际会话标识。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Replacement_UnknownModelUsesFallbackBeforeAdoption()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.on('session_shutdown',()=>pi.appendEntry('closed',{}));
              pi.registerCommand('resume',{handler:async(path,ctx)=>{
                await ctx.switchSession(path,{withSession:async fresh=>{
                  if(fresh.model.provider!=='synthetic'||fresh.model.id!=='test')throw Error('unusable model');
                }});return 'restored';
              }});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(root, "old.jsonl"), root));
        commands.BindSession(runner, tree);
        var previousId = runner.SessionId;
        var target = new CodingAgentTreeSessionStore(Path.Combine(root, "target.jsonl"), root);
        target.AppendModelChange("retired-provider", "retired-model");
        target.AppendMessages([new UserMessage("restored target")], 0);
        var result = fixture.Runtime.Invoke(fixture.Files[0], "resume", target.Path);
        Assert.True(result.Success, result.Error);
        Assert.Equal(target.Path, tree.Path);
        Assert.NotEqual(previousId, runner.SessionId);
        Assert.Equal(target.GetSessionHeader().Id, runner.SessionId);
        Assert.Contains("retired-provider/retired-model", runner.SessionModelFallbackMessage);
        Assert.Equal("synthetic", runner.Model.Provider);
        Assert.Equal("restored target", Assert.IsType<TextContent>(Assert.Single(Assert.Single(runner.Messages.OfType<UserMessage>()).Content)).Text);
        var raw = File.ReadAllText(target.Path);
        Assert.Contains("retired-model", raw);
        Assert.Contains("synthetic", raw);
    }

    /// <summary>旧命令在替换前积累的消息会丢弃，保存的事件总线闭包失效，新回调消息仍可正常交付。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Replacement_DiscardsOldActionsAndInvalidatesEventBus()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerCommand('replace',{handler:async(_,ctx)=>{
                const emit=pi.events.emit;
                pi.sendUserMessage('must not enter new session');
                pi.sendMessage({customType:'stale',content:'discard old action'});
                await ctx.newSession({withSession:async fresh=>await fresh.sendMessage({customType:'fresh',content:'kept'})});
                let stale=false;try{emit('late',{});}catch(e){stale=/stale/.test(e.message);}
                if(!stale)throw Error('old bus still works');
                return 'replaced';
              }});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        await foreach (var ignored in runner.RunAsync("/replace")) { }
        Assert.Empty(provider.Contexts);
        Assert.Empty(runner.Messages.OfType<UserMessage>());
        var custom = Assert.Single(runner.Messages.OfType<AgentCustomMessage>());
        Assert.Equal("fresh", custom.CustomType);
    }
}
