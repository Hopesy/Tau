// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>成功通知携带真实原因，结束时已解除压缩状态并允许订阅者直接执行下一轮。</summary>
    /// <param name="reason">压缩原因。</param>
    /// <param name="retry">是否重试原回合。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("manual", false)]
    [InlineData("threshold", false)]
    [InlineData("overflow", true)]
    public async Task CompactionEvents_ExposeReasonAndAllowEndReentry(string reason, bool retry)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let seen=[];
              pi.on('session_before_compact',(e,ctx)=>{
                seen.push({type:e.type,reason:e.reason,retry:e.willRetry,idle:ctx.isIdle()});
                return {compaction:{summary:'custom',firstKeptEntryId:e.preparation.firstKeptEntryId,tokensBefore:100}};
              });
              pi.on('session_compact',(e,ctx)=>seen.push({type:e.type,reason:e.reason,retry:e.willRetry,idle:ctx.isIdle()}));
              pi.registerCommand('seen',{handler:()=>JSON.stringify(seen)});
            };
            """);
        var (runner, provider) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        await foreach (var _ in runner.RunAsync("original")) { }
        var events = new List<AgentEvent>();
        var startedBusy = false;
        var endedIdle = false;
        runner.BackgroundEvent += async (evt, _) =>
        {
            events.Add(evt);
            if (evt is CodingAgentCompactionStartEvent) startedBusy = runner.IsCompacting;
            if (evt is CodingAgentCompactionEndEvent)
            {
                endedIdle = !runner.IsCompacting && !runner.IsStreaming;
                await foreach (var ignored in runner.RunAsync("after end")) { }
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await runner.CompactForReasonAsync(null, reason, retry, timeout.Token);
        Assert.True(startedBusy);
        Assert.True(endedIdle);
        Assert.Equal(["compaction_start", "compaction_end"], events.Select(evt => evt.Type));
        var ended = Assert.IsType<CodingAgentCompactionEndEvent>(events[1]);
        Assert.Same(result, ended.Result);
        Assert.Equal(reason, ended.Reason);
        Assert.Equal(retry, ended.WillRetry);
        Assert.False(ended.Aborted);
        Assert.Equal(2, provider.Contexts.Count);
        using var seen = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "seen", "").StatusMessage!);
        Assert.Equal(2, seen.RootElement.GetArrayLength());
        foreach (var evt in seen.RootElement.EnumerateArray())
        {
            Assert.Equal(reason, evt.GetProperty("reason").GetString());
            Assert.Equal(retry, evt.GetProperty("retry").GetBoolean());
            Assert.False(evt.GetProperty("idle").GetBoolean());
        }
    }

    /// <summary>失败和取消发送唯一结束事件，失败钩子执行时压缩状态已经清除。</summary>
    /// <param name="cancel">是否由扩展取消。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactionEvents_FailureAndCancellationReleaseState(bool cancel)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let failed;
              pi.on('session_before_compact',e=>e.customInstructions==='cancel' ? {cancel:true} : undefined);
              pi.on('session_compact_failed',(e,ctx)=>{failed={...e,idle:ctx.isIdle()};});
              pi.registerCommand('failed',{handler:()=>JSON.stringify(failed)});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        if (cancel) await foreach (var ignored in runner.RunAsync("original")) { }
        var events = new List<AgentEvent>();
        runner.BackgroundEvent += (evt, _) => { events.Add(evt); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<Exception>(() => runner.CompactAsync(cancel ? "cancel" : null));
        Assert.Equal(["compaction_start", "compaction_end"], events.Select(evt => evt.Type));
        var ended = Assert.IsType<CodingAgentCompactionEndEvent>(events[1]);
        Assert.Null(ended.Result);
        Assert.Equal(cancel, ended.Aborted);
        if (cancel) Assert.Null(ended.ErrorMessage);
        else Assert.Contains("Nothing to compact", ended.ErrorMessage);
        using var failed = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "failed", "").StatusMessage!);
        Assert.True(failed.RootElement.GetProperty("idle").GetBoolean());
        Assert.Equal(cancel, failed.RootElement.GetProperty("aborted").GetBoolean());
    }

    /// <summary>提交后的成功钩子取消信号不会把已经落盘的成功转换为失败。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CompactionEvents_AbortAfterCommitStillReportsSuccess()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              pi.on('session_compact',(_,ctx)=>ctx.abort());
              pi.on('session_compact_failed',()=>pi.sendMessage({customType:'unexpected',content:'failed'}));
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        await foreach (var ignored in runner.RunAsync("original")) { }
        var events = new List<AgentEvent>();
        runner.BackgroundEvent += (evt, _) => { events.Add(evt); return Task.CompletedTask; };
        var result = await runner.CompactAsync();
        Assert.NotNull(result.StoredEntryId);
        Assert.False(Assert.Single(events.OfType<CodingAgentCompactionEndEvent>()).Aborted);
        Assert.Empty(runner.Messages.OfType<Tau.AgentCore.Harness.AgentCustomMessage>());
    }

    /// <summary>RPC 输出标准压缩开始、结束与响应字段，内部提交控制字段不进入协议。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CompactionEvents_RpcPublishesCommittedResult()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunner();
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        using var commands = fixture.BindCommands(runner);
        await foreach (var ignored in runner.RunAsync("original")) { }
        using var input = new BackgroundLineReader();
        using var output = new BackgroundLineWriter();
        var host = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = host.RunAsync(timeout.Token);
        await input.Lines.Writer.WriteAsync("""{"id":"compact","type":"compact"}""", timeout.Token);
        var received = new List<JsonElement>();
        while (true)
        {
            var line = await output.WaitAsync(_ => true, timeout.Token);
            received.Add(line);
            if (line.TryGetProperty("id", out var id) && id.GetString() == "compact") break;
        }
        Assert.Equal(["compaction_start", "compaction_end", "response"], received.Select(line => line.GetProperty("type").GetString()));
        var data = received[1].GetProperty("result");
        Assert.NotEmpty(data.GetProperty("firstKeptEntryId").GetString()!);
        Assert.True(data.GetProperty("estimatedTokensAfter").GetInt32() > 0);
        Assert.False(data.TryGetProperty("storedEntryId", out _));
        Assert.False(data.TryGetProperty("usesPreparedBoundary", out _));
        Assert.Equal(data.GetProperty("summary").GetString(), received[2].GetProperty("data").GetProperty("summary").GetString());
        input.Lines.Writer.Complete();
        Assert.Equal(0, await running);
    }
}
