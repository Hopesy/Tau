// 作者：xxx
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>仅追加的消息也依次触发开始和结束转换，宿主及落盘历史只收到转换后的完成消息。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BackgroundAppend_TransformsMessageEndExactlyOnce()
    {
        using var fixture = new Fixture("""
            export default pi => {
              const seen=[];
              pi.on('session_start',()=>pi.sendMessage({customType:'note',content:'original'}));
              pi.on('message_start',e=>seen.push(e.type));
              pi.on('message_end',e=>{
                seen.push(e.type);
                return {message:{...e.message,content:[{type:'text',text:'transformed'}]}};
              });
              pi.registerCommand('seen',{handler:()=>JSON.stringify(seen)});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var startup = JsonDocument.Parse("""{"type":"session_start"}""");
        Assert.True(fixture.Runtime.EmitEvent(fixture.Files[0], startup.RootElement).Success);
        runner.StartBackgroundDelivery();
        await runner.WaitForIdleAsync(default);
        Assert.Empty(provider.Contexts);
        Assert.Equal("transformed", Assert.Single(Assert.Single(runner.Messages.OfType<AgentCustomMessage>()).Content.OfType<TextContent>()).Text);
        using var seen = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "seen", "").StatusMessage!);
        Assert.Equal(["message_start", "message_end"], seen.RootElement.EnumerateArray().Select(item => item.GetString()));
        await runner.StopBackgroundDeliveryAsync();
    }

    /// <summary>RPC 能查询和取消自动回合，取消后仍发布结束事件，不把宿主留在流式状态。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task RpcBackgroundRun_ReportsStreamingAndAcceptsAbort()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('session_start',()=>pi.sendUserMessage('background'));
            """);
        var (runner, provider) = fixture.CreateRunner();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })));
            requested.TrySetResult();
            return stream;
        };
        using var startup = JsonDocument.Parse("""{"type":"session_start"}""");
        Assert.True(fixture.Runtime.EmitEvent(fixture.Files[0], startup.RootElement).Success);
        using var input = new BackgroundLineReader();
        using var output = new BackgroundLineWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var host = new CodingAgentRpcHost(runner, input, output);
        var running = host.RunAsync(timeout.Token);
        await requested.Task.WaitAsync(timeout.Token);
        input.Lines.Writer.TryWrite("""{"id":"state","type":"get_state"}""");
        var state = await output.WaitAsync(item => item.TryGetProperty("id", out var id) && id.GetString() == "state", timeout.Token);
        Assert.True(state.GetProperty("data").GetProperty("isStreaming").GetBoolean());
        input.Lines.Writer.TryWrite("""{"id":"abort","type":"abort"}""");
        await output.WaitAsync(item => item.GetProperty("type").GetString() == "agent_end", timeout.Token);
        await output.WaitAsync(item => item.GetProperty("type").GetString() == "agent_settled", timeout.Token);
        await runner.WaitForIdleAsync(timeout.Token);
        Assert.False(runner.IsStreaming);
        input.Lines.Writer.TryComplete();
        Assert.Equal(0, await running.WaitAsync(timeout.Token));
    }

    /// <summary>【CodingAgent】【RPC 结算】取消真实前台运行后继续输出终态，客户端能等待最终结算</summary>
    /// <returns>异步协议回归任务</returns>
    [Fact]
    public async Task RpcForegroundCancellation_PublishesAgentEndThenSettled()
    {
        using var fixture = new Fixture("export default pi => {};");
        var (runner, provider) = fixture.CreateRunner();
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var host = new CodingAgentRpcHost(runner, input, output);
        var running = host.RunAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"prompt","type":"prompt","message":"foreground"}""");
        await requested.Task.WaitAsync(deadline.Token);
        input.Lines.Writer.TryWrite("""{"id":"abort","type":"abort"}""");
        await output.WaitAsync(item => item.GetProperty("type").GetString() == "agent_end", deadline.Token);
        await output.WaitAsync(item => item.GetProperty("type").GetString() == "agent_settled", deadline.Token);
        await runner.WaitForIdleAsync(deadline.Token);
        Assert.False(runner.IsStreaming);
        input.Lines.Writer.TryComplete();
        Assert.Equal(0, await running.WaitAsync(deadline.Token));
    }

    /// <summary>打印模式能保存启动期间的主动消息，并维持完整 JSON 事件或最终文本输出。</summary>
    /// <param name="json">是否输出 JSON 事件。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrintBackgroundDelivery_PreservesFinalOutput(bool json)
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('session_start',()=>pi.sendMessage({customType:'startup',content:'startup data'}));
            """);
        var (runner, provider) = fixture.CreateRunner();
        using var startup = JsonDocument.Parse("""{"type":"session_start"}""");
        Assert.True(fixture.Runtime.EmitEvent(fixture.Files[0], startup.RootElement).Success);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new CodingAgentPrintMode(runner, output, error, json);
        Assert.Equal(0, await host.RunAsync("foreground"));
        Assert.Empty(error.ToString());
        Assert.Single(provider.Contexts);
        Assert.Single(runner.Messages.OfType<AgentCustomMessage>());
        if (!json) Assert.Equal("done" + Environment.NewLine, output.ToString());
        else
        {
            var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Assert.Single(lines, item => item.GetProperty("type").GetString() == "agent_end");
            Assert.Contains(lines, item => item.GetProperty("type").GetString() == "message_end" && item.GetProperty("message").TryGetProperty("customType", out var type) && type.GetString() == "startup");
        }
    }

    /// <summary>加载阶段创建的定时器在宿主空闲时仍能发送图片、保存元数据并触发模型。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BackgroundTimer_FromLoad_UsesLiveSessionAndPreservesImages()
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';
            export default pi => {
              const timer = setInterval(() => {
                if (!fs.existsSync('release')) return;
                clearInterval(timer);
                pi.setSessionName('background session');
                pi.appendEntry('timer-state',{complete:true});
                pi.sendUserMessage([{type:'text',text:'from timer'},{type:'image',data:'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',mimeType:'image/png'}]);
              },5);
              pi.registerCommand('ready',{handler:()=> 'ready'});
              pi.on('input', e => ({action:'transform',text:e.source+':'+e.text}));
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.BackgroundEvent += (evt, _) => { if (evt is AgentEndEvent) completed.TrySetResult(); return Task.CompletedTask; };
        runner.StartBackgroundDelivery();
        Assert.True(fixture.Runtime.Invoke(fixture.Files[0], "ready", "").Success);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "release"), "");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runner.WaitForIdleAsync(default);
        var message = Assert.Single(Assert.Single(provider.Contexts).Messages.OfType<UserMessage>());
        Assert.Equal("extension:from timer", Assert.Single(message.Content.OfType<TextContent>()).Text);
        Assert.Equal("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", Assert.Single(message.Content.OfType<ImageContent>()).Data);
        Assert.Equal("background session", runner.SessionName);
        await runner.StopBackgroundDeliveryAsync();
    }

    /// <summary>调用结束后的异步宿主认证和等待 API 能返回，不依赖尚存的请求记录。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BackgroundHostRequests_CompleteAfterCommandReturns()
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';
            export default pi => pi.registerCommand('arm',{handler:(_,ctx)=> {
              const timer=setInterval(async()=>{
                if (!fs.existsSync('release')) return;
                clearInterval(timer);
                await ctx.waitForIdle();
                const key=await ctx.modelRegistry.getApiKey(ctx.model);
                pi.sendMessage({customType:'auth-status',content:key === 'synthetic' ? 'resolved' : 'missing'});
              },5);
              return 'armed';
            }});
            """);
        var (runner, provider) = fixture.CreateRunner();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.BackgroundEvent += (evt, _) => { if (evt is MessageEndEvent) completed.TrySetResult(); return Task.CompletedTask; };
        runner.StartBackgroundDelivery();
        Assert.True(fixture.Runtime.Invoke(fixture.Files[0], "arm", "").Success);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "release"), "");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runner.WaitForIdleAsync(default);
        Assert.Empty(provider.Contexts);
        Assert.Equal("resolved", Assert.Single(Assert.Single(runner.Messages.OfType<AgentCustomMessage>()).Content.OfType<TextContent>()).Text);
        await runner.StopBackgroundDeliveryAsync();
    }

    /// <summary>后台 nextTurn 只缓存到下一轮，追加消息会发布事件并立即保存到 JSONL。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BackgroundCustomMessages_PersistAndWaitForNextTurn()
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';
            export default pi => pi.registerCommand('arm',{handler:()=> {
              const timer=setInterval(()=>{
                if (!fs.existsSync('release')) return;
                clearInterval(timer);
                pi.sendMessage({customType:'deferred',content:'next context'},{deliverAs:'nextTurn'});
                pi.sendMessage({customType:'appended',content:'saved now',display:false,details:{tag:7}});
              },5);
              return 'armed';
            }});
            """);
        var (runner, provider) = fixture.CreateRunner();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "history.jsonl");
        var tree = new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(path));
        fixture.Runtime.BindSession(runner, tree);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.BackgroundEvent += (evt, _) => { if (evt is MessageEndEvent) completed.TrySetResult(); return Task.CompletedTask; };
        runner.StartBackgroundDelivery();
        Assert.True(fixture.Runtime.Invoke(fixture.Files[0], "arm", "").Success);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "release"), "");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runner.WaitForIdleAsync(default);
        Assert.Empty(provider.Contexts);
        Assert.Equal("appended", Assert.Single(runner.Messages.OfType<AgentCustomMessage>()).CustomType);
        Assert.Contains("saved now", File.ReadAllText(path));
        await foreach (var _ in runner.RunAsync("next")) { }
        Assert.Contains(Assert.Single(provider.Contexts).Messages.OfType<UserMessage>(), item => item.Content.OfType<TextContent>().Any(block => block.Text == "next context"));
        Assert.Single(runner.Messages.OfType<AgentCustomMessage>(), item => item.CustomType == "deferred");
        await runner.StopBackgroundDeliveryAsync();
    }

    /// <summary>后台流运行时停止宿主，会取消真正的模型请求并等待事件处理结束。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task StopBackground_CancelsModelRequestAndAllowsForegroundReuse()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('session_start',()=>pi.sendUserMessage('background'));
            """);
        var (runner, provider) = fixture.CreateRunner();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => { cancelled.TrySetResult(); stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })); });
            requested.TrySetResult();
            return stream;
        };
        using var startup = JsonDocument.Parse("""{"type":"session_start"}""");
        Assert.True(fixture.Runtime.EmitEvent(fixture.Files[0], startup.RootElement).Success);
        runner.StartBackgroundDelivery();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(runner.IsStreaming);
        await runner.StopBackgroundDeliveryAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(runner.IsStreaming);
        provider.StreamFactory = null;
        await foreach (var _ in runner.RunAsync("foreground")) { }
        Assert.Equal(2, provider.Contexts.Count);
    }

    /// <summary>后台与用户请求按完整回合串行，慢订阅和出错订阅不破坏下一轮运行。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BackgroundAndForeground_AreSerializedAcrossSubscribers()
    {
        using var fixture = new Fixture("""
            export default pi => pi.on('session_start',()=>pi.sendUserMessage('background'));
            """);
        var (runner, provider) = fixture.CreateRunner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<AgentEvent>();
        runner.BackgroundEvent += (_, _) => throw new InvalidOperationException("subscriber failure");
        runner.BackgroundEvent += async (evt, token) =>
        {
            seen.Enqueue(evt);
            if (evt is AgentEndEvent) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        using var startup = JsonDocument.Parse("""{"type":"session_start"}""");
        Assert.True(fixture.Runtime.EmitEvent(fixture.Files[0], startup.RootElement).Success);
        runner.StartBackgroundDelivery();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var foreground = Task.Run(async () => { await foreach (var _ in runner.RunAsync("foreground")) { } });
        Assert.Single(provider.Contexts);
        release.TrySetResult();
        await foreground.WaitAsync(TimeSpan.FromSeconds(10));
        await runner.WaitForIdleAsync(default);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Single(seen.OfType<AgentEndEvent>());
        Assert.Equal(["background", "foreground"], runner.Messages.OfType<UserMessage>().Select(message => Assert.Single(message.Content.OfType<TextContent>()).Text));
        await runner.StopBackgroundDeliveryAsync();
    }

    /// <summary>以管道在测试期间持续向 RPC 发送命令，避免依赖固定延时。</summary>
    private sealed class BackgroundLineReader : TextReader
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        /// <summary>读取下一条命令或等待输入完成。</summary>
        /// <param name="cancellationToken">停止输入的信号。</param>
        /// <returns>命令行，输入完成时为空。</returns>
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (await Lines.Reader.WaitToReadAsync(cancellationToken)) return await Lines.Reader.ReadAsync(cancellationToken);
            return null;
        }
    }

    /// <summary>按完整协议行同步测试，不轮询输出文本。</summary>
    private sealed class BackgroundLineWriter : StringWriter
    {
        private readonly Channel<JsonElement> _lines = Channel.CreateUnbounded<JsonElement>();
        /// <summary>复制 JSON 行并唤醒测试读取端。</summary>
        /// <param name="value">完整协议行。</param>
        /// <returns>写入完成的任务。</returns>
        public override Task WriteLineAsync(string? value)
        {
            using var document = JsonDocument.Parse(value!);
            _lines.Writer.TryWrite(document.RootElement.Clone());
            return Task.CompletedTask;
        }
        /// <summary>等待指定协议事件。</summary>
        /// <param name="predicate">目标事件判定。</param>
        /// <param name="token">测试超时信号。</param>
        /// <returns>匹配的 JSON 对象。</returns>
        public async Task<JsonElement> WaitAsync(Func<JsonElement, bool> predicate, CancellationToken token)
        {
            await foreach (var item in _lines.Reader.ReadAllAsync(token)) if (predicate(item)) return item;
            throw new EndOfStreamException();
        }
    }
}
