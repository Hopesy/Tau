using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;
using Tau.AgentCore.Runtime;
using Tau.CodingAgent.Runtime;

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
    public void CreateDefaultTools_WithExtensionTools_OverridesBuiltInToolByName()
    {
        var readOverride = new StaticAgentTool("read_file", "Extension Read");
        var customTool = new StaticAgentTool("extension_tool", "Extension Tool");

        var tools = RuntimeCodingAgentRunner.CreateDefaultTools(extensionTools: [readOverride, customTool]);

        Assert.Same(readOverride, Assert.Single(tools, static tool => tool.Name == "read_file"));
        Assert.Same(customTool, Assert.Single(tools, static tool => tool.Name == "extension_tool"));
        Assert.Contains(tools, static tool => tool.Name == "shell");
    }

    [Fact]
    public void CreateDefaultTools_WithNullSelection_ReturnsFullBuiltInSet()
    {
        var tools = RuntimeCodingAgentRunner.CreateDefaultTools(selectedBuiltInToolNames: null);

        Assert.Equal(7, tools.Length);
        Assert.Contains(tools, static tool => tool.Name == "read_file");
        Assert.Contains(tools, static tool => tool.Name == "shell");
        Assert.Contains(tools, static tool => tool.Name == "glob");
    }

    [Fact]
    public void CreateDefaultTools_WithExplicitSelection_EnablesOnlyNamedBuiltIns()
    {
        var tools = RuntimeCodingAgentRunner.CreateDefaultTools(
            selectedBuiltInToolNames: ["read_file", "grep"]);

        Assert.Equal(2, tools.Length);
        Assert.Contains(tools, static tool => tool.Name == "read_file");
        Assert.Contains(tools, static tool => tool.Name == "grep");
        Assert.DoesNotContain(tools, static tool => tool.Name == "shell");
    }

    [Fact]
    public void CreateDefaultTools_WithEmptySelection_DropsBuiltInsButKeepsExtensions()
    {
        var customTool = new StaticAgentTool("extension_tool", "Extension Tool");

        var tools = RuntimeCodingAgentRunner.CreateDefaultTools(
            extensionTools: [customTool],
            selectedBuiltInToolNames: []);

        Assert.Same(customTool, Assert.Single(tools));
    }

    [Fact]
    public void CliToolNameToTauToolName_MapsUpstreamNamesToTauTools()
    {
        var map = CodingAgentCliArguments.CliToolNameToTauToolName;

        Assert.Equal("read_file", map["read"]);
        Assert.Equal("shell", map["bash"]);
        Assert.Equal("edit_file", map["edit"]);
        Assert.Equal("write_file", map["write"]);
        Assert.Equal("glob", map["find"]);
        Assert.Equal("grep", map["grep"]);
        Assert.Equal("ls", map["ls"]);
    }

    [Fact]
    public async Task Create_WithApiKey_PassesExplicitKeyToProvider()
    {
        string? capturedApiKey = null;
        var registry = new ProviderRegistry();
        registry.Register("options-capture-test", () => new OptionsCapturingProvider(key => capturedApiKey = key), sourceId: "test");
        var catalog = new Tau.Ai.Registry.ModelCatalog();
        catalog.RegisterModel(new Model
        {
            Provider = "test-provider",
            Id = "test-model",
            Name = "Test Model",
            Api = "options-capture-test"
        });

        var runner = RuntimeCodingAgentRunner.Create(
            "test-provider",
            "test-model",
            toolsOverride: [],
            providerRegistryOverride: registry,
            modelCatalogOverride: catalog,
            apiKey: "sk-cli-supplied-key");

        await foreach (var _ in runner.RunAsync("hello")) { }

        Assert.Equal("sk-cli-supplied-key", capturedApiKey);
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
                    ])
                {
                    Usage = new Usage(
                        InputTokens: 100,
                        OutputTokens: 20,
                        CacheReadTokens: 3,
                        CacheWriteTokens: 4,
                        ServiceTier: "flex",
                        Cost: new UsageCost(0.01m, 0.02m, 0.003m, 0.004m))
                },
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
        Assert.Equal(100, stats.Tokens.Input);
        Assert.Equal(20, stats.Tokens.Output);
        Assert.Equal(3, stats.Tokens.CacheRead);
        Assert.Equal(4, stats.Tokens.CacheWrite);
        Assert.Equal(127, stats.Tokens.Total);
        Assert.Equal(0.037m, stats.Cost);
        Assert.Equal(1, stats.CostRecords);
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
    public async Task SummarizeBranchAsync_WithReplaceInstructions_UsesCustomPromptInsteadOfDefaultTemplate()
    {
        LlmContext? capturedContext = null;
        var runner = RuntimeCodingAgentRunner.Create(
            "test-provider",
            "test-model",
            toolsOverride: [],
            providerRegistryOverride: CreatePromptCapturingRegistry(context => capturedContext = context),
            modelCatalogOverride: CreatePromptCapturingModelCatalog());

        var result = await runner.SummarizeBranchAsync(
            [new UserMessage("investigate branch"), new AssistantMessage([new TextContent("found issue")])],
            "Summarize only blockers and next commands.",
            replaceInstructions: true);

        Assert.Contains("summary result", result.Summary, StringComparison.Ordinal);
        Assert.NotNull(capturedContext);
        var context = capturedContext!.Value;
        var promptMessage = Assert.IsType<UserMessage>(Assert.Single(context.Messages));
        var prompt = Assert.IsType<TextContent>(Assert.Single(promptMessage.Content)).Text;
        Assert.Contains("Summarize only blockers and next commands.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(CodingAgentCompactionMessages.BranchSummaryPrompt, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Additional focus:", prompt, StringComparison.Ordinal);
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
        }, new TauRuntimeLogContext(SessionId: "session-1", MessageId: "message-1"));

        var events = new List<AgentEvent>();
        await foreach (var evt in runner.RunAsync("hello"))
        {
            events.Add(evt);
        }

        var startEvent = Assert.Single(sink.Events, e => e.Category == "agent" && e.Event == "run.start");
        Assert.Equal("agent", startEvent.Category);
        Assert.Equal("test-provider", startEvent.Fields["provider"]);
        Assert.Equal("test-model", startEvent.Fields["model"]);
        Assert.Equal("5", startEvent.Fields["inputBytes"]);
        Assert.False(string.IsNullOrWhiteSpace(startEvent.Fields["correlationId"]));
        Assert.Equal("session-1", startEvent.Fields["sessionId"]);
        Assert.Equal("message-1", startEvent.Fields["messageId"]);

        var endEvent = Assert.Single(sink.Events, e => e.Category == "agent" && e.Event == "run.end");
        Assert.Equal("agent", endEvent.Category);
        Assert.True(int.Parse(endEvent.Fields["elapsedMs"]!, System.Globalization.CultureInfo.InvariantCulture) >= 0);
        Assert.Equal(startEvent.Fields["correlationId"], endEvent.Fields["correlationId"]);
        Assert.Equal("session-1", endEvent.Fields["sessionId"]);
        Assert.Equal("message-1", endEvent.Fields["messageId"]);

        Assert.DoesNotContain(sink.Events, e => e.Category == "agent" && (e.Event == "run.error" || e.Event == "run.cancel"));
    }

    [Fact]
    public async Task RunAsync_PerRunLogContextOverridesConfiguredContext()
    {
        var sink = new RecordingLogSink();
        var runner = CreateInstrumentedRunner(sink, () =>
        {
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent("ok")])));
            return stream;
        }, new TauRuntimeLogContext(
            CorrelationId: "configured-correlation",
            SessionId: "configured-session",
            MessageId: "configured-message"));
        var runContext = new TauRuntimeLogContext(
            CorrelationId: "run-correlation",
            SessionId: "run-session",
            MessageId: "run-message");

        await foreach (var _ in runner.RunAsync("hello", runContext)) { }

        var startEvent = Assert.Single(sink.Events, e => e.Category == "agent" && e.Event == "run.start");
        Assert.Equal("run-correlation", startEvent.Fields["correlationId"]);
        Assert.Equal("run-session", startEvent.Fields["sessionId"]);
        Assert.Equal("run-message", startEvent.Fields["messageId"]);

        var endEvent = Assert.Single(sink.Events, e => e.Category == "agent" && e.Event == "run.end");
        Assert.Equal("run-correlation", endEvent.Fields["correlationId"]);
        Assert.Equal("run-session", endEvent.Fields["sessionId"]);
        Assert.Equal("run-message", endEvent.Fields["messageId"]);
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

        Assert.Contains(sink.Events, e => e.Category == "agent" && e.Event == "run.start");
        var errorEvent = Assert.Single(sink.Events, e => e.Category == "agent" && e.Event == "run.error");
        Assert.Equal("InvalidOperationException", errorEvent.Fields["error"]);
        Assert.Equal("boom", errorEvent.Fields["message"]);
        var startEvent = Assert.Single(sink.Events, e => e.Category == "agent" && e.Event == "run.start");
        Assert.False(string.IsNullOrWhiteSpace(startEvent.Fields["correlationId"]));
        Assert.Equal(startEvent.Fields["correlationId"], errorEvent.Fields["correlationId"]);
        Assert.DoesNotContain(sink.Events, e => e.Category == "agent" && e.Event == "run.end");
        var providerEnd = Assert.Single(sink.Events, e => e.Category == "provider" && e.Event == "run.end");
        Assert.Equal("false", providerEnd.Fields["success"]);
        Assert.Equal("exception", providerEnd.Fields["failureKind"]);
        Assert.Equal("InvalidOperationException", providerEnd.Fields["exceptionType"]);
    }

    [Fact]
    public async Task RunAsync_LogsJavascriptLifecycleHandlerErrorWithoutFailingRun()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-runtime-lifecycle-error-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "lifecycle");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.on("message_start", () => {
                throw new Error("lifecycle boom");
              });
            }
            """);

        try
        {
            var sink = new RecordingLogSink();
            var extensionStore = new CodingAgentExtensionCommandStore(
                cwd: directory,
                userExtensionsDirectory: Path.Combine(directory, "missing-user-extensions"),
                javaScriptRuntime: new CodingAgentJavaScriptExtensionRuntime(directory, nodeExecutable: "node"));
            var runner = RuntimeCodingAgentRunner.Create(
                "test-provider",
                "test-model",
                toolsOverride: [],
                logSink: sink,
                providerRegistryOverride: CreatePromptCapturingRegistry(_ => { }),
                modelCatalogOverride: CreatePromptCapturingModelCatalog(),
                extensionLifecycleEventSink: extensionStore.LoadLifecycleEventSink());

            var events = new List<AgentEvent>();
            await foreach (var evt in runner.RunAsync("hello"))
            {
                events.Add(evt);
            }

            Assert.NotEmpty(events);
            Assert.Contains(sink.Events, e => e.Category == "agent" && e.Event == "run.end");
            Assert.DoesNotContain(sink.Events, e => e.Category == "agent" && e.Event == "run.error");
            var errorEvent = Assert.Single(sink.Events, e => e.Category == "extension" && e.Event == "event.error");
            Assert.Equal("message_start", errorEvent.Fields["eventType"]);
            Assert.Equal("project", errorEvent.Fields["scope"]);
            Assert.Equal("javascript", errorEvent.Fields["runtime"]);
            Assert.Contains("lifecycle boom", errorEvent.Fields["error"], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    [Fact]
    public async Task Create_AppendsAppendSystemPromptToGeneratedPrompt()
    {
        string? capturedPrompt = null;
        var runner = RuntimeCodingAgentRunner.Create(
            "test-provider",
            "test-model",
            toolsOverride: [],
            providerRegistryOverride: CreatePromptCapturingRegistry(context => capturedPrompt = context.SystemPrompt),
            modelCatalogOverride: CreatePromptCapturingModelCatalog(),
            appendSystemPrompt: "EXTRA SYSTEM RULE");

        await foreach (var _ in runner.RunAsync("hello")) { }

        Assert.NotNull(capturedPrompt);
        Assert.Contains("You are Tau", capturedPrompt, StringComparison.Ordinal);
        Assert.EndsWith("EXTRA SYSTEM RULE", capturedPrompt!.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_AppendsAppendSystemPromptToCustomPrompt()
    {
        string? capturedPrompt = null;
        var runner = RuntimeCodingAgentRunner.Create(
            "test-provider",
            "test-model",
            toolsOverride: [],
            systemPromptOverride: "custom system prompt",
            providerRegistryOverride: CreatePromptCapturingRegistry(context => capturedPrompt = context.SystemPrompt),
            modelCatalogOverride: CreatePromptCapturingModelCatalog(),
            appendSystemPrompt: "EXTRA RULE");

        await foreach (var _ in runner.RunAsync("hello")) { }

        Assert.Equal("custom system prompt\n\nEXTRA RULE", capturedPrompt);
    }

    [Fact]
    public async Task RefreshSystemPromptResources_PreservesAppendSystemPrompt()
    {
        string? capturedPrompt = null;
        var runner = RuntimeCodingAgentRunner.Create(
            "test-provider",
            "test-model",
            toolsOverride: [],
            providerRegistryOverride: CreatePromptCapturingRegistry(context => capturedPrompt = context.SystemPrompt),
            modelCatalogOverride: CreatePromptCapturingModelCatalog(),
            appendSystemPrompt: "PERSISTENT APPEND");

        var refreshed = runner.RefreshSystemPromptResources(
            [],
            [new CodingAgentContextFile(Path.Combine(Path.GetTempPath(), "CLAUDE.md"), "new context", "project")]);

        Assert.True(refreshed);
        await foreach (var _ in runner.RunAsync("hello")) { }

        Assert.NotNull(capturedPrompt);
        Assert.Contains("new context", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("PERSISTENT APPEND", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EmitsJavascriptLifecycleEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-runtime-lifecycle-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "lifecycle");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            import fs from "node:fs";

            export default function(pi) {
              pi.on("agent_start", (event) => fs.appendFileSync("events.log", `${event.type}\n`));
              pi.on("message_start", (event) => {
                const first = event.message?.content?.[0]?.text ?? "";
                fs.appendFileSync("events.log", `${event.type}:${event.message.role}:${first}\n`);
              });
              pi.on("message_end", (event) => {
                const first = event.message?.content?.[0]?.text ?? "";
                fs.appendFileSync("events.log", `${event.type}:${event.message.role}:${first}\n`);
              });
              pi.on("agent_end", (event) => fs.appendFileSync("events.log", `${event.type}:${event.messages.length}\n`));
            }
            """);

        try
        {
            var extensionStore = new CodingAgentExtensionCommandStore(
                cwd: directory,
                userExtensionsDirectory: Path.Combine(directory, "missing-user-extensions"),
                javaScriptRuntime: new CodingAgentJavaScriptExtensionRuntime(directory, nodeExecutable: "node"));
            var runner = RuntimeCodingAgentRunner.Create(
                "test-provider",
                "test-model",
                toolsOverride: [],
                providerRegistryOverride: CreatePromptCapturingRegistry(_ => { }),
                modelCatalogOverride: CreatePromptCapturingModelCatalog(),
                extensionLifecycleEventSink: extensionStore.LoadLifecycleEventSink());

            await foreach (var _ in runner.RunAsync("hello")) { }

            var log = File.ReadAllText(Path.Combine(directory, "events.log")).ReplaceLineEndings("\n");
            Assert.Contains("agent_start\n", log, StringComparison.Ordinal);
            Assert.Contains("message_start:assistant:summary result\n", log, StringComparison.Ordinal);
            Assert.Contains("message_end:assistant:summary result\n", log, StringComparison.Ordinal);
            Assert.Contains("agent_end:2\n", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RuntimeCodingAgentRunner CreateInstrumentedRunner(
        ITauLogSink sink,
        Func<AssistantMessageStream> streamFactory,
        TauRuntimeLogContext? logContext = null)
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
                LogContext = logContext,
                SystemPrompt = "test",
                StreamOptions = new SimpleStreamOptions { MaxTokens = 256 }
            },
            new Tau.Ai.Registry.ModelCatalog(),
            logSink: sink,
            logContext: logContext);
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

    private static void WriteJavaScriptExtension(string extensionDirectory, string source)
    {
        File.WriteAllText(
            Path.Combine(extensionDirectory, "package.json"),
            """
            {
              "type": "module",
              "pi": {
                "extensions": ["index.js"]
              }
            }
            """);
        File.WriteAllText(Path.Combine(extensionDirectory, "index.js"), source);
    }
}

file sealed class StaticAgentTool : IAgentTool
{
    public StaticAgentTool(string name, string label)
    {
        Name = name;
        Label = label;
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        ParameterSchema = schema.RootElement.Clone();
    }

    public string Name { get; }
    public string Label { get; }
    public string Description => Label;
    public JsonElement ParameterSchema { get; }

    public Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct = default,
        Func<ToolUpdate, Task>? onUpdate = null) =>
        Task.FromResult(new ToolResult([new TextContent(Label)]));
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
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("summary result")])));
        return stream;
    }
}

file sealed class OptionsCapturingProvider : IStreamProvider
{
    private readonly Action<string?> _captureApiKey;
    public OptionsCapturingProvider(Action<string?> captureApiKey) => _captureApiKey = captureApiKey;
    public string Api => "options-capture-test";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        _captureApiKey(options.ApiKey);
        return CreateStream();
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        _captureApiKey(options.ApiKey);
        return CreateStream();
    }

    private static AssistantMessageStream CreateStream()
    {
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
