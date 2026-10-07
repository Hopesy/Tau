// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>压缩与分支摘要共享重试策略，并输出各自来源的完整重试通知。</summary>
    /// <param name="branchSummary">是否生成分支摘要。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryRetry_RecoversTransientFailureAndPublishesLifecycle(bool branchSummary)
    {
        using var fixture = new Fixture("export default pi=>{};");
        var messages = new List<AssistantMessage>();
        if (!branchSummary) messages.Add(new([new TextContent("original answer")]));
        messages.Add(new([]) { StopReason = StopReason.Error, ErrorMessage = "stream ended before a terminal response event" });
        messages.Add(new([new TextContent("final summary")]) { StopReason = StopReason.EndTurn, Usage = new(17, 4) });
        var (runner, provider) = fixture.CreateRunner(messages.ToArray());
        runner.SummaryRetryOptions = new(2, 0);
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        if (!branchSummary) await foreach (var ignored in runner.RunAsync("original")) { }
        var events = new List<AgentEvent>();
        runner.BackgroundEvent += (evt, _) => { events.Add(evt); return Task.CompletedTask; };
        if (branchSummary)
        {
            var result = await runner.SummarizeBranchAsync([new UserMessage("branch history")]);
            Assert.Contains("final summary", result.Summary);
        }
        else
        {
            var result = await runner.CompactForReasonAsync(null, "overflow", true, default);
            Assert.Contains("final summary", result.Summary);
            Assert.Equal(17, result.Usage!.Value.GetProperty("input").GetInt32());
        }
        Assert.Equal(branchSummary ? 2 : 3, provider.Contexts.Count);
        var retries = events.Where(evt => evt.Type.StartsWith("summarization_retry_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["summarization_retry_scheduled", "summarization_retry_attempt_start", "summarization_retry_finished"], retries.Select(evt => evt.Type));
        var scheduled = Assert.IsType<CodingAgentSummaryRetryScheduledEvent>(retries[0]);
        Assert.Equal(1, scheduled.Attempt);
        Assert.Equal(2, scheduled.MaxAttempts);
        Assert.Equal(0, scheduled.DelayMs);
        var started = Assert.IsType<CodingAgentSummaryRetryAttemptEvent>(retries[1]);
        Assert.Equal(branchSummary ? "branchSummary" : "compaction", started.Source);
        Assert.Equal(branchSummary ? null : "overflow", started.Reason);
        using var protocol = JsonDocument.Parse(CodingAgentRpcHost.SerializeEventLine(started));
        Assert.Equal(started.Source, protocol.RootElement.GetProperty("source").GetString());
        if (branchSummary) Assert.False(protocol.RootElement.TryGetProperty("reason", out _));
        else Assert.Equal("overflow", protocol.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>等待摘要重试时可中止，结束通知唯一且原会话保持不变。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task SummaryRetry_AbortDuringDelayPreservesHistory()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner(new([new TextContent("original")]),
            new([]) { StopReason = StopReason.Error, ErrorMessage = "terminated" });
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        runner.SummaryRetryOptions = new(3, 60000);
        await foreach (var ignored in runner.RunAsync("original input")) { }
        var original = runner.Messages.ToArray();
        var events = new List<AgentEvent>();
        runner.BackgroundEvent += (evt, _) =>
        {
            events.Add(evt);
            if (evt is CodingAgentSummaryRetryScheduledEvent) runner.Abort();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.CompactAsync());
        Assert.Equal(original, runner.Messages);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Single(events.OfType<CodingAgentSummaryRetryFinishedEvent>());
        Assert.Empty(events.OfType<CodingAgentSummaryRetryAttemptEvent>());
        Assert.True(Assert.Single(events.OfType<CodingAgentCompactionEndEvent>()).Aborted);
    }

    /// <summary>额度耗尽不能因 HTTP 429 被误判为临时故障，不产生重试或压缩提交。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task SummaryRetry_DoesNotRetryAccountQuota()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, provider) = fixture.CreateRunner(new([new TextContent("original")]),
            new([]) { StopReason = StopReason.Error, ErrorMessage = "429 insufficient_quota" });
        runner.CompactionSettings = new(KeepRecentTokens: 0);
        runner.SummaryRetryOptions = new(3, 0);
        await foreach (var ignored in runner.RunAsync("original")) { }
        var events = new List<AgentEvent>();
        runner.BackgroundEvent += (evt, _) => { events.Add(evt); return Task.CompletedTask; };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runner.CompactAsync());
        Assert.Contains("insufficient_quota", error.Message);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.Empty(events.OfType<CodingAgentSummaryRetryScheduledEvent>());
        Assert.False(Assert.Single(events.OfType<CodingAgentCompactionEndEvent>()).Aborted);
        Assert.False(CodingAgentRetryClassifier.IsRetryable("429 insufficient_quota", 128000));
    }
}
