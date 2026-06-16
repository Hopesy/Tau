using System.Text.Json;
using Tau.Agent.Platform;
using Tau.Agent.Proxy;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;
using Tau.Ai.Streaming;

namespace Tau.Agent.Tests;

public sealed class AgentPublicApiCompileSampleTests
{
    [Fact]
    public async Task PublicApiSample_CompilesAndRunsFacadeRuntimeProxyAndEventSurface()
    {
        var provider = new SampleProvider(
            new AssistantMessage([new ToolCallContent("call-1", "echo", """{"text":"hello"}""")]),
            new AssistantMessage([new TextContent("done")]));
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        var model = new Model
        {
            Provider = "sample",
            Id = "sample-model",
            Name = "Sample Model",
            Api = provider.Api
        };
        var tool = new EchoTool();
        var events = new List<AgentEvent>();

        var agent = new Tau.Agent.Agent(new AgentOptions
        {
            Model = model,
            ProviderRegistry = registry,
            SystemPrompt = "system",
            Tools = [tool],
            SteeringMode = AgentQueueMode.All,
            FollowUpMode = AgentQueueMode.OneAtATime,
            ToolExecution = ToolExecutionMode.Sequential,
            StreamOptions = new SimpleStreamOptions
            {
                Temperature = 0,
                Reasoning = ThinkingLevel.Low,
                SessionId = "sample-session"
            },
            LogSink = NullTauLogSink.Instance,
            LogContext = new TauRuntimeLogContext("sample-correlation", "sample-session", "sample-message")
        });

        using var subscription = agent.Subscribe((evt, _) =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        });

        agent.Steer(new UserMessage("queued steering"));
        agent.FollowUp(new UserMessage("queued follow-up"));
        Assert.True(agent.HasQueuedMessages);
        agent.ClearAllQueues();
        Assert.False(agent.HasQueuedMessages);

        await agent.PromptAsync("hello", Array.Empty<ImageContent>()).WaitAsync(TimeSpan.FromSeconds(5));

        var toolStart = Assert.Single(events.OfType<ToolExecutionStartEvent>());
        Assert.Equal("echo", toolStart.ToolName);
        Assert.Equal("""{"text":"hello"}""", toolStart.Args);

        var toolUpdate = Assert.Single(events.OfType<ToolExecutionUpdateEvent>());
        Assert.Equal("echo", toolUpdate.ToolName);
        Assert.Equal("""{"text":"hello"}""", toolUpdate.Args);
        Assert.Equal("partial echo", ReadText(toolUpdate.PartialResult?.Content ?? []));

        var toolEnd = Assert.Single(events.OfType<ToolExecutionEndEvent>());
        Assert.Equal("echo", toolEnd.ToolName);
        Assert.False(toolEnd.IsError);

        var toolTurn = events.OfType<TurnEndEvent>().Single(turn => turn.ToolResults.Count == 1);
        Assert.Equal("hello", ReadText(Assert.Single(toolTurn.ToolResults).Content));

        var end = Assert.IsType<AgentEndEvent>(events.Last());
        Assert.Equal(4, end.Messages.Count);
        Assert.Equal("sample-model", agent.State.Model?.Id);
        Assert.Equal("system", agent.State.SystemPrompt);
        Assert.False(agent.State.IsStreaming);

        var lowLevelRuntime = new AgentRuntime();
        var lowLevelConfig = new AgentLoopConfig
        {
            Model = model,
            ProviderRegistry = registry,
            Tools = [tool],
            DefaultExecutionMode = ToolExecutionMode.Sequential
        };
        Assert.NotNull(lowLevelRuntime.State);
        Assert.Equal(ToolExecutionMode.Sequential, lowLevelConfig.DefaultExecutionMode);
        lowLevelRuntime.AddMessage(new UserMessage("low-level hello"));
        var lowLevelStream = lowLevelRuntime.RunStream(lowLevelConfig);
        var lowLevelEvents = await CollectAgentEventsAsync(lowLevelStream).WaitAsync(TimeSpan.FromSeconds(5));
        var lowLevelResult = await lowLevelStream.ResultAsync.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<AgentEndEvent>(lowLevelEvents.Last());
        Assert.Equal("done", ReadText(Assert.IsType<AssistantMessage>(lowLevelResult.Last()).Content));

        var proxyProvider = new ProxyStreamProvider();
        var proxyOptions = new ProxyStreamOptions
        {
            ProxyUrl = "https://proxy.example.invalid",
            AuthToken = "sample-token",
            StreamPath = "/api/stream"
        };
        Assert.Equal(ProxyStreamProvider.DefaultApi, proxyProvider.Api);
        Assert.Equal("/api/stream", proxyOptions.StreamPath);

        var platformRegistry = new ProviderRegistry();
        var faux = Faux.Register(platformRegistry);
        faux.SetResponses([
            Faux.AssistantMessage(
                [Faux.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "platform hello" }, "platform-call")],
                stopReason: StopReason.ToolUse),
            Faux.AssistantMessage("platform done")
        ]);
        var sessions = new InMemoryAgentSessionStore();
        var platformApp = AgentApplication.CreateBuilder()
            .UseProviderRegistry(platformRegistry)
            .UseModel(faux.GetModel())
            .UseSystemPrompt("platform system")
            .UseSessionId("platform-session")
            .UseSessionStore(sessions)
            .UseLogSink(NullTauLogSink.Instance)
            .AddTool(
                "echo",
                "Echo",
                "Echoes text.",
                CreateEchoSchema(),
                (context, _) => new ToolResult([
                    new TextContent(context.Arguments.GetProperty("text").GetString() ?? string.Empty)
                ]))
            .Build();

        var platformResult = await platformApp.PromptAsync("run platform").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(platformResult.IsSuccess);
        Assert.True(platformResult.SavedSession);
        Assert.Equal("platform done", platformResult.AssistantText);
        Assert.Equal("platform-session", platformResult.LogContext.SessionId);
        Assert.Equal("platform-session", sessions.Load("platform-session")?.SessionId);

        var preparedPlatformApp = AgentApplication.CreateBuilder()
            .UseProviderRegistry(platformRegistry)
            .UseModel(faux.GetModel())
            .UseSystemPrompt("platform system")
            .UseSessionId("prepared-platform-session")
            .UseSessionStore(new InMemoryAgentSessionStore())
            .UseLogSink(NullTauLogSink.Instance)
            .AddTool(
                "echo",
                "Echo",
                "Echoes text.",
                CreateEchoSchema(),
                (context, _) => new ToolResult([
                    new TextContent(context.Arguments.GetProperty("text").GetString() ?? string.Empty)
                ]),
                prepareArguments: (rawArgs, _) => new ValueTask<JsonElement>(rawArgs))
            .Build();

        Assert.NotNull(preparedPlatformApp);
    }

    private static string ReadText(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

    private static async Task<List<AgentEvent>> CollectAgentEventsAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var collected = new List<AgentEvent>();
        await foreach (var evt in events)
        {
            collected.Add(evt);
        }

        return collected;
    }

    private static JsonElement CreateEchoSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
                "type": "object",
                "properties": {
                    "text": { "type": "string" }
                },
                "required": ["text"]
            }
            """);
        return document.RootElement.Clone();
    }

    private sealed class SampleProvider(params AssistantMessage[] messages) : IStreamProvider
    {
        private int _calls;

        public string Api => "sample-public-agent-api";

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete();

        private AssistantMessageStream Complete()
        {
            var message = _calls < messages.Length
                ? messages[_calls++]
                : new AssistantMessage([new TextContent("done")]);
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(message));
            return stream;
        }
    }

    private sealed class EchoTool : IAgentTool
    {
        private readonly JsonElement _schema = CreateSchema();

        public string Name => "echo";
        public string Label => "Echo";
        public string Description => "Echoes text.";
        public JsonElement ParameterSchema => _schema;

        public async Task<ToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct = default,
            Func<ToolUpdate, Task>? onUpdate = null)
        {
            if (onUpdate is not null)
            {
                await onUpdate(new ToolUpdate("partial echo", [new TextContent("partial echo")])).ConfigureAwait(false);
            }

            return new ToolResult([new TextContent(args.GetProperty("text").GetString() ?? string.Empty)]);
        }

        private static JsonElement CreateSchema()
        {
            using var document = JsonDocument.Parse(
                """
                {
                    "type": "object",
                    "properties": {
                        "text": { "type": "string" }
                    },
                    "required": ["text"]
                }
                """);
            return document.RootElement.Clone();
        }
    }
}
