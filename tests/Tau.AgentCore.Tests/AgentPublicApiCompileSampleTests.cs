using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.AgentCore.Platform;
using Tau.AgentCore.Proxy;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

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
        var generatedSessionId = UuidV7.Create();
        Assert.True(Guid.TryParse(generatedSessionId, out _));
        Assert.Equal('7', generatedSessionId[14]);
        var harnessRepo = new InMemorySessionRepo();
        var harnessSession = await harnessRepo.CreateAsync("sample-harness-session");
        var harnessEntryId = await harnessSession.AppendMessageAsync(new UserMessage("stored in harness"));
        Assert.Equal(
            "stored in harness",
            Assert.IsType<TextContent>(Assert.Single(
                Assert.IsType<UserMessage>(Assert.Single((await harnessSession.BuildContextAsync()).Messages)).Content)).Text);
        Assert.Equal([harnessEntryId], (await harnessSession.GetEntriesAsync()).Select(static entry => entry.Id));

        var publicHarnessProvider = new SampleProvider(new AssistantMessage([new TextContent("harness done")]));
        var publicHarnessRegistry = new ProviderRegistry();
        publicHarnessRegistry.Register(publicHarnessProvider.Api, publicHarnessProvider);
        var publicHarnessEvents = new List<object>();
        var publicHarness = new AgentHarness<SessionMetadata>(new AgentHarnessOptions<SessionMetadata>
        {
            Session = new AgentHarnessSession<SessionMetadata>(new InMemorySessionStorage<SessionMetadata>()),
            ProviderRegistry = publicHarnessRegistry,
            Model = model,
            SystemPrompt = "harness system",
            Resources = new AgentHarnessResources(
                PromptTemplates:
                [
                    new AgentPromptTemplate("sample", "Sample template", "Run $1 through harness.")
                ])
        });
        using var publicHarnessSubscription = publicHarness.Subscribe(publicHarnessEvents.Add);
        using var publicHarnessContextHook = publicHarness.OnContext((evt, _) =>
            Task.FromResult<AgentHarnessContextResult?>(new(evt.Messages)));
        using var publicHarnessBeforeCompactHook = publicHarness.OnSessionBeforeCompact((_, _) =>
            Task.FromResult<AgentHarnessSessionBeforeCompactResult?>(null));
        using var publicHarnessBeforeTreeHook = publicHarness.OnSessionBeforeTree((_, _) =>
            Task.FromResult<AgentHarnessSessionBeforeTreeResult?>(null));
        var harnessAssistant = await publicHarness
            .PromptFromTemplateAsync("sample", ["public-api"])
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("harness done", ReadText(harnessAssistant.Content));
        Assert.Contains(publicHarnessEvents, static evt => evt is AgentHarnessSavePointEvent);
        Assert.Equal(
            ["message", "message"],
            (await publicHarness.Session.GetBranchAsync()).Select(static entry => entry.Type));

        var agent = new Tau.AgentCore.Agent(new AgentOptions
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
            LogContext = new TauRuntimeLogContext("sample-correlation", "sample-session", "sample-message"),
            PrepareNextTurnAsync = (_, _) => Task.FromResult<AgentLoopTurnUpdate?>(null),
            ShouldStopAfterTurnAsync = (_, _) => Task.FromResult(false)
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
        Assert.False(toolUpdate.Update.Terminate);
        Assert.False(toolUpdate.PartialResult?.Terminate);

        var toolEnd = Assert.Single(events.OfType<ToolExecutionEndEvent>());
        Assert.Equal("echo", toolEnd.ToolName);
        Assert.False(toolEnd.IsError);
        Assert.False(toolEnd.Result.Terminate);

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
            DefaultExecutionMode = ToolExecutionMode.Sequential,
            PrepareNextTurnAsync = (turn, _) => Task.FromResult<AgentLoopTurnUpdate?>(new(
                Context: turn.Context,
                SystemPrompt: "low-level system",
                Tools: [tool],
                Reasoning: ThinkingLevel.Low)),
            ShouldStopAfterTurnAsync = (_, _) => Task.FromResult(false)
        };
        Assert.NotNull(lowLevelRuntime.State);
        Assert.Equal(ToolExecutionMode.Sequential, lowLevelConfig.DefaultExecutionMode);
        lowLevelRuntime.AddMessage(new UserMessage("low-level hello"));
        var lowLevelStream = lowLevelRuntime.RunStream(lowLevelConfig);
        var lowLevelEvents = await CollectAgentEventsAsync(lowLevelStream).WaitAsync(TimeSpan.FromSeconds(5));
        var lowLevelResult = await lowLevelStream.ResultAsync.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<AgentEndEvent>(lowLevelEvents.Last());
        Assert.Equal("done", ReadText(Assert.IsType<AssistantMessage>(lowLevelResult.Last()).Content));
        Assert.Equal("low-level system", lowLevelRuntime.State.SystemPrompt);

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
                await onUpdate(new ToolUpdate(
                    "partial echo",
                    [new TextContent("partial echo")],
                    Terminate: false)).ConfigureAwait(false);
            }

            return new ToolResult(
                [new TextContent(args.GetProperty("text").GetString() ?? string.Empty)],
                Terminate: false);
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
