// 作者：xxx
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【命令输出回归】文本只发布最终回复，JSON 继续保留完整事件。</summary>
public sealed class CodingAgentPrintModeParityTests
{
    /// <summary>工具前说明、流式草稿和思考不得混入可供管道消费的最终正文。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Text_PrintsFinalBlocksAfterSuccessfulRunOnly()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var final = new AssistantMessage([new ThinkingContent("hidden"), new TextContent("final one"), new TextContent("final two")]) { StopReason = StopReason.EndTurn };
        var runner = new FakeCodingAgentRunner((_, _) => Events());
        var mode = new CodingAgentPrintMode(runner, output, error);
        Assert.Equal(0, await mode.RunAsync("run"));
        Assert.Equal("final one" + Environment.NewLine + "final two" + Environment.NewLine, output.ToString());
        Assert.Empty(error.ToString());

        /// <summary>发送包含工具中间回合和终值修订的事件序列。</summary>
        /// <returns>测试事件。</returns>
        async IAsyncEnumerable<AgentEvent> Events()
        {
            yield return new MessageUpdateEvent(new TextDeltaEvent(0, "working draft", new AssistantMessage()));
            Assert.Empty(output.ToString());
            yield return new MessageEndEvent(new AssistantMessage([new TextContent("calling tool")]) { StopReason = StopReason.ToolUse });
            yield return new MessageEndEvent(new ToolResultMessage("call", [new TextContent("tool result")]));
            yield return new MessageUpdateEvent(new TextDeltaEvent(0, "superseded", new AssistantMessage()));
            yield return new MessageEndEvent(final);
            Assert.Empty(output.ToString());
            yield return new AgentEndEvent(messages: [final]);
            await Task.CompletedTask;
        }
    }

    /// <summary>依据最终消息判断成功、截断和失败，失败不输出已经收到的正文。</summary>
    /// <param name="reason">助手终态。</param>
    /// <param name="json">是否输出 JSON。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData(StopReason.EndTurn, false)]
    [InlineData(StopReason.MaxTokens, false)]
    [InlineData(StopReason.Error, false)]
    [InlineData(StopReason.Aborted, false)]
    [InlineData(StopReason.Error, true)]
    [InlineData(StopReason.Aborted, true)]
    public async Task FinalStatus_ControlsOutputAndExitCode(StopReason reason, bool json)
    {
        var failed = reason is StopReason.Error or StopReason.Aborted;
        var final = new AssistantMessage([new TextContent("partial")]) { StopReason = reason };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((_, _) => Sequence([
            new MessageUpdateEvent(new TextDeltaEvent(0, "partial", final)), new MessageEndEvent(final), new AgentEndEvent(messages: [final])]));
        Assert.Equal(failed ? 1 : 0, await new CodingAgentPrintMode(runner, output, error, json).RunAsync("run"));
        if (json)
        {
            var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            using var last = JsonDocument.Parse(lines[^1]);
            Assert.Equal("agent_end", last.RootElement.GetProperty("type").GetString());
            Assert.Empty(error.ToString());
        }
        else
        {
            Assert.Equal(failed ? "" : "partial" + Environment.NewLine, output.ToString());
            Assert.Equal(failed, error.ToString().Contains("Request ", StringComparison.Ordinal));
        }
    }

    /// <summary>agent_end 的替换快照优先于 message_end，避免打印扩展替换前的旧文本。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Text_UsesAgentEndSnapshot()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((_, _) => Sequence([
            new MessageEndEvent(new AssistantMessage([new TextContent("old")])),
            new AgentEndEvent(messages: [new AssistantMessage([new TextContent("replacement")])]) ]));
        Assert.Equal(0, await new CodingAgentPrintMode(runner, output, error).RunAsync("run"));
        Assert.Equal("replacement" + Environment.NewLine, output.ToString());
    }

    /// <summary>最终消息不是助手时不回退打印早先的工具前说明。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Text_DoesNotPrintEarlierAssistantWhenToolResultIsLast()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((_, _) => Sequence([
            new MessageEndEvent(new AssistantMessage([new TextContent("intermediate")])),
            new AgentEndEvent(messages: [new ToolResultMessage("call", [new TextContent("result")])])]));
        Assert.Equal(0, await new CodingAgentPrintMode(runner, output, error).RunAsync("run"));
        Assert.Empty(output.ToString());
    }

    /// <summary>已经有成功草稿但运行随后抛错时，stdout 仍为空。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Text_DoesNotCommitBeforeException()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeCodingAgentRunner((_, _) => Fail());
        Assert.Equal(1, await new CodingAgentPrintMode(runner, output, error).RunAsync("run"));
        Assert.Empty(output.ToString());
        Assert.Contains("failed after draft", error.ToString());

        /// <summary>在完成草稿之后模拟运行错误。</summary>
        /// <returns>测试事件。</returns>
        static async IAsyncEnumerable<AgentEvent> Fail()
        {
            yield return new MessageEndEvent(new AssistantMessage([new TextContent("draft")]));
            await Task.Yield();
            throw new IOException("failed after draft");
        }
    }

    /// <summary>枚举器因取消静默结束时仍返回失败，且不提交最后草稿。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Text_SilentCancellationDoesNotCommitDraft()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancel = new CancellationTokenSource();
        var runner = new FakeCodingAgentRunner((_, ct) => Cancel(ct));
        Assert.Equal(1, await new CodingAgentPrintMode(runner, output, error).RunAsync("run", cancel.Token));
        Assert.Empty(output.ToString());
        Assert.Contains("Cancelled", error.ToString());

        /// <summary>发送草稿后取消并直接结束。</summary>
        /// <param name="ct">枚举取消信号。</param>
        /// <returns>测试事件。</returns>
        async IAsyncEnumerable<AgentEvent> Cancel([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new MessageEndEvent(new AssistantMessage([new TextContent("draft")]));
            await cancel.CancelAsync();
        }
    }

    /// <summary>依次提供测试事件。</summary>
    /// <param name="events">测试序列。</param>
    /// <returns>异步事件序列。</returns>
    private static async IAsyncEnumerable<AgentEvent> Sequence(AgentEvent[] events)
    {
        foreach (var item in events) yield return item;
        await Task.CompletedTask;
    }
}
