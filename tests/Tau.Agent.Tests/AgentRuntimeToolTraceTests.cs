using System.Text.Json;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Agent.Tests;

public sealed class AgentRuntimeToolTraceTests
{
    [Fact]
    public async Task RunAsync_LogsToolExecutionTrace()
    {
        var sink = new CapturingLogSink();
        var logContext = new TauRuntimeLogContext("corr-tool", "session-tool", "message-tool");
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use tool"));

        await DrainAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(new ToolCallContent("tool-1", "echo", """{"text":"hello"}""")),
            sink,
            [new EchoTool()],
            logContext)));

        var start = Assert.Single(sink.Events, evt => evt.Event == "execution.start");
        Assert.Equal("tool", start.Category);
        Assert.Equal("tool-1", start.Fields["toolCallId"]);
        Assert.Equal("echo", start.Fields["toolName"]);
        Assert.Equal("parallel", start.Fields["executionMode"]);
        Assert.Equal("""{"text":"hello"}""".Length.ToString(System.Globalization.CultureInfo.InvariantCulture), start.Fields["argumentBytes"]);
        Assert.Equal("corr-tool", start.Fields["correlationId"]);
        Assert.Equal("session-tool", start.Fields["sessionId"]);
        Assert.Equal("message-tool", start.Fields["messageId"]);

        var end = Assert.Single(sink.Events, evt => evt.Event == "execution.end");
        Assert.Equal("tool", end.Category);
        Assert.Equal("tool-1", end.Fields["toolCallId"]);
        Assert.Equal("echo", end.Fields["toolName"]);
        Assert.Equal("true", end.Fields["success"]);
        Assert.Equal("false", end.Fields["isError"]);
        Assert.Equal("none", end.Fields["failureKind"]);
        Assert.Equal("1", end.Fields["contentBlockCount"]);
        Assert.Equal("hello".Length.ToString(System.Globalization.CultureInfo.InvariantCulture), end.Fields["textBytes"]);
        Assert.True(end.Fields.ContainsKey("durationMs"));
        Assert.Equal("corr-tool", end.Fields["correlationId"]);
        Assert.Equal("session-tool", end.Fields["sessionId"]);
        Assert.Equal("message-tool", end.Fields["messageId"]);
        Assert.False(end.Fields.ContainsKey("arguments"));
        Assert.False(end.Fields.ContainsKey("result"));
    }

    [Fact]
    public async Task RunAsync_LogsNotFoundToolFailureKind()
    {
        var sink = new CapturingLogSink();
        var logContext = new TauRuntimeLogContext("corr-missing", "session-missing", "message-missing");
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("use missing tool"));

        await DrainAsync(runtime.RunAsync(CreateConfig(
            new ScriptedProvider(new ToolCallContent("tool-missing", "missing_tool", "{}")),
            sink,
            [],
            logContext)));

        var end = Assert.Single(sink.Events, evt => evt.Event == "execution.end");
        Assert.Equal("tool-missing", end.Fields["toolCallId"]);
        Assert.Equal("missing_tool", end.Fields["toolName"]);
        Assert.Equal("false", end.Fields["success"]);
        Assert.Equal("true", end.Fields["isError"]);
        Assert.Equal("not-found", end.Fields["failureKind"]);
        Assert.Equal("1", end.Fields["contentBlockCount"]);
        Assert.True(int.Parse(end.Fields["textBytes"]!, System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("corr-missing", end.Fields["correlationId"]);
        Assert.Equal("session-missing", end.Fields["sessionId"]);
        Assert.Equal("message-missing", end.Fields["messageId"]);
    }

    private static AgentLoopConfig CreateConfig(
        IStreamProvider provider,
        ITauLogSink logSink,
        IReadOnlyList<IAgentTool> tools,
        TauRuntimeLogContext? logContext = null)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);

        return new AgentLoopConfig
        {
            Model = new Model
            {
                Provider = "test",
                Id = "test-model",
                Name = "Test Model",
                Api = provider.Api
            },
            ProviderRegistry = registry,
            Tools = tools,
            LogSink = logSink,
            LogContext = logContext
        };
    }

    private static async Task DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private sealed class ScriptedProvider(ToolCallContent toolCall) : IStreamProvider
    {
        private int _calls;

        public string Api => "test-scripted";

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete();

        private AssistantMessageStream Complete()
        {
            var stream = new AssistantMessageStream();
            var message = _calls++ == 0
                ? new AssistantMessage([toolCall])
                : new AssistantMessage([new TextContent("done")]);
            stream.Push(new DoneEvent(message));
            return stream;
        }
    }

    private sealed class EchoTool : IAgentTool
    {
        public string Name => "echo";
        public string Label => "Echo";
        public string Description => "Echoes text.";
        public JsonElement ParameterSchema => JsonDocument.Parse("{}").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct = default,
            Func<ToolUpdate, Task>? onUpdate = null)
        {
            var text = args.TryGetProperty("text", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            return Task.FromResult(new ToolResult([new TextContent(text)]));
        }
    }

    private sealed class CapturingLogSink : ITauLogSink
    {
        public List<TauLogEvent> Events { get; } = new();

        public void Log(TauLogEvent evt) => Events.Add(evt);
    }
}
