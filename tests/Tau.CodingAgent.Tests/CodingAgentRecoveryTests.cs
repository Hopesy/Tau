// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>重试保留原始计费记录、工具结果和会话身份，只追加一次用户输入及启动钩子。</summary>
    /// <param name="persistent">是否绑定持久树。</param>
    /// <param name="withTool">是否在故障之前完成工具调用。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Recovery_RetriesContinuationAndRetainsRawHistory(bool persistent, bool withTool)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              let inputs=0,starts=0;
              pi.on('input',()=>{inputs++;});
              pi.on('before_agent_start',()=>{starts++;return {systemPrompt:'forced for all attempts'};});
              pi.registerCommand('counts',{handler:()=>JSON.stringify({inputs,starts})});
              pi.registerCommand('raw',{handler:(_,ctx)=>JSON.stringify(ctx.sessionManager.getEntries())});
            };
            """);
        var responses = new List<AssistantMessage>();
        if (withTool) responses.Add(new([new ToolCallContent("one-call", "read", "{}")]) { StopReason = StopReason.ToolUse });
        responses.Add(new([new TextContent("partial failure")])
            { StopReason = StopReason.Error, ErrorMessage = "stream ended before a terminal response event", Usage = new(7, 3) });
        responses.Add(new([new TextContent("recovered")]) { Usage = new(11, 5) });
        var (runner, provider) = fixture.CreateRunner(responses.ToArray());
        runner.RetryOptions = new(2, 0);
        CodingAgentTreeSessionController? tree = null;
        if (persistent)
        {
            tree = new(new CodingAgentTreeSessionStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "recovery.jsonl")));
            fixture.Runtime.BindSession(runner, tree);
        }
        var sessionId = runner.GetSessionStats().SessionId;
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("original input")) events.Add(evt);
        Assert.Equal(withTool ? 3 : 2, provider.Contexts.Count);
        Assert.Equal(sessionId, runner.GetSessionStats().SessionId);
        Assert.Equal(18, runner.GetSessionStats().Tokens.Input);
        Assert.Equal(withTool ? 3 : 2, runner.GetSessionStats().AssistantMessages);
        Assert.Equal(withTool ? 1 : 0, events.OfType<ToolExecutionStartEvent>().Count());
        Assert.Equal(withTool ? 1 : 0, provider.Contexts[^1].Messages.OfType<ToolResultMessage>().Count());
        Assert.DoesNotContain(provider.Contexts[^1].Messages.OfType<AssistantMessage>(), message => message.StopReason == StopReason.Error);
        Assert.Single(provider.Contexts[^1].Messages.OfType<UserMessage>());
        Assert.All(provider.Contexts, context => Assert.Equal("forced for all attempts", Transcript.GetCurrentSystemPrompt(context.Messages)));
        Assert.Equal([true, false], events.OfType<AgentEndEvent>().Select(evt => evt.WillRetry));
        Assert.True(Assert.Single(events.OfType<CodingAgentAutoRetryEndEvent>()).Success);
        var edit = Assert.Single(events.OfType<CodingAgentEntryAppendedEvent>()).Entry;
        Assert.Equal("context_edit", edit.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, edit.GetProperty("replacement").ValueKind);
        using var counts = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "counts", "").StatusMessage!);
        Assert.Equal(1, counts.RootElement.GetProperty("inputs").GetInt32());
        Assert.Equal(1, counts.RootElement.GetProperty("starts").GetInt32());
        using var raw = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "raw", "").StatusMessage!);
        Assert.Contains(raw.RootElement.EnumerateArray(), entry => entry.GetProperty("id").GetString() == edit.GetProperty("targetId").GetString());
        if (tree is not null)
        {
            var reopened = new CodingAgentTreeSessionStore(tree.Store.Path);
            Assert.DoesNotContain(reopened.LoadCurrentBranchSnapshot().Messages.OfType<AssistantMessage>(), message => message.StopReason == StopReason.Error);
            Assert.Equal(18, reopened.GetSessionUsageSummary().Tokens.Input);
        }
    }

    /// <summary>预算耗尽保留最后失败消息，非瞬时错误直接结束，不再次触发输入。</summary>
    /// <param name="error">最终错误。</param>
    /// <param name="expectedCalls">预期请求数量。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("terminated", 2)]
    [InlineData("429 insufficient_quota", 1)]
    public async Task Recovery_StopsAtBudgetOrTerminalFailure(string error, int expectedCalls)
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner(
            new([]) { StopReason = StopReason.Error, ErrorMessage = error },
            new([]) { StopReason = StopReason.Error, ErrorMessage = error });
        runner.RetryOptions = new(1, 0);
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("test")) events.Add(evt);
        Assert.Equal(expectedCalls, provider.Contexts.Count);
        Assert.False(events.OfType<AgentEndEvent>().Last().WillRetry);
        Assert.Equal(StopReason.Error, runner.Messages.OfType<AssistantMessage>().Last().StopReason);
        Assert.Equal(expectedCalls - 1, events.OfType<CodingAgentAutoRetryEndEvent>().Count());
        if (expectedCalls > 1) Assert.False(Assert.Single(events.OfType<CodingAgentAutoRetryEndEvent>()).Success);
    }

    /// <summary>开始事件发出时可以立即取消退避，后续新输入仍能执行。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Recovery_AbortRetryCancelsDelayAndKeepsSessionUsable()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner(new AssistantMessage([]) { StopReason = StopReason.Error, ErrorMessage = "terminated" });
        runner.RetryOptions = new(3, 60000);
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("cancel"))
        {
            events.Add(evt);
            if (evt is CodingAgentAutoRetryStartEvent)
            {
                Assert.True(runner.IsRetrying);
                Assert.True(runner.IsStreaming);
                runner.AbortRetry();
            }
        }
        Assert.Equal("Retry cancelled", Assert.Single(events.OfType<CodingAgentAutoRetryEndEvent>()).FinalError);
        Assert.Single(provider.Contexts);
        Assert.False(runner.IsRetrying);
        Assert.False(runner.IsStreaming);
        await foreach (var ignored in runner.RunAsync("next")) { }
        Assert.Equal(2, provider.Contexts.Count);
    }

    /// <summary>真实 RPC 必须完整消费失败后的恢复事件，而不是在首个 agent_end 停止。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Recovery_RpcConsumesAllAttemptsAndReportsNativeEvents()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner(new AssistantMessage([]) { StopReason = StopReason.Error, ErrorMessage = "terminated" });
        using var commands = fixture.BindCommands(runner);
        using var input = new BackgroundLineReader();
        using var output = new BackgroundLineWriter();
        var host = new CodingAgentRpcHost(runner, input, output, extensionCommandStore: commands, retryOptions: new(1, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = host.RunAsync(timeout.Token);
        await input.Lines.Writer.WriteAsync("""{"id":"p","type":"prompt","message":"retry"}""", timeout.Token);
        var firstEnd = await output.WaitAsync(line => line.GetProperty("type").GetString() == "agent_end", timeout.Token);
        Assert.True(firstEnd.GetProperty("willRetry").GetBoolean());
        var finished = await output.WaitAsync(line => line.GetProperty("type").GetString() == "auto_retry_end", timeout.Token);
        Assert.True(finished.GetProperty("success").GetBoolean());
        Assert.Equal(2, provider.Contexts.Count);
        input.Lines.Writer.Complete();
        Assert.Equal(0, await running);
    }
}
