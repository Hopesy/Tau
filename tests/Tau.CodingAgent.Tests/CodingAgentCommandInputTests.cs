// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>真实交互宿主只在消息完成时调用渲染器一次，同时保留命令发送的图片。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task InteractiveCommand_RendersOnceAndPreservesImages()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let rendered=0;
              pi.registerMessageRenderer('note',()=>({render:()=>['rendered '+(++rendered)],invalidate(){}}));
              pi.registerCommand('image',{handler:()=>{
                pi.sendMessage({customType:'note',content:'note'});
                pi.sendUserMessage([{type:'image',data:'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',mimeType:'image/png'}]);
              }});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var terminal = new FakeTerminal();
        terminal.QueueInput("/image");
        terminal.QueueInput("exit");
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner, extensionCommandStore: commands);
        Assert.Equal(0, await host.RunAsync());
        Assert.Single(provider.Contexts);
        Assert.Single(Assert.Single(runner.Messages.OfType<UserMessage>()).Content.OfType<ImageContent>());
        Assert.Contains("rendered 1", terminal.FlattenedText());
        Assert.DoesNotContain("rendered 2", terminal.FlattenedText());
    }

    /// <summary>快捷键返回的完整动作通过真实调度器投递，保留默认自定义追加行为和图片。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ShortcutActions_UseSharedDeliverySemantics()
    {
        using var fixture = new Fixture("""
            export default pi=>pi.registerShortcut('ctrl+x',{handler:()=>{
              pi.sendMessage({customType:'note',content:'context'});
              pi.sendUserMessage([{type:'text',text:'inspect'},{type:'image',data:'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',mimeType:'image/png'}]);
            }});
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var shortcut = Assert.Single(commands.LoadStatus().Shortcuts);
        Assert.True(commands.TryInvokeShortcut(shortcut, out var result, preserveMessageActions: true));
        Assert.NotNull(result?.MessageActions);
        await foreach (var _ in runner.DeliverExtensionMessagesAsync(result.MessageActions, default)) { }
        Assert.Single(provider.Contexts);
        Assert.Single(runner.Messages.OfType<AgentCustomMessage>());
        Assert.Single(Assert.Single(runner.Messages.OfType<UserMessage>()).Content.OfType<ImageContent>());
    }

    /// <summary>命令在 input 之前执行，可等待空闲；主动发送的图片经过 extension 来源的输入钩子。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CommandPrompt_PreservesImagesAndInputOrdering()
    {
        using var fixture = new Fixture("""
            export default pi => {
              pi.on('input',e=>({action:'transform',text:e.source+':'+e.text}));
              pi.registerCommand('photo',{handler:async(args,ctx)=>{
                await ctx.waitForIdle();
                pi.sendMessage({customType:'context',content:'before image',display:false});
                pi.sendUserMessage([{type:'text',text:args},{type:'image',data:'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',mimeType:'image/png'}]);
              }});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var events = new List<AgentEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in runner.RunAsync("/photo inspect", timeout.Token)) events.Add(evt);
        var message = Assert.Single(runner.Messages.OfType<UserMessage>());
        Assert.Equal("extension:inspect", Assert.Single(message.Content.OfType<TextContent>()).Text);
        Assert.Equal("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", Assert.Single(message.Content.OfType<ImageContent>()).Data);
        Assert.Single(provider.Contexts);
        Assert.Single(events.OfType<AgentStartEvent>());
        var custom = Assert.Single(runner.Messages.OfType<AgentCustomMessage>());
        Assert.True(runner.Messages.ToList().IndexOf(custom) < runner.Messages.ToList().IndexOf(message));
    }

    /// <summary>已注册但不发送消息的命令被消费，未注册命令按普通输入处理。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task StatusOnlyCommand_DoesNotReachInputHookOrModel()
    {
        using var fixture = new Fixture("""
            export default pi => {
              pi.registerCommand('status',{handler:()=> 'available'});
              pi.on('input',e=>({action:'transform',text:'input:'+e.text}));
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        await foreach (var _ in runner.RunAsync("/status")) { }
        Assert.Empty(provider.Contexts);
        Assert.Empty(runner.Messages.OfType<UserMessage>());
        await foreach (var _ in runner.RunAsync("/unknown args")) { }
        Assert.Equal("input:/unknown args", Assert.Single(Assert.Single(runner.Messages.OfType<UserMessage>()).Content.OfType<TextContent>()).Text);
    }

    /// <summary>命令抛错产生扩展错误事件，抛错前发送的消息仍提交，原始命令不写入对话。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task FailedCommand_PreservesPriorActionsWithoutStartingModel()
    {
        using var fixture = new Fixture("""
            export default pi => pi.registerCommand('fail',{handler:()=>{
              pi.sendMessage({customType:'saved',content:'before error'});
              throw Error('expected failure');
            }});
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("/fail")) events.Add(evt);
        Assert.Contains("expected failure", Assert.Single(events.OfType<CodingAgentExtensionErrorEvent>()).Error);
        Assert.Equal("saved", Assert.Single(runner.Messages.OfType<AgentCustomMessage>()).CustomType);
        Assert.Empty(provider.Contexts);
        Assert.Empty(runner.Messages.OfType<UserMessage>());
    }

    /// <summary>steer 和 followUp 不允许把扩展命令当普通文本加入队列。</summary>
    /// <param name="followUp">是否使用后续队列。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedExtensionCommands_AreRejected(bool followUp)
    {
        using var fixture = new Fixture("export default pi=>pi.registerCommand('command',{handler:()=>{}});");
        var (runner, _) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        Assert.Throws<InvalidOperationException>(() => { if (followUp) runner.FollowUp("/command"); else runner.Steer("/command"); });
        Assert.Equal(0, runner.PendingMessageCount);
    }

    /// <summary>正在生成的回合不阻止扩展命令执行；命令的 followUp 消息加入原有循环。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CommandDuringStreaming_QueuesIntoExistingRun()
    {
        using var fixture = new Fixture("""
            export default pi=>pi.registerCommand('more',{handler:()=>pi.sendUserMessage('follow up',{deliverAs:'followUp'})});
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new AssistantMessageStream();
        provider.StreamFactory = _ => { entered.TrySetResult(); return stream; };
        var running = Task.Run(async () => { await foreach (var _ in runner.RunAsync("first", timeout.Token)) { } });
        await entered.Task.WaitAsync(timeout.Token);
        var commandEvents = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("/more", timeout.Token)) commandEvents.Add(evt);
        Assert.Empty(commandEvents);
        provider.StreamFactory = null;
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("first done")]) { StopReason = StopReason.EndTurn }));
        await running.WaitAsync(timeout.Token);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Equal(["first", "follow up"], runner.Messages.OfType<UserMessage>().Select(message => Assert.Single(message.Content.OfType<TextContent>()).Text));
    }

    /// <summary>RPC 命令在流式运行时执行且不要求 streamingBehavior，可通过命令取消当前运行。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task RpcCommandDuringStreaming_CanAbortCurrentRun()
    {
        using var fixture = new Fixture("export default pi=>pi.registerCommand('stop',{handler:(_,ctx)=>ctx.abort()});");
        var (runner, provider) = fixture.CreateRunner();
        using var commands = fixture.BindCommands(runner);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })));
            requested.TrySetResult();
            return stream;
        };
        using var input = new BackgroundLineReader();
        using var output = new BackgroundLineWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands);
        var running = host.RunAsync(timeout.Token);
        input.Lines.Writer.TryWrite("""{"id":"initial","type":"prompt","message":"first"}""");
        await requested.Task.WaitAsync(timeout.Token);
        input.Lines.Writer.TryWrite("""{"id":"command","type":"prompt","message":"/stop"}""");
        var response = await output.WaitAsync(item => item.TryGetProperty("id", out var id) && id.GetString() == "command", timeout.Token);
        Assert.True(response.GetProperty("success").GetBoolean());
        await runner.WaitForIdleAsync(timeout.Token);
        Assert.Single(provider.Contexts);
        input.Lines.Writer.TryComplete();
        Assert.Equal(0, await running.WaitAsync(timeout.Token));
    }
}
