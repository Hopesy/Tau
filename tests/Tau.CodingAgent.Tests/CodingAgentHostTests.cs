using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentHostTests
{
    [Fact]
    public async Task RunAsync_ExitInput_ShowsWelcomeAndGoodbye_WithoutInvokingRunner()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        var exitCode = await host.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Empty(runner.Inputs);

        var output = terminal.FlattenedText();
        Assert.Contains("Tau — Coding Agent\n", output);
        Assert.Contains("> ", output);
        Assert.Contains("Goodbye!\n", output);
    }

    [Fact]
    public async Task RunAsync_RendersAssistantAndToolLifecycle()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("hello");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => GetEvents());
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        var output = terminal.FlattenedText();

        Assert.Single(runner.Inputs);
        Assert.Contains("you> hello\n", output);
        Assert.Contains("thinking", output);
        Assert.Contains("response", output);
        Assert.Contains("tool> [read_file] (done)", output);
        Assert.Contains("Goodbye!\n", output);

        static async IAsyncEnumerable<AgentEvent> GetEvents()
        {
            var partial = new AssistantMessage();
            yield return new MessageUpdateEvent(new ThinkingDeltaEvent(0, "thinking", partial));
            yield return new MessageUpdateEvent(new TextDeltaEvent(1, "response", partial));
            yield return new ToolExecutionStartEvent("tool-1", "read_file");
            yield return new ToolExecutionEndEvent("tool-1", new ToolResult([new TextContent("done")]));
            yield return new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_RunnerThrows_WritesRuntimeError_AndContinuesToExit()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("boom");
        terminal.QueueInput("exit");

        var runner = new FakeCodingAgentRunner((_, _) => throw new InvalidOperationException("runner failed"));
        var host = new CodingAgentHost(new InteractiveConsoleSession(terminal), runner);

        await host.RunAsync();

        var output = terminal.FlattenedText();

        Assert.Contains("error> runner failed\n", output);
        Assert.Contains("Goodbye!\n", output);
    }
}
