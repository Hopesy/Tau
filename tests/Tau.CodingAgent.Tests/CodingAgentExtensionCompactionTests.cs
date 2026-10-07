// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>发起命令已经返回时，压缩仍会完成并由后台通道投递完成回调中的消息。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task DetachedCompaction_CompletesAfterCommandReturn()
    {
        using var fixture = new Fixture("""
            export default pi=>pi.registerCommand('compact',{handler:(_,ctx)=>{
              ctx.compact({onComplete:result=>pi.sendMessage({customType:'completed',content:result.summary})});
              return 'started';
            }});
            """);
        var (runner, provider) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        using var commands = fixture.BindCommands(runner);
        await foreach (var _ in runner.RunAsync("original")) { }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new AssistantMessageStream();
        provider.StreamFactory = _ => { entered.TrySetResult(); return stream; };
        runner.BackgroundEvent += (evt, _) => { if (evt is MessageEndEvent { Message: AgentCustomMessage }) completed.TrySetResult(); return Task.CompletedTask; };
        runner.StartBackgroundDelivery();
        var invocation = fixture.Runtime.Invoke(fixture.Files[0], "compact", "");
        Assert.True(invocation.Success);
        Assert.Equal("started", invocation.StatusMessage);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("summary")]) { StopReason = StopReason.EndTurn }));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runner.WaitForIdleAsync(default);
        Assert.Contains("summary", Assert.Single(Assert.Single(runner.Messages.OfType<AgentCustomMessage>()).Content.OfType<TextContent>()).Text);
        await runner.StopBackgroundDeliveryAsync();
    }

    /// <summary>重载扩展进程会取消已脱离原命令的摘要请求，旧回调不能继续改写会话。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task RuntimeReset_CancelsDetachedCompaction()
    {
        using var fixture = new Fixture("""
            import fs from 'node:fs';
            export default pi=>{
              fs.appendFileSync('loads.txt','loaded\n');
              pi.on('session_compact_failed',()=>fs.appendFileSync('loads.txt','failed\n'));
              pi.registerCommand('compact',{handler:(_,ctx)=>ctx.compact({onComplete:()=>pi.sendMessage({customType:'late',content:'late'})})});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        await foreach (var _ in runner.RunAsync("original")) { }
        var original = runner.Messages.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })));
            entered.TrySetResult();
            return stream;
        };
        Assert.True(fixture.Runtime.Invoke(fixture.Files[0], "compact", "").Success);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Runtime.Reset();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runner.WaitForIdleAsync(timeout.Token);
        Assert.False(runner.IsCompacting);
        Assert.Equal(original, runner.Messages);
        Assert.Empty(runner.Messages.OfType<AgentCustomMessage>());
        Assert.Equal("loaded\n", File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "loads.txt")));
    }

    /// <summary>ctx.compact 真正生成摘要，并在回调前更新内存或持久会话且保持会话身份。</summary>
    /// <param name="persistent">是否使用 JSONL 会话。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionCompaction_CommitsBeforeCompletionCallback(bool persistent)
    {
        using var fixture = new Fixture("""
            export default pi=>pi.registerCommand('compact',{handler:(_,ctx)=>new Promise(resolve=>{
              const sessionId=ctx.sessionManager.getSessionId();
              ctx.compact({customInstructions:'retain decisions',onComplete:result=>{
                pi.sendMessage({customType:'compacted',content:result.summary,details:{...result,sameSession:sessionId===ctx.sessionManager.getSessionId()}});
                resolve();
              },onError:error=>{pi.sendMessage({customType:'failed',content:error.message});resolve();}});
            })});
            """);
        var (runner, provider) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        using var commands = fixture.BindCommands(runner);
        CodingAgentTreeSessionController? tree = null;
        if (persistent)
        {
            tree = new(new CodingAgentTreeSessionStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "history.jsonl")), new(KeepRecentTokens: 0));
            commands.BindSession(runner, tree);
        }
        await foreach (var _ in runner.RunAsync("previous conversation")) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in runner.RunAsync("/compact", timeout.Token)) { }
        var completed = Assert.Single(runner.Messages.OfType<AgentCustomMessage>());
        Assert.Equal("compacted", completed.CustomType);
        var details = Assert.IsType<JsonElement>(completed.Details);
        Assert.True(details.GetProperty("sameSession").GetBoolean());
        Assert.True(details.GetProperty("tokensBefore").GetInt32() > 0);
        Assert.Contains(runner.Messages, CodingAgentCompactionMessages.IsSummaryMessage);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Contains(provider.Contexts[1].Messages.OfType<UserMessage>(), message => message.Content.OfType<TextContent>().Any(block => block.Text.Contains("previous conversation", StringComparison.Ordinal)));
        if (tree is not null) Assert.Single(tree.Store.ReadExtensionSnapshot().Entries, entry => entry.Type == "compaction");
    }

    /// <summary>会话过小或摘要失败通过 onError 返回 Error，不生成虚假的完成结果。</summary>
    /// <param name="providerFailure">是否已存在对话但摘要请求失败。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionCompaction_ReportsFailureAndPreservesHistory(bool providerFailure)
    {
        using var fixture = new Fixture("""
            export default pi=>pi.registerCommand('compact',{handler:(_,ctx)=>new Promise(resolve=>ctx.compact({
              onComplete:()=>{pi.sendMessage({customType:'unexpected',content:'success'});resolve();},
              onError:error=>{pi.sendMessage({customType:'error',content:error.message,details:{isError:error instanceof Error}});resolve();}
            }))});
            """);
        var (runner, provider) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        using var commands = fixture.BindCommands(runner);
        if (providerFailure)
        {
            await foreach (var _ in runner.RunAsync("original")) { }
            provider.StreamFactory = _ =>
            {
                var stream = new AssistantMessageStream();
                stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Error, ErrorMessage = "summary failed" }));
                return stream;
            };
        }
        var original = runner.Messages.ToArray();
        await foreach (var _ in runner.RunAsync("/compact")) { }
        Assert.Equal(original, runner.Messages.Take(original.Length));
        var error = Assert.Single(runner.Messages.OfType<AgentCustomMessage>());
        Assert.Equal("error", error.CustomType);
        Assert.True(Assert.IsType<JsonElement>(error.Details).GetProperty("isError").GetBoolean());
        Assert.Contains(providerFailure ? "summary failed" : "Nothing to compact", Assert.Single(error.Content.OfType<TextContent>()).Text);
        Assert.False(runner.IsCompacting);
    }

    /// <summary>摘要请求独立接受 abort；压缩期间的新提示明确拒绝，取消后历史仍可运行。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CompactionAbort_CancelsSummaryAndRejectsConcurrentPrompt()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        await foreach (var _ in runner.RunAsync("original")) { }
        var original = runner.Messages.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.StreamFactory = options =>
        {
            var stream = new AssistantMessageStream();
            options.Signal.Register(() => stream.Push(new DoneEvent(new AssistantMessage([]) { StopReason = StopReason.Aborted })));
            entered.TrySetResult();
            return stream;
        };
        var compacting = runner.CompactAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(runner.IsCompacting);
        Assert.False(runner.IsStreaming);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in runner.RunAsync("during summary")) { } });
        runner.Abort();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compacting);
        Assert.Equal(original, runner.Messages);
        Assert.False(runner.IsCompacting);
        provider.StreamFactory = null;
        await foreach (var _ in runner.RunAsync("after summary")) { }
    }
}
