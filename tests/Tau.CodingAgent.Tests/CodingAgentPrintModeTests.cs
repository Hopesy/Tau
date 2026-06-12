using System.Runtime.CompilerServices;
using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentPrintModeTests
{
    [Fact]
    public async Task RunAsync_StreamsTextDeltasAndReturnsZero()
    {
        var runner = new FakeCodingAgentRunner((_, _) => GetEvents());
        var output = new StringWriter();
        var error = new StringWriter();
        var printMode = new CodingAgentPrintMode(runner, output, error);

        var exitCode = await printMode.RunAsync("hello");

        Assert.Equal(0, exitCode);
        Assert.Equal("Hello world" + Environment.NewLine, output.ToString());
        Assert.Empty(error.ToString());
        Assert.Single(runner.Inputs);
        Assert.Equal("hello", runner.Inputs[0]);

        static async IAsyncEnumerable<AgentEvent> GetEvents()
        {
            var partial = new AssistantMessage();
            yield return new MessageUpdateEvent(new TextDeltaEvent(0, "Hello ", partial));
            yield return new MessageUpdateEvent(new TextDeltaEvent(1, "world", partial));
            yield return new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_WithImagePromptUsesContentBlocks()
    {
        var runner = new FakeCodingAgentRunner((_, _) => GetEvents());
        var output = new StringWriter();
        var error = new StringWriter();
        var printMode = new CodingAgentPrintMode(runner, output, error);
        var prompt = new CodingAgentInitialPrompt(
            "describe",
            [new ImageContent("aGVsbG8=", "image/png")]);

        var exitCode = await printMode.RunAsync(prompt);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Empty(runner.Inputs);
        var content = Assert.Single(runner.ContentInputs);
        Assert.Equal("describe", Assert.IsType<TextContent>(content[0]).Text);
        var image = Assert.IsType<ImageContent>(content[1]);
        Assert.Equal("aGVsbG8=", image.Data);
        Assert.Equal("image/png", image.MimeType);

        static async IAsyncEnumerable<AgentEvent> GetEvents()
        {
            yield return new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_AgentEndError_WritesToErrorAndReturnsOne()
    {
        var runner = new FakeCodingAgentRunner((_, _) => GetEvents());
        var output = new StringWriter();
        var error = new StringWriter();
        var printMode = new CodingAgentPrintMode(runner, output, error);

        var exitCode = await printMode.RunAsync("hello");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("provider rate limited", error.ToString());

        static async IAsyncEnumerable<AgentEvent> GetEvents()
        {
            yield return new AgentEndEvent("provider rate limited");
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_RunnerThrows_WritesToErrorAndReturnsOne()
    {
        var runner = new FakeCodingAgentRunner((_, _) => Throw());
        var output = new StringWriter();
        var error = new StringWriter();
        var printMode = new CodingAgentPrintMode(runner, output, error);

        var exitCode = await printMode.RunAsync("hello");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("kaboom", error.ToString());

        static async IAsyncEnumerable<AgentEvent> Throw()
        {
            await Task.Yield();
            throw new InvalidOperationException("kaboom");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task RunAsync_Cancelled_WritesCancelledMessageAndReturnsOne()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeCodingAgentRunner((_, ct) => GetEventsAndCancel(cts, ct));
        var output = new StringWriter();
        var error = new StringWriter();
        var printMode = new CodingAgentPrintMode(runner, output, error);

        var exitCode = await printMode.RunAsync("hello", cts.Token);

        Assert.Equal(1, exitCode);
        Assert.Contains("Cancelled", error.ToString());

        static async IAsyncEnumerable<AgentEvent> GetEventsAndCancel(
            CancellationTokenSource cts,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            cts.Cancel();
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield break;
        }
    }

    [Fact]
    public async Task RunAsync_JsonMode_EmitsEachEventAsJsonLine()
    {
        var runner = new FakeCodingAgentRunner((_, _) => GetEvents());
        var output = new StringWriter();
        var error = new StringWriter();
        var printMode = new CodingAgentPrintMode(runner, output, error, jsonMode: true);

        var exitCode = await printMode.RunAsync("hello");

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        var lines = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        // Each line is a standalone JSON object carrying the upstream event "type".
        Assert.Contains("\"type\":\"message_update\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"delta\":\"Hi\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"type\":\"agent_end\"", lines[1], StringComparison.Ordinal);

        static async IAsyncEnumerable<AgentEvent> GetEvents()
        {
            var partial = new AssistantMessage();
            yield return new MessageUpdateEvent(new TextDeltaEvent(0, "Hi", partial));
            yield return new AgentEndEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_JsonMode_AgentEndError_ReturnsOneWithoutStderr()
    {
        var runner = new FakeCodingAgentRunner((_, _) => GetEvents());
        var output = new StringWriter();
        var error = new StringWriter();
        var printMode = new CodingAgentPrintMode(runner, output, error, jsonMode: true);

        var exitCode = await printMode.RunAsync("hello");

        Assert.Equal(1, exitCode);
        // Upstream json mode keeps the error on the event stream and leaves stderr quiet.
        Assert.Empty(error.ToString());
        Assert.Contains("\"errorMessage\":\"provider rate limited\"", output.ToString(), StringComparison.Ordinal);

        static async IAsyncEnumerable<AgentEvent> GetEvents()
        {
            yield return new AgentEndEvent("provider rate limited");
            await Task.CompletedTask;
        }
    }
}
