// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>阈值压缩在完成回复后生成一次摘要，不重试已成功回复，也不因保留的旧用量重复压缩。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AutoCompaction_ThresholdCommitsAndSkipsStaleUsageBeforeNextInput()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunnerWithWindow(1000,
            AutoResponse("answer", 920), AutoResponse("summary", 5), AutoResponse("next answer", 10));
        runner.CompactionSettings = new(ReserveTokens: 100, KeepRecentTokens: 0);
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("first")) events.Add(evt);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Equal("threshold", Assert.Single(events.OfType<CodingAgentCompactionStartEvent>()).Reason);
        var end = Assert.Single(events.OfType<CodingAgentCompactionEndEvent>());
        Assert.False(end.WillRetry);
        Assert.NotNull(end.Result?.StoredEntryId);
        Assert.Single(events.OfType<AgentEndEvent>());
        Assert.Null(runner.GetContextUsage()!.Tokens);
        await foreach (var ignored in runner.RunAsync("next")) { }
        Assert.Equal(3, provider.Contexts.Count);
        Assert.Equal(11, runner.GetContextUsage()!.Tokens);
    }

    /// <summary>溢出和异常长度各自最多恢复一次，模型继续请求时不再看到失败消息。</summary>
    /// <param name="length">是否使用输出未达期望预算的长度结束。</param>
    /// <param name="failsAgain">压缩之后是否再次失败。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AutoCompaction_RecoversOverflowOrTruncationOnce(bool length, bool failsAgain)
    {
        using var fixture = new Fixture("""
            export default pi=>{
              const failures=[];
              pi.on('session_compact_failed',e=>failures.push(e));
              pi.registerCommand('failures',{handler:()=>JSON.stringify(failures)});
            };
            """);
        var failed = AutoResponse("failed fragment", 10) with
        {
            StopReason = length ? StopReason.MaxTokens : StopReason.Error,
            ErrorMessage = length ? null : "maximum context length is 2000 tokens"
        };
        var (runner, provider) = fixture.CreateRunnerWithLimits(2000, 100,
            AutoResponse("earlier reply", 10), failed, AutoResponse("recovery summary", 5), AutoResponse("turn summary", 5),
            failsAgain ? failed with { Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(1) } : AutoResponse("recovered", 10));
        runner.CompactionSettings = new(ReserveTokens: 100, KeepRecentTokens: 0);
        runner.RetryOptions = new(2, 0);
        await foreach (var ignored in runner.RunAsync("earlier input")) { }
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("current input")) events.Add(evt);
        Assert.Equal(5, provider.Contexts.Count);
        Assert.Single(events.OfType<CodingAgentCompactionStartEvent>());
        var endings = events.OfType<CodingAgentCompactionEndEvent>().ToArray();
        Assert.Equal(failsAgain ? 2 : 1, endings.Length);
        Assert.True(endings[0].WillRetry);
        Assert.NotNull(endings[0].Result);
        Assert.Equal(2, events.OfType<AgentEndEvent>().Count());
        Assert.Empty(events.OfType<CodingAgentAutoRetryStartEvent>());
        Assert.Single(events.OfType<CodingAgentEntryAppendedEvent>());
        Assert.DoesNotContain(provider.Contexts[^1].Messages.OfType<AssistantMessage>(), message => message.Content.OfType<TextContent>().Any(block => block.Text == "failed fragment"));
        if (failsAgain)
        {
            Assert.False(endings[1].WillRetry);
            Assert.Contains("after one compact-and-retry attempt", endings[1].ErrorMessage);
            using var failures = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "failures", "").StatusMessage!);
            Assert.Single(failures.RootElement.EnumerateArray());
        }
    }

    /// <summary>用户禁用或响应来自其他模型时不进行溢出重试。</summary>
    /// <param name="disabled">是否显式关闭自动压缩。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoCompaction_RespectsSwitchAndModelIdentity(bool disabled)
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunnerWithWindow(2000,
            AutoResponse("first", 10), AutoResponse("overflow", 10) with
            { StopReason = StopReason.Error, ErrorMessage = "context window exceeded", Model = disabled ? "test" : "old-model" });
        runner.CompactionSettings = new(ReserveTokens: 100, KeepRecentTokens: 0);
        if (disabled) runner.SetAutoCompactionEnabled(false);
        await foreach (var ignored in runner.RunAsync("first")) { }
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("next")) events.Add(evt);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Empty(events.OfType<CodingAgentCompactionStartEvent>());
        Assert.Empty(events.OfType<CodingAgentEntryAppendedEvent>());
    }

    /// <summary>扩展取消自动压缩时按 aborted 结束，不重试、不报告额外失败异常。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AutoCompaction_ExtensionCancellationPreservesHistory()
    {
        using var fixture = new Fixture("export default pi=>pi.on('session_before_compact',()=>({cancel:true}));");
        var (runner, provider) = fixture.CreateRunnerWithWindow(1000, AutoResponse("answer", 920));
        runner.CompactionSettings = new(ReserveTokens: 100, KeepRecentTokens: 0);
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("first")) events.Add(evt);
        var end = Assert.Single(events.OfType<CodingAgentCompactionEndEvent>());
        Assert.True(end.Aborted);
        Assert.False(end.WillRetry);
        Assert.Null(end.ErrorMessage);
        Assert.Single(provider.Contexts);
        Assert.Single(runner.Messages.OfType<AssistantMessage>());
        Assert.False(runner.IsCompacting);
    }

    /// <summary>打印 JSON 模式输出同一自动压缩生命周期，摘要不会替换用户的最终回复。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AutoCompaction_PrintModeUsesSharedRecovery()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunnerWithWindow(1000, AutoResponse("answer", 920), AutoResponse("summary", 5));
        runner.CompactionSettings = new(ReserveTokens: 100, KeepRecentTokens: 0);
        var output = new StringWriter();
        Assert.Equal(0, await new CodingAgentPrintMode(runner, output, new StringWriter(), jsonMode: true).RunAsync("first"));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Single(lines, line => line.RootElement.GetProperty("type").GetString() == "compaction_start");
            Assert.Single(lines, line => line.RootElement.GetProperty("type").GetString() == "compaction_end");
            Assert.Single(lines, line => line.RootElement.GetProperty("type").GetString() == "agent_end");
        }
        finally { foreach (var line in lines) line.Dispose(); }
        Assert.Equal(2, provider.Contexts.Count);
    }

    /// <summary>压缩期间排队的后续输入在下一次请求直接消费，不先重复执行已完成的回复。</summary>
    /// <param name="followUp">是否使用后续队列；否则使用引导队列。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoCompaction_ConsumesMessagesQueuedDuringSummary(bool followUp)
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunnerWithWindow(1000,
            AutoResponse("answer", 920), AutoResponse("summary", 5), AutoResponse("queued answer", 10));
        runner.CompactionSettings = new(ReserveTokens: 100, KeepRecentTokens: 0);
        await foreach (var evt in runner.RunAsync("first"))
            if (evt is CodingAgentCompactionStartEvent)
            {
                if (followUp) runner.FollowUp("queued input");
                else runner.Steer("queued input");
            }
        Assert.Equal(3, provider.Contexts.Count);
        Assert.Contains(provider.Contexts[^1].Messages.OfType<UserMessage>(), message => message.Content.OfType<TextContent>().Any(block => block.Text == "queued input"));
        Assert.Equal(0, runner.PendingMessageCount);
    }

    /// <summary>成功回复报告超过窗口的输入用量时只压缩，不再次请求答案。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AutoCompaction_SuccessfulOverflowCompactsWithoutRetry()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunnerWithWindow(1000, AutoResponse("answer", 1200), AutoResponse("summary", 5));
        runner.CompactionSettings = new(ReserveTokens: 100, KeepRecentTokens: 0);
        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("first")) events.Add(evt);
        var end = Assert.Single(events.OfType<CodingAgentCompactionEndEvent>());
        Assert.Equal("overflow", end.Reason);
        Assert.False(end.WillRetry);
        Assert.Empty(events.OfType<CodingAgentEntryAppendedEvent>());
        Assert.Equal(2, provider.Contexts.Count);
    }

    /// <summary>生成带有真实模型身份和用量的测试响应。</summary>
    /// <param name="text">助手文本。</param>
    /// <param name="input">输入 token 数。</param>
    /// <returns>正常结束的模型响应。</returns>
    private static AssistantMessage AutoResponse(string text, int input) => new([new TextContent(text)])
        { Provider = "synthetic", Model = "test", Api = "test-before-start", Usage = new(input, 1), StopReason = StopReason.EndTurn };
}
