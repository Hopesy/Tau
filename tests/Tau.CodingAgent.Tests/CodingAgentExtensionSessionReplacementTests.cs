// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>RPC 原生新建、分叉和恢复复用同一替换通道，各自生成独立文件并触发实际生命周期。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Replacement_RpcOperationsUseNativeFilesAndLifecycle()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.on('session_start',e=>pi.appendEntry('opened',{reason:e.reason}));
              pi.on('session_shutdown',e=>pi.appendEntry('closed',{reason:e.reason}));
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(root, "original.jsonl"), root));
        commands.BindSession(runner, tree);
        await commands.EnsureSessionStartedAsync();
        await foreach (var ignored in runner.RunAsync("original conversation")) { }
        tree.SyncFromRunner(runner);
        var original = tree.Path;
        var leaf = tree.GetSummary().LeafId!;
        using var input = new StringReader($$"""
            {"id":"forked","type":"fork","entryId":"{{leaf}}","position":"at"}
            {"id":"new","type":"new_session"}
            {"id":"resumed","type":"switch_session","sessionPath":"{{JsonEncodedText.Encode(original)}}"}
            """);
        using var output = new StringWriter();
        var host = new CodingAgentRpcHost(runner, input, output, treeSessionController: tree, extensionCommandStore: commands);
        Assert.Equal(0, await host.RunAsync());
        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            /// <summary>读取指定命令成功返回的实际文件路径。</summary>
            /// <param name="id">命令标识。</param>
            /// <returns>当前会话文件。</returns>
            string ReadPath(string id)
            {
                var response = responses.Select(document => document.RootElement).Single(value => value.TryGetProperty("id", out var key) && key.GetString() == id);
                Assert.True(response.GetProperty("success").GetBoolean(), response.GetRawText());
                return response.GetProperty("data").GetProperty("sessionFile").GetString()!;
            }
            var forked = ReadPath("forked");
            var fresh = ReadPath("new");
            Assert.Equal(original, ReadPath("resumed"));
            Assert.Equal(3, new HashSet<string> { original, forked, fresh }.Count);
            Assert.Contains("original conversation", File.ReadAllText(forked));
            Assert.DoesNotContain("original conversation", File.ReadAllText(fresh));
            Assert.Contains("\"reason\":\"new\"", File.ReadAllText(fresh));
            Assert.Contains("\"reason\":\"resume\"", File.ReadAllText(original));
        }
        finally { foreach (var document in responses) document.Dispose(); }
    }

    /// <summary>真实扩展新建独立会话，重新初始化实例，并在新上下文中执行 setup 与 withSession。</summary>
    /// <param name="persistent">是否使用独立 JSONL 文件。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_NewSessionRefreshesInstancesAndRejectsStaleContext(bool persistent)
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';
            export default pi=>{
              let starts=0;
              pi.on('session_start',(e,ctx)=>{ starts++; pi.appendEntry('started',{reason:e.reason,previous:e.previousSessionFile,count:starts}); });
              pi.on('session_shutdown',(e,ctx)=>{pi.appendEntry('closed',{reason:e.reason,target:e.targetSessionFile});});
              pi.registerCommand('new',{handler:async(_,ctx)=>{
                const oldId=ctx.sessionManager.getSessionId(), oldManager=ctx.sessionManager, oldGetter=ctx.getSystemPrompt;
                let newId, setupLeaf;
                const result=await ctx.newSession({parentSession:'parent.jsonl', setup:async sm=>{
                  setupLeaf=sm.appendMessage({role:'user',content:'seeded',timestamp:Date.now()});
                  sm.appendCustomEntry('setup',{ready:true});
                  sm.appendSessionInfo('replacement');
                }, withSession:async fresh=>{
                  newId=fresh.sessionManager.getSessionId();
                  if(newId===oldId || !fresh.sessionManager.getEntry(setupLeaf)) throw Error('setup missing');
                  for(const call of [()=>ctx.getSystemPrompt(),()=>oldManager.getEntries(),()=>oldGetter(),()=>pi.appendEntry('stale',{})]){
                    let rejected=false; try{call();}catch(e){rejected=/stale/.test(e.message);} if(!rejected) throw Error('old context accepted');
                  }
                  await fresh.sendMessage({customType:'new-message',content:'new context',display:false});
                }});
                return JSON.stringify({result,oldId,newId});
              }});
              pi.registerCommand('state',{handler:(_,ctx)=>JSON.stringify(ctx.sessionManager.getEntries())});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        CodingAgentTreeSessionController? tree = null;
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        if (persistent)
        {
            tree = new(new CodingAgentTreeSessionStore(Path.Combine(root, "old.jsonl"), root));
            commands.BindSession(runner, tree);
        }
        await commands.EnsureSessionStartedAsync();
        await foreach (var ignored in runner.RunAsync("old conversation")) { }
        var previousPath = tree?.Path;
        var result = fixture.Runtime.Invoke(fixture.Files[0], "new", "");
        Assert.True(result.Success, result.Error);
        using var returned = JsonDocument.Parse(result.StatusMessage!);
        Assert.NotEqual(returned.RootElement.GetProperty("oldId").GetString(), returned.RootElement.GetProperty("newId").GetString());
        Assert.Equal("replacement", runner.SessionName);
        Assert.Equal("seeded", Assert.Single(runner.Messages.OfType<UserMessage>()).Content.OfType<TextContent>().Single().Text);
        using var entries = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "state", "").StatusMessage!);
        var started = entries.RootElement.EnumerateArray().Single(entry => entry.TryGetProperty("customType", out var type) && type.GetString() == "started");
        Assert.Equal("new", started.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(1, started.GetProperty("data").GetProperty("count").GetInt32());
        if (persistent)
        {
            Assert.NotEqual(previousPath, tree!.Path);
            Assert.Contains("old conversation", File.ReadAllText(previousPath!));
            Assert.Contains("\"closed\"", File.ReadAllText(previousPath!));
            Assert.DoesNotContain("seeded", File.ReadAllText(previousPath!));
        }
    }

    /// <summary>取消钩子可以阻止新建、恢复和分叉，并停止后续处理器，不创建文件或终止旧会话。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Replacement_CancellationPrecedesValidationAndStopsHandlers()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let count=0;
              pi.on('session_before_switch',()=>{count++;return {cancel:true};});
              pi.on('session_before_switch',()=>{throw Error('must not run');});
              pi.on('session_before_fork',()=>{count++;return {cancel:true};});
              pi.on('session_shutdown',()=>{throw Error('must not shutdown');});
              pi.registerCommand('cancel',{handler:async(_,ctx)=>{
                const id=ctx.sessionManager.getSessionId();
                const results=[await ctx.newSession(),await ctx.switchSession('missing.jsonl'),await ctx.fork('invalid')];
                if(results.some(r=>!r.cancelled)||ctx.sessionManager.getSessionId()!==id||count!==3) throw Error('bad cancellation');
                return 'cancelled';
              }});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        await commands.EnsureSessionStartedAsync();
        var result = fixture.Runtime.Invoke(fixture.Files[0], "cancel", "");
        Assert.True(result.Success, result.Error);
        Assert.Equal("cancelled", result.StatusMessage);
    }

    /// <summary>分叉复制选中父链并保留标识，before 返回用户文本且不复制该输入，at 包含目标条目。</summary>
    /// <param name="persistent">是否使用持久化文件。</param>
    /// <param name="position">分叉位置。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, "before")]
    [InlineData(false, "at")]
    [InlineData(true, "before")]
    [InlineData(true, "at")]
    public async Task Replacement_ForkPreservesIdsAndSelectedBoundary(bool persistent, string position)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.registerCommand('fork',{handler:async(position,ctx)=>{
                const sm=ctx.sessionManager, entries=sm.getEntries();
                const users=entries.filter(e=>e.type==='message'&&e.message.role==='user');
                const first=users[0], target=users[1];
                pi.setLabel(first.id,'kept');
                const result=await ctx.fork(target.id,{position,withSession:async fresh=>{
                  const manager=fresh.sessionManager;
                  if(!manager.getEntry(first.id)||manager.getLabel(first.id)!=='kept') throw Error('lost ID or label');
                  if(Boolean(manager.getEntry(target.id))!==(position==='at')) throw Error('wrong boundary');
                }});
                return JSON.stringify(result);
              }});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var tree = persistent ? new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(root, "old.jsonl"), root)) : null;
        if (tree is not null) commands.BindSession(runner, tree);
        await commands.EnsureSessionStartedAsync();
        await foreach (var ignored in runner.RunAsync("first")) { }
        await foreach (var ignored in runner.RunAsync("second")) { }
        var previousPath = tree?.Path;
        var result = fixture.Runtime.Invoke(fixture.Files[0], "fork", position);
        Assert.True(result.Success, result.Error);
        using var returned = JsonDocument.Parse(result.StatusMessage!);
        Assert.False(returned.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.Equal(position == "before" ? "second" : null, returned.RootElement.GetProperty("selectedText").GetString());
        Assert.Equal(position == "at" ? 2 : 1, runner.Messages.OfType<UserMessage>().Count());
        if (persistent)
        {
            Assert.NotEqual(previousPath, tree!.Path);
            Assert.Contains("second", File.ReadAllText(previousPath!));
            using var header = JsonDocument.Parse(File.ReadLines(tree.Path).First());
            Assert.Equal(previousPath, header.RootElement.GetProperty("parentSession").GetString());
        }
    }

    /// <summary>恢复实际文件，非法目标失败不关闭原会话，新会话回调能读取恢复消息。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Replacement_ResumeValidatesBeforeShutdownAndLoadsTarget()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.on('session_shutdown',e=>pi.appendEntry('closed',{reason:e.reason}));
              pi.on('session_start',(e,ctx)=>pi.appendEntry('start',{reason:e.reason,previous:e.previousSessionFile}));
              pi.registerCommand('resume',{handler:async(path,ctx)=>{
                try{await ctx.switchSession('missing.jsonl');throw Error('missing accepted');}
                catch(e){if(/missing accepted/.test(e.message))throw e;}
                if(ctx.sessionManager.getEntries().some(e=>e.customType==='closed'))throw Error('closed too early');
                const result=await ctx.switchSession(path,{withSession:async fresh=>{
                  if(!fresh.sessionManager.buildSessionProjection().messages.some(m=>m.role==='user'&&JSON.stringify(m.content).includes('target')))throw Error('target missing');
                }});
                return JSON.stringify(result);
              }});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var old = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(Path.Combine(root, "old.jsonl"), root));
        var target = new CodingAgentTreeSessionStore(Path.Combine(root, "target.jsonl"), root);
        target.AppendMessages([new UserMessage("target")], 0);
        commands.BindSession(runner, old);
        await commands.EnsureSessionStartedAsync();
        var result = fixture.Runtime.Invoke(fixture.Files[0], "resume", target.Path);
        Assert.True(result.Success, result.Error);
        Assert.Equal(target.Path, old.Path);
        Assert.Equal("target", Assert.Single(runner.Messages.OfType<UserMessage>()).Content.OfType<TextContent>().Single().Text);
    }
}
