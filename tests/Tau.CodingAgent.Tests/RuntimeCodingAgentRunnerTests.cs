using System.Runtime.CompilerServices;
using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;
using Tau.Agent.Runtime;
using Tau.CodingAgent.Runtime;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;

namespace Tau.CodingAgent.Tests;

public class RuntimeCodingAgentRunnerTests
{
    [Fact]
    public void Create_WithExplicitProviderAndModel_UsesRequestedModel()
    {
        var runner = RuntimeCodingAgentRunner.Create("google", "gemini-2.5-pro");

        Assert.Equal("google", runner.Model.Provider, ignoreCase: true);
        Assert.Equal("gemini-2.5-pro", runner.Model.Id, ignoreCase: true);
        Assert.Empty(runner.Messages);
    }

    [Fact]
    public void Create_WithCanonicalModelReference_UsesRequestedModel()
    {
        var runner = RuntimeCodingAgentRunner.Create("google-antigravity", "google-antigravity/claude-opus-4-6-thinking");

        Assert.Equal("google-antigravity", runner.Model.Provider, ignoreCase: true);
        Assert.Equal("claude-opus-4-6-thinking", runner.Model.Id, ignoreCase: true);
    }



    [Fact]
    public void SelectModel_UpdatesRuntimeModel()
    {
        var runner = RuntimeCodingAgentRunner.Create("openai", "gpt-5.4");

        var selected = runner.SelectModel("google", "gemini-2.5-pro");

        Assert.Equal("google", selected.Provider, ignoreCase: true);
        Assert.Equal("gemini-2.5-pro", selected.Id, ignoreCase: true);
        Assert.Equal("google", runner.Model.Provider, ignoreCase: true);
        Assert.Equal("gemini-2.5-pro", runner.Model.Id, ignoreCase: true);
    }

    [Fact]
    public void Create_WithInitialMessages_RehydratesConversationState()
    {
        var runner = RuntimeCodingAgentRunner.Create(
            "openai",
            "gpt-5.4",
            [new UserMessage("hello"), new AssistantMessage([new TextContent("world")])]);

        Assert.Equal(2, runner.Messages.Count);
        Assert.IsType<UserMessage>(runner.Messages[0]);
        Assert.IsType<AssistantMessage>(runner.Messages[1]);
    }

    [Fact]
    public void ResetSession_ClearsConversationStateAndKeepsModel()
    {
        var runner = RuntimeCodingAgentRunner.Create(
            "openai",
            "gpt-5.4",
            [new UserMessage("hello"), new AssistantMessage([new TextContent("world")])]);

        runner.ResetSession();

        Assert.Empty(runner.Messages);
        Assert.Equal("openai", runner.Model.Provider, ignoreCase: true);
        Assert.Equal("gpt-5.4", runner.Model.Id, ignoreCase: true);
    }

    [Fact]
    public void GetSessionStats_CountsFlatSessionMessagesAndToolCalls()
    {
        var sessionFile = Path.Combine(Path.GetTempPath(), "tau-session-stats.json");
        var runner = RuntimeCodingAgentRunner.Create(
            "openai",
            "gpt-5.4",
            [
                new UserMessage("hello"),
                new AssistantMessage(
                    [
                        new TextContent("thinking"),
                        new ToolCallContent("tool-1", "read_file", "{}")
                    ]),
                new ToolResultMessage("tool-1", [new TextContent("done")])
            ]);
        runner.SessionName = "stats session";

        var stats = runner.GetSessionStats(sessionFile);

        Assert.Equal("openai", stats.Provider, ignoreCase: true);
        Assert.Equal("gpt-5.4", stats.Model, ignoreCase: true);
        Assert.Equal("stats session", stats.SessionName);
        Assert.Equal(3, stats.TotalMessages);
        Assert.Equal(1, stats.UserMessages);
        Assert.Equal(1, stats.AssistantMessages);
        Assert.Equal(1, stats.ToolResultMessages);
        Assert.Equal(1, stats.ToolCalls);
        Assert.Equal(CodingAgentTokenEstimator.Estimate(runner.Messages), stats.EstimatedTokens);
        Assert.Equal(runner.Model.ContextWindow, stats.ContextWindowTokens);
        Assert.Equal(sessionFile, stats.SessionFile);
    }

    [Fact]
    public void TokenEstimator_IncludesPendingInputAndStructuredContent()
    {
        var tokens = CodingAgentTokenEstimator.Estimate(
            [
                new UserMessage("12345678"),
                new AssistantMessage(
                    [
                        new ThinkingContent("think"),
                        new ToolCallContent("tool-1", "read_file", "{\"path\":\"README.md\"}")
                    ]),
                new ToolResultMessage("tool-1", [new TextContent("done")])
            ],
            "next");

        Assert.True(tokens >= 12);
    }

    [Fact]
    public void AutoCompactionOptions_FromEnvironment_ReadsPositiveThreshold()
    {
        var oldThreshold = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_AUTO_COMPACT_TOKENS");
        var oldInstructions = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS");

        try
        {
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_AUTO_COMPACT_TOKENS", "1024");
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS", " keep blockers ");

            var options = CodingAgentAutoCompactionOptions.FromEnvironment();

            Assert.True(options.IsEnabled);
            Assert.Equal(1024, options.ThresholdTokens);
            Assert.Equal("keep blockers", options.Instructions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_AUTO_COMPACT_TOKENS", oldThreshold);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS", oldInstructions);
        }
    }

    [Fact]
    public async Task CompactAsync_ReplacesConversationWithCompactionSummaryMessage()
    {
        var model = new Model
        {
            Provider = "test-provider",
            Id = "test-model",
            Name = "Test Model",
            Api = "test-api"
        };

        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("first"));
        runtime.AddMessage(new AssistantMessage([new TextContent("second")]));

        var registry = new ProviderRegistry();
        registry.Register("test-api", () => new CompactingTestProvider(), sourceId: "test");

        var runner = new RuntimeCodingAgentRunner(
            runtime,
            new AgentLoopConfig
            {
                Model = model,
                ProviderRegistry = registry,
                Tools = [],
                SystemPrompt = "test",
                StreamOptions = new SimpleStreamOptions { MaxTokens = 512 }
            },
            new Tau.Ai.Registry.ModelCatalog());

        var result = await runner.CompactAsync("focus on current blocker");

        Assert.Equal("summary result", result.Summary);
        Assert.Equal(2, result.MessagesBefore);
        Assert.Equal(1, result.MessagesAfter);

        var compacted = Assert.Single(runner.Messages);
        var user = Assert.IsType<UserMessage>(compacted);
        var text = Assert.IsType<TextContent>(Assert.Single(user.Content)).Text;
        Assert.Contains("The conversation history before this point was compacted", text);
        Assert.Contains("summary result", text);
    }

    [Fact]
    public async Task RunAsync_EmitsRunStartAndRunEndOnHappyPath()
    {
        var sink = new RecordingLogSink();
        var runner = CreateInstrumentedRunner(sink, () =>
        {
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent("ok")])));
            return stream;
        });

        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("hello"))
        {
            events.Add(evt);
        }

        var startEvent = Assert.Single(sink.Events, e => e.Event == "run.start");
        Assert.Equal("agent", startEvent.Category);
        Assert.Equal("test-provider", startEvent.Fields["provider"]);
        Assert.Equal("test-model", startEvent.Fields["model"]);
        Assert.Equal("5", startEvent.Fields["inputBytes"]);

        var endEvent = Assert.Single(sink.Events, e => e.Event == "run.end");
        Assert.Equal("agent", endEvent.Category);
        Assert.True(int.Parse(endEvent.Fields["elapsedMs"]!, System.Globalization.CultureInfo.InvariantCulture) >= 0);

        Assert.DoesNotContain(sink.Events, e => e.Event == "run.error" || e.Event == "run.cancel");
    }

    [Fact]
    public async Task RunAsync_EmitsRunErrorWhenProviderThrows()
    {
        var sink = new RecordingLogSink();
        var runner = CreateInstrumentedRunner(sink, () => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runner.RunAsync("hi")) { }
        });

        Assert.Contains(sink.Events, e => e.Event == "run.start");
        var errorEvent = Assert.Single(sink.Events, e => e.Event == "run.error");
        Assert.Equal("InvalidOperationException", errorEvent.Fields["error"]);
        Assert.Equal("boom", errorEvent.Fields["message"]);
        Assert.DoesNotContain(sink.Events, e => e.Event == "run.end");
    }

    [Fact]
    public async Task RunAsync_IncludesContextFilesInGeneratedSystemPrompt()
    {
        string? capturedPrompt = null;
        var runner = CreatePromptCapturingRunner(
            context => capturedPrompt = context.SystemPrompt,
            contextFiles:
            [
                new CodingAgentContextFile(
                    Path.Combine(Path.GetTempPath(), "AGENTS.md"),
                    "follow project rules",
                    "project")
            ]);

        await foreach (var _ in runner.RunAsync("hello")) { }

        Assert.NotNull(capturedPrompt);
        Assert.Contains("# Project Context", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("/AGENTS.md", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("follow project rules", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshSystemPromptResources_UpdatesGeneratedPromptContextFiles()
    {
        string? capturedPrompt = null;
        var runner = CreatePromptCapturingRunner(context => capturedPrompt = context.SystemPrompt);

        var refreshed = runner.RefreshSystemPromptResources(
            [],
            [new CodingAgentContextFile(Path.Combine(Path.GetTempPath(), "CLAUDE.md"), "new context", "project")]);

        Assert.True(refreshed);
        await foreach (var _ in runner.RunAsync("hello")) { }

        Assert.NotNull(capturedPrompt);
        Assert.Contains("/CLAUDE.md", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("new context", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshSystemPromptResources_WhenSystemPromptIsCustom_DoesNotOverwritePrompt()
    {
        string? capturedPrompt = null;
        var runner = RuntimeCodingAgentRunner.Create(
            "test-provider",
            "test-model",
            toolsOverride: [],
            systemPromptOverride: "custom system prompt",
            contextFiles:
            [
                new CodingAgentContextFile(Path.Combine(Path.GetTempPath(), "AGENTS.md"), "initial context", "project")
            ],
            providerRegistryOverride: CreatePromptCapturingRegistry(context => capturedPrompt = context.SystemPrompt),
            modelCatalogOverride: CreatePromptCapturingModelCatalog());

        var refreshed = runner.RefreshSystemPromptResources(
            [],
            [new CodingAgentContextFile(Path.Combine(Path.GetTempPath(), "CLAUDE.md"), "new context", "project")]);

        Assert.False(refreshed);
        await foreach (var _ in runner.RunAsync("hello")) { }

        Assert.Equal("custom system prompt", capturedPrompt);
    }

    private static RuntimeCodingAgentRunner CreateInstrumentedRunner(
        ITauLogSink sink,
        Func<AssistantMessageStream> streamFactory)
    {
        var model = new Model
        {
            Provider = "test-provider",
            Id = "test-model",
            Name = "Test",
            Api = "instrumented-test"
        };
        var registry = new ProviderRegistry();
        registry.Register("instrumented-test", () => new FactoryStreamProvider(streamFactory), sourceId: "test");
        var runtime = new AgentRuntime();
        return new RuntimeCodingAgentRunner(
            runtime,
            new AgentLoopConfig
            {
                Model = model,
                ProviderRegistry = registry,
                Tools = [],
                SystemPrompt = "test",
                StreamOptions = new SimpleStreamOptions { MaxTokens = 256 }
            },
            new Tau.Ai.Registry.ModelCatalog(),
            logSink: sink);
    }

    private static RuntimeCodingAgentRunner CreatePromptCapturingRunner(
        Action<LlmContext> capture,
        IReadOnlyList<CodingAgentContextFile>? contextFiles = null)
    {
        return RuntimeCodingAgentRunner.Create(
            "test-provider",
            "test-model",
            toolsOverride: [],
            contextFiles: contextFiles,
            providerRegistryOverride: CreatePromptCapturingRegistry(capture),
            modelCatalogOverride: CreatePromptCapturingModelCatalog());
    }

    private static ProviderRegistry CreatePromptCapturingRegistry(Action<LlmContext> capture)
    {
        var registry = new ProviderRegistry();
        registry.Register("prompt-capture-test", () => new PromptCapturingProvider(capture), sourceId: "test");
        return registry;
    }

    private static Tau.Ai.Registry.ModelCatalog CreatePromptCapturingModelCatalog()
    {
        var catalog = new Tau.Ai.Registry.ModelCatalog();
        catalog.RegisterModel(new Model
        {
            Provider = "test-provider",
            Id = "test-model",
            Name = "Test Model",
            Api = "prompt-capture-test"
        });
        return catalog;
    }
}

file sealed class RecordingLogSink : ITauLogSink
{
    public List<TauLogEvent> Events { get; } = [];
    public void Log(TauLogEvent evt) => Events.Add(evt);
}

file sealed class FactoryStreamProvider : IStreamProvider
{
    private readonly Func<AssistantMessageStream> _factory;
    public FactoryStreamProvider(Func<AssistantMessageStream> factory) => _factory = factory;
    public string Api => "instrumented-test";
    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) => _factory();
    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => _factory();
}

file sealed class PromptCapturingProvider : IStreamProvider
{
    private readonly Action<LlmContext> _capture;
    public PromptCapturingProvider(Action<LlmContext> capture) => _capture = capture;
    public string Api => "prompt-capture-test";
    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
        CreateStream(context);

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        CreateStream(context);

    private AssistantMessageStream CreateStream(LlmContext context)
    {
        _capture(context);
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("ok")])));
        return stream;
    }
}

file sealed class CompactingTestProvider : IStreamProvider
{
    public string Api => "test-api";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
        throw new NotSupportedException();

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("summary result")])));
        return stream;
    }
}

public class WebChatStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSessions()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-webui-{Guid.NewGuid():N}.json");
        var store = new WebChatStore(path);
        var sessions = new[]
        {
            new WebChatSessionDto(
                "session-1",
                "Test Session",
                "openai",
                "gpt-5.4",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow,
                true,
                [new WebChatMessageDto("user", "hello", DateTimeOffset.UtcNow)])
        };

        try
        {
            store.Save(sessions);
            var loaded = store.Load();

            var session = Assert.Single(loaded);
            Assert.Equal("session-1", session.Id);
            Assert.Equal("openai", session.Provider);
            Assert.Equal("gpt-5.4", session.Model);
            Assert.Single(session.Messages);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SendMessageStreamAsync_PersistsAssistantAndBuildsAttachmentPrompt()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-webui-{Guid.NewGuid():N}.json");
        var store = new WebChatStore(path);
        FakeCodingAgentRunner? runner = null;
        var service = new WebChatService(
            store,
            (_, _, _) =>
            {
                runner = new FakeCodingAgentRunner(StreamOk);
                return runner;
            });
        var session = service.CreateSession("Web stream", "openai", "gpt-5.4");
        var attachment = new WebChatAttachmentDto(
            "att-1",
            "document",
            "notes.txt",
            "text/plain",
            5,
            "aGVsbG8=",
            "hello from attachment");
        var events = new List<WebChatStreamEventDto>();

        try
        {
            await foreach (var streamEvent in service.SendMessageStreamAsync(
                               session.Id,
                               "please inspect",
                               [attachment]))
            {
                events.Add(streamEvent);
            }

            Assert.Equal("user", events[0].Type);
            Assert.Contains(events, evt => evt is { Type: "text_delta", Text: "stream ok" });
            var done = Assert.Single(events, evt => evt.Type == "done");
            Assert.True(done.Session?.Persisted);
            Assert.Equal(2, done.Session?.Messages.Count);

            Assert.NotNull(runner);
            var content = Assert.Single(runner!.ContentInputs);
            var prompt = Assert.IsType<TextContent>(content[0]).Text;
            Assert.Contains("please inspect", prompt, StringComparison.Ordinal);
            Assert.Contains("<file name=\"notes.txt\" mimeType=\"text/plain\" size=\"5\">", prompt, StringComparison.Ordinal);
            Assert.Contains("hello from attachment", prompt, StringComparison.Ordinal);

            var stored = Assert.Single(new WebChatStore(path).Load());
            Assert.Equal(session.Id, stored.Id);
            Assert.Equal(2, stored.Messages.Count);
            Assert.Equal("stream ok", stored.Messages[1].Text);
            Assert.Single(stored.Messages[0].Attachments!);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async IAsyncEnumerable<AgentEvent> StreamOk(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var partial = new AssistantMessage([new TextContent("stream ok")]);
        yield return new MessageUpdateEvent(new TextDeltaEvent(0, "stream ok", partial));
        yield return new AgentEndEvent();
    }
}
