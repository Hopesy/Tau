// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>命令重载真正更换模块工厂、工具及事件注册，原上下文失效，会话消息保持。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Reload_RebuildsToolsAndHandlersWithoutLosingMessages()
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';
            import path from 'node:path';
            export default pi=>{
              const next=fs.existsSync(path.join(pi.cwd,'next'));
              pi.registerTool({name:next?'new_tool':'old_tool',parameters:{type:'object'},execute:async()=>({content:[{type:'text',text:'tool'}]})});
              pi.on('session_start',(e,ctx)=>pi.appendEntry('start',{reason:e.reason,next}));
              if(next)pi.on('input',e=>({action:'transform',text:'new:'+e.text}));
              pi.on('session_shutdown',e=>pi.appendEntry('closed',{reason:e.reason}));
              pi.registerCommand('reload',{handler:async(_,ctx)=>{
                fs.writeFileSync(path.join(ctx.cwd,'next'),'');
                await ctx.reload();
                let stale=false;try{ctx.sessionManager.getSessionId();}catch(e){stale=/stale/.test(e.message);}
                if(!stale)throw Error('reload left context valid');
                return 'reloaded';
              }});
              pi.registerCommand('state',{handler:(_,ctx)=>JSON.stringify(ctx.sessionManager.getEntries())});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        await commands.EnsureSessionStartedAsync();
        await foreach (var ignored in runner.RunAsync("first")) { }
        var result = fixture.Runtime.Invoke(fixture.Files[0], "reload", "");
        Assert.True(result.Success, result.Error);
        Assert.Equal("reloaded", result.StatusMessage);
        Assert.Contains("new_tool", runner.GetActiveToolNames());
        Assert.DoesNotContain("old_tool", runner.GetRegisteredTools().Select(tool => tool.Name));
        await foreach (var ignored in runner.RunAsync("second")) { }
        Assert.Equal(["first", "new:second"], runner.Messages.OfType<UserMessage>().Select(message => string.Concat(message.Content.OfType<TextContent>().Select(text => text.Text))));
        Assert.Equal(2, provider.Contexts.Count);
        using var state = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "state", "").StatusMessage!);
        Assert.Contains(state.RootElement.EnumerateArray(), entry => entry.TryGetProperty("customType", out var type) && type.GetString() == "closed" && entry.GetProperty("data").GetProperty("reason").GetString() == "reload");
        Assert.Contains(state.RootElement.EnumerateArray(), entry => entry.TryGetProperty("customType", out var type) && type.GetString() == "start" && entry.GetProperty("data").GetProperty("next").GetBoolean());
    }

    /// <summary>新会话回调等待实际用户回合完成；旧回合先被取消并保存，回调中读取的是新会话最终消息。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Replacement_AbortsOldTurnAndAwaitsWithSessionPrompt()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.on('session_shutdown',(e,ctx)=>pi.appendEntry('closed',{messages:ctx.sessionManager.buildSessionProjection().messages}));
              pi.registerCommand('new',{handler:async(_,ctx)=>{
                await ctx.newSession({withSession:async fresh=>{
                  await fresh.sendUserMessage('new conversation');
                  const messages=fresh.sessionManager.buildSessionProjection().messages;
                  if(!messages.some(m=>m.role==='assistant')||messages.some(m=>m.role==='user'&&JSON.stringify(m.content).includes('old conversation')))throw Error('prompt not completed');
                }});
                return 'done';
              }});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var root = Path.GetDirectoryName(fixture.Files[0])!;
        var oldPath = Path.Combine(root, "old.jsonl");
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(oldPath, root));
        commands.BindSession(runner, tree);
        await commands.EnsureSessionStartedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })));
            entered.TrySetResult();
            return stream;
        };
        var running = Task.Run(async () =>
        {
            try { await foreach (var ignored in runner.RunAsync("old conversation", timeout.Token)) { } }
            catch (OperationCanceledException) { }
        });
        await entered.Task.WaitAsync(timeout.Token);
        provider.StreamFactory = null;
        var result = await Task.Run(() => fixture.Runtime.Invoke(fixture.Files[0], "new", "", timeout.Token), timeout.Token);
        Assert.True(result.Success, result.Error);
        await running.WaitAsync(timeout.Token);
        Assert.Equal("done", result.StatusMessage);
        Assert.Equal("new conversation", Assert.Single(runner.Messages.OfType<UserMessage>()).Content.OfType<TextContent>().Single().Text);
        Assert.Contains("old conversation", File.ReadAllText(oldPath));
        Assert.Contains("\"closed\"", File.ReadAllText(oldPath));
        Assert.DoesNotContain("new conversation", File.ReadAllText(oldPath));
    }
}
