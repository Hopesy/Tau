using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.AgentCore.Platform;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

public sealed class AgentHarnessTests
{
    [Fact]
    public async Task PromptAsync_AppendsPromptAndAssistantToSessionAndEmitsEvents()
    {
        var provider = new CapturingProvider("done");
        var harness = CreateHarness(provider, systemPromptFactory: context =>
            $"system with {context.Resources.Skills?.Count ?? 0} skills");
        var events = new List<object>();
        using var subscription = harness.Subscribe(events.Add);

        var assistant = await harness.PromptAsync("hello").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("done", ReadText(assistant.Content));
        Assert.NotNull(provider.LastContext);
        Assert.Equal("system with 1 skills", provider.LastContext.Value.SystemPrompt);
        var request = Assert.Single(provider.LastContext.Value.Messages);
        Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(request).Content));

        var branch = await harness.Session.GetBranchAsync();
        Assert.Equal(["message", "message"], branch.Select(static entry => entry.Type));
        Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(Assert.IsType<MessageSessionEntry>(branch[0]).Message).Content));
        Assert.Equal("done", ReadText(Assert.IsType<AssistantMessage>(Assert.IsType<MessageSessionEntry>(branch[1]).Message).Content));

        Assert.Contains(events, static evt => evt is AgentEndEvent);
        Assert.Contains(events, static evt => evt is AgentHarnessSavePointEvent);
        Assert.Contains(events, static evt => evt is AgentHarnessSettledEvent { NextTurnCount: 0 });
        Assert.Equal(AgentHarnessPhase.Idle, harness.Phase);
    }

    [Fact]
    public async Task SkillAndPromptTemplateInvocationsUseHarnessResources()
    {
        var provider = new CapturingProvider("skill done", "template done");
        var harness = CreateHarness(provider);

        await harness.SkillAsync("review", "focus security").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(provider.LastContext);
        var skillPrompt = ReadText(Assert.IsType<UserMessage>(provider.LastContext.Value.Messages[^1]).Content);
        Assert.Contains("""<skill name="review" location="C:\skills\review\SKILL.md">""", skillPrompt, StringComparison.Ordinal);
        Assert.Contains("Read code carefully.", skillPrompt, StringComparison.Ordinal);
        Assert.Contains("focus security", skillPrompt, StringComparison.Ordinal);

        await harness.PromptFromTemplateAsync("fix", ["README.md", "tests"]).WaitAsync(TimeSpan.FromSeconds(5));
        var templatePrompt = ReadText(Assert.IsType<UserMessage>(provider.LastContext!.Value.Messages[^1]).Content);
        Assert.Equal("Fix README.md then run tests.", templatePrompt);
    }

    [Fact]
    public async Task CompactAsync_AppendsCompactionEntryAndRebuildsContextFromSummary()
    {
        var provider = new CapturingProvider("compact summary");
        var harness = CreateHarness(provider);
        await harness.AppendMessageAsync(new UserMessage("old request"));
        await harness.AppendMessageAsync(new AssistantMessage([new TextContent("old answer")]));
        var firstKept = await harness.Session.AppendMessageAsync(new UserMessage("recent request"));
        var events = new List<object>();
        using var subscription = harness.Subscribe(events.Add);

        var result = await harness.CompactAsync(
            settings: new AgentCompactionSettings(KeepRecentTokens: 1))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("compact summary", result.Summary);
        Assert.Equal(firstKept, result.FirstKeptEntryId);
        Assert.True(result.TokensBefore > 0);
        var compaction = Assert.IsType<CompactionSessionEntry>(
            (await harness.Session.GetEntriesAsync()).Last(static entry => entry.Type == "compaction"));
        Assert.Equal("compact summary", compaction.Summary);
        Assert.Equal(firstKept, compaction.FirstKeptEntryId);
        Assert.Contains(events, static evt => evt is AgentHarnessSessionCompactEvent);

        var context = await harness.Session.BuildContextAsync();
        Assert.Equal(["compactionSummary", "user"], context.Messages.Select(static message => message.Role));
        Assert.Equal("compact summary", Assert.IsType<AgentCompactionSummaryMessage>(context.Messages[0]).Summary);
    }

    [Fact]
    public async Task CompactAsync_UsesSessionBeforeCompactHookResult()
    {
        var provider = new CapturingProvider("unexpected summary");
        var harness = CreateHarness(provider);
        await harness.AppendMessageAsync(new UserMessage("old request"));
        await harness.AppendMessageAsync(new AssistantMessage([new TextContent("old answer")]));
        var firstKept = await harness.Session.AppendMessageAsync(new UserMessage("recent request"));
        var events = new List<object>();
        using var subscription = harness.Subscribe(events.Add);
        using var beforeCompact = harness.OnSessionBeforeCompact((evt, _) =>
        {
            Assert.Equal("prefer hook", evt.CustomInstructions);
            Assert.Equal(firstKept, evt.Preparation.FirstKeptEntryId);
            Assert.Equal(3, evt.BranchEntries.Count);
            return Task.FromResult<AgentHarnessSessionBeforeCompactResult?>(new(
                Compaction: new AgentCompactionResult(
                    "hook compact summary",
                    evt.Preparation.FirstKeptEntryId,
                    evt.Preparation.TokensBefore,
                    new AgentCompactionDetails(["README.md"], ["src/Edit.cs"]))));
        });

        var result = await harness.CompactAsync(
            customInstructions: "prefer hook",
            settings: new AgentCompactionSettings(KeepRecentTokens: 1))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("hook compact summary", result.Summary);
        Assert.Null(provider.LastContext);
        var compaction = Assert.IsType<CompactionSessionEntry>(
            (await harness.Session.GetEntriesAsync()).Last(static entry => entry.Type == "compaction"));
        Assert.True(compaction.FromHook);
        Assert.Equal("hook compact summary", compaction.Summary);
        Assert.Equal(firstKept, compaction.FirstKeptEntryId);
        Assert.Equal(["README.md"], Assert.IsType<AgentCompactionDetails>(compaction.Details).ReadFiles);
        var compactEvent = Assert.IsType<AgentHarnessSessionCompactEvent>(
            Assert.Single(events.OfType<AgentHarnessSessionCompactEvent>()));
        Assert.True(compactEvent.FromHook);
        Assert.Equal(compaction.Id, compactEvent.CompactionEntry.Id);
    }

    [Fact]
    public async Task NavigateTreeAsync_SummarizesOldBranchAndPreservesFromId()
    {
        var provider = new CapturingProvider("branch summary");
        var harness = CreateHarness(provider);
        var root = await harness.Session.AppendMessageAsync(new UserMessage("root prompt"));
        await harness.Session.AppendMessageAsync(new AssistantMessage([new TextContent("main answer")]));
        await harness.Session.AppendMessageAsync(new UserMessage("branch request"));
        await harness.Session.AppendMessageAsync(new AssistantMessage(
        [
            new TextContent("branch work"),
            new ToolCallContent("call-1", "write_file", """{"path":"src/Branch.cs"}""")
        ]));
        var events = new List<object>();
        using var subscription = harness.Subscribe(events.Add);

        var result = await harness.NavigateTreeAsync(
            root,
            summarize: true)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Cancelled);
        Assert.Equal("root prompt", result.EditorText);
        Assert.NotNull(result.SummaryEntry);
        Assert.Null(result.SummaryEntry.ParentId);
        Assert.Equal("root", result.SummaryEntry.FromId);
        Assert.Contains("branch summary", result.SummaryEntry.Summary, StringComparison.Ordinal);
        Assert.Equal("src/Branch.cs", Assert.IsType<AgentBranchSummaryDetails>(result.SummaryEntry.Details).ModifiedFiles.Single());
        Assert.Equal(result.SummaryEntry.Id, await harness.Session.GetLeafIdAsync());
        var context = await harness.Session.BuildContextAsync();
        Assert.Equal("branchSummary", Assert.Single(context.Messages).Role);
        Assert.Contains(events, static evt => evt is AgentHarnessSessionTreeEvent);
    }

    [Fact]
    public async Task NavigateTreeAsync_UsesSessionBeforeTreeHookSummary()
    {
        var provider = new CapturingProvider("unexpected branch summary");
        var harness = CreateHarness(provider);
        var root = await harness.Session.AppendMessageAsync(new UserMessage("root prompt"));
        await harness.Session.AppendMessageAsync(new AssistantMessage([new TextContent("main answer")]));
        await harness.Session.AppendMessageAsync(new UserMessage("branch request"));
        await harness.Session.AppendMessageAsync(new AssistantMessage([new TextContent("branch work")]));
        var events = new List<object>();
        using var subscription = harness.Subscribe(events.Add);
        using var beforeTree = harness.OnSessionBeforeTree((evt, _) =>
        {
            Assert.Equal(root, evt.Preparation.TargetId);
            Assert.True(evt.Preparation.UserWantsSummary);
            Assert.Equal("tree focus", evt.Preparation.CustomInstructions);
            Assert.False(evt.Preparation.ReplaceInstructions);
            Assert.Equal(3, evt.Preparation.EntriesToSummarize.Count);
            return Task.FromResult<AgentHarnessSessionBeforeTreeResult?>(new(
                Summary: new AgentHarnessSessionBeforeTreeSummary(
                    "hook branch summary",
                    new AgentBranchSummaryDetails(["README.md"], ["src/Branch.cs"]))));
        });

        var result = await harness.NavigateTreeAsync(
            root,
            summarize: true,
            customInstructions: "tree focus")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Cancelled);
        Assert.Equal("root prompt", result.EditorText);
        Assert.NotNull(result.SummaryEntry);
        Assert.True(result.SummaryEntry.FromHook);
        Assert.Equal("hook branch summary", result.SummaryEntry.Summary);
        Assert.Equal("src/Branch.cs", Assert.IsType<AgentBranchSummaryDetails>(result.SummaryEntry.Details).ModifiedFiles.Single());
        Assert.Null(provider.LastContext);
        var treeEvent = Assert.IsType<AgentHarnessSessionTreeEvent>(
            Assert.Single(events.OfType<AgentHarnessSessionTreeEvent>()));
        Assert.True(treeEvent.FromHook);
        Assert.Equal(result.SummaryEntry.Id, treeEvent.SummaryEntry?.Id);
    }

    [Fact]
    public async Task SetModelThinkingAndActiveToolsPersistSessionState()
    {
        var provider = new CapturingProvider("done");
        var tool = new DelegateAgentTool(
            "echo",
            "Echo",
            "Echoes.",
            Json("""{"type":"object"}"""),
            (context, _) => Task.FromResult(new ToolResult([new TextContent(context.ToolName)])));
        var harness = CreateHarness(provider, tools: [tool]);
        var nextModel = Model with { Id = "next-model", Provider = "next-provider" };

        await harness.SetModelAsync(nextModel);
        await harness.SetThinkingLevelAsync(ThinkingLevel.High);
        await harness.SetActiveToolsAsync(["echo"]);

        var context = await harness.Session.BuildContextAsync();
        Assert.Equal(new SessionModelReference("next-provider", "next-model"), context.Model);
        Assert.Equal("high", context.ThinkingLevel);
        Assert.Equal(["echo"], context.ActiveToolNames);
    }

    [Fact]
    public async Task PromptAsync_AppliesHarnessHookResultsAcrossContextProviderAndTools()
    {
        var provider = new HookCapturingProvider(
            new AssistantMessage([new ToolCallContent("call-1", "count", """{"count":"1"}""")])
            {
                StopReason = StopReason.ToolUse
            },
            new AssistantMessage([new TextContent("done")]));
        var tool = new DelegateAgentTool(
            "count",
            "Count",
            "Returns the count.",
            Json("""
            {
                "type": "object",
                "properties": {
                    "count": { "type": "string" }
                },
                "required": ["count"]
            }
            """),
            (context, _) => Task.FromResult(new ToolResult([
                new TextContent("tool saw " + context.Arguments.GetProperty("count").GetString())
            ])));
        var harness = CreateHarness(provider, tools: [tool]);
        var afterProviderResponseCount = 0;

        using var beforeAgentStart = harness.OnBeforeAgentStart((evt, _) =>
            Task.FromResult<AgentHarnessBeforeAgentStartResult?>(new(
                Messages: [new UserMessage("hook-added prompt")],
                SystemPrompt: evt.SystemPrompt + " + before")));
        using var contextHook = harness.OnContext((evt, _) =>
            Task.FromResult<AgentHarnessContextResult?>(new(
                [.. evt.Messages, new UserMessage("context-only prompt")])));
        using var beforeProviderRequest = harness.OnBeforeProviderRequest((_, _) =>
            Task.FromResult<AgentHarnessBeforeProviderRequestResult?>(new(
                new AgentHarnessStreamOptionsPatch
                {
                    MaxTokens = 123,
                    Headers = new Dictionary<string, string?> { ["x-hook"] = "enabled" },
                    Metadata = new Dictionary<string, object?> { ["hook"] = "metadata" }
                })));
        using var beforeProviderPayload = harness.OnBeforeProviderPayload((evt, _) =>
        {
            var payload = Assert.IsAssignableFrom<IDictionary<string, object>>(evt.Payload);
            payload["hooked"] = true;
            return Task.FromResult<AgentHarnessBeforeProviderPayloadResult?>(new(payload));
        });
        using var afterProviderResponse = harness.OnAfterProviderResponse((evt, _) =>
        {
            afterProviderResponseCount++;
            Assert.Equal(200, evt.Status);
            Assert.Equal("ok", evt.Headers["x-provider"]);
            return Task.CompletedTask;
        });
        using var toolCall = harness.OnToolCall((evt, _) =>
        {
            Assert.Equal("count", evt.ToolName);
            Assert.Equal("1", evt.Input.GetProperty("count").GetString());
            return Task.FromResult<AgentHarnessToolCallResult?>(new(
                Arguments: Json("""{"count":"2"}""")));
        });
        using var toolResult = harness.OnToolResult((evt, _) =>
        {
            Assert.Equal("tool saw 2", ReadText(evt.Content));
            return Task.FromResult<AgentHarnessToolResultPatch?>(new(
                Content: [new TextContent("hooked tool result")],
                IsError: false));
        });

        var assistant = await harness.PromptAsync("hello hooks").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("done", ReadText(assistant.Content));
        Assert.NotNull(provider.LastContext);
        Assert.Equal("system + before", provider.Calls[0].SystemPrompt);
        Assert.Equal("system", provider.LastContext.Value.SystemPrompt);
        Assert.Equal(
            ["hello hooks", "hook-added prompt", "context-only prompt"],
            provider.LastContext.Value.Messages
                .OfType<UserMessage>()
                .Select(static message => ReadText(message.Content))
                .ToArray());
        Assert.Equal(123, provider.LastOptions?.MaxTokens);
        Assert.Equal("enabled", provider.LastOptions?.Headers?["x-hook"]);
        Assert.Equal("metadata", provider.LastOptions?.Metadata?["hook"]);
        Assert.True(provider.LastPayload?.TryGetValue("hooked", out var hooked) == true && hooked is true);
        Assert.Equal(2, provider.PayloadCallbackCount);
        Assert.Equal(2, afterProviderResponseCount);

        var toolResultMessage = Assert.Single((await harness.Session.BuildContextAsync()).Messages.OfType<ToolResultMessage>());
        Assert.Equal("hooked tool result", ReadText(toolResultMessage.Content));
    }

    [Fact]
    public async Task PromptAsync_ToolResultHookTerminateStopsProviderLoop()
    {
        var provider = new HookCapturingProvider(
            new AssistantMessage([new ToolCallContent("call-1", "count", """{"count":"1"}""")])
            {
                StopReason = StopReason.ToolUse
            },
            new AssistantMessage([new TextContent("unexpected follow-up")]));
        var tool = new DelegateAgentTool(
            "count",
            "Count",
            "Returns the count.",
            Json("""{"type":"object"}"""),
            (context, _) => Task.FromResult(new ToolResult([
                new TextContent("tool saw " + context.Arguments.GetProperty("count").GetString())
            ])));
        var harness = CreateHarness(provider, tools: [tool]);
        using var toolResult = harness.OnToolResult((_, _) =>
            Task.FromResult<AgentHarnessToolResultPatch?>(new(
                Content: [new TextContent("terminal hook result")],
                Terminate: true)));

        var assistant = await harness.PromptAsync("stop after tool").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(assistant.Content.OfType<ToolCallContent>());
        Assert.Equal(1, provider.StreamSimpleCallCount);
        var context = await harness.Session.BuildContextAsync();
        Assert.Equal(["user", "assistant", "toolResult"], context.Messages.Select(static message => message.Role));
        var toolResultMessage = Assert.Single(context.Messages.OfType<ToolResultMessage>());
        Assert.Equal("terminal hook result", ReadText(toolResultMessage.Content));
        Assert.DoesNotContain(
            context.Messages.OfType<AssistantMessage>(),
            message => ReadText(message.Content).Equals("unexpected follow-up", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromptAsync_PrepareNextTurnRefreshesSessionBackedState()
    {
        var provider = new HookCapturingProvider(
            new AssistantMessage([new ToolCallContent("call-1", "switch", "{}")])
            {
                StopReason = StopReason.ToolUse
            },
            new AssistantMessage([new TextContent("done")]));
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        var initialModel = Model with
        {
            Id = "initial-model",
            Name = "Initial Model",
            Api = provider.Api,
            Reasoning = true
        };
        var nextModel = Model with
        {
            Id = "next-model",
            Name = "Next Model",
            Api = provider.Api,
            Reasoning = true
        };
        AgentHarness<SessionMetadata>? harness = null;
        var switchTool = new DelegateAgentTool(
            "switch",
            "Switch",
            "Switches turn state.",
            Json("""{"type":"object"}"""),
            async (_, cancellationToken) =>
            {
                await harness!.SetModelAsync(nextModel, cancellationToken).ConfigureAwait(false);
                await harness.SetThinkingLevelAsync(ThinkingLevel.High, cancellationToken).ConfigureAwait(false);
                await harness.SetActiveToolsAsync(["next"], cancellationToken).ConfigureAwait(false);
                return new ToolResult([new TextContent("switched")]);
            });
        var nextTool = new DelegateAgentTool(
            "next",
            "Next",
            "Next active tool.",
            Json("""{"type":"object"}"""),
            (_, _) => Task.FromResult(new ToolResult([new TextContent("next")])));
        harness = new AgentHarness<SessionMetadata>(new AgentHarnessOptions<SessionMetadata>
        {
            Session = new AgentHarnessSession<SessionMetadata>(new InMemorySessionStorage<SessionMetadata>()),
            ProviderRegistry = registry,
            Model = initialModel,
            Tools = [switchTool, nextTool],
            ActiveToolNames = ["switch"],
            SystemPrompt = "system",
            ThinkingLevel = ThinkingLevel.Low
        });

        var assistant = await harness.PromptAsync("refresh next turn").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("done", ReadText(assistant.Content));
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal("initial-model", provider.Calls[0].ModelId);
        Assert.Equal(["switch"], provider.Calls[0].ToolNames);
        Assert.Equal(ThinkingLevel.Low, provider.Calls[0].Reasoning);
        Assert.Equal("next-model", provider.Calls[1].ModelId);
        Assert.Equal(["next"], provider.Calls[1].ToolNames);
        Assert.Equal(ThinkingLevel.High, provider.Calls[1].Reasoning);
        var context = await harness.Session.BuildContextAsync();
        Assert.Equal("next-model", context.Model?.ModelId);
        Assert.Equal("high", context.ThinkingLevel);
        Assert.Equal(["next"], context.ActiveToolNames);
    }

    [Fact]
    public async Task PromptAsync_WhenToolCallHookBlocksWithoutReasonUsesDefaultBlockedMessage()
    {
        var provider = new HookCapturingProvider(
            new AssistantMessage([new ToolCallContent("call-1", "count", """{"count":"1"}""")])
            {
                StopReason = StopReason.ToolUse
            },
            new AssistantMessage([new TextContent("done")]));
        var toolRan = false;
        var tool = new DelegateAgentTool(
            "count",
            "Count",
            "Returns the count.",
            Json("""{"type":"object"}"""),
            (_, _) =>
            {
                toolRan = true;
                return Task.FromResult(new ToolResult([new TextContent("unexpected")]));
            });
        var harness = CreateHarness(provider, tools: [tool]);
        using var toolCall = harness.OnToolCall((_, _) =>
            Task.FromResult<AgentHarnessToolCallResult?>(new(Blocked: true)));

        var assistant = await harness.PromptAsync("block tool").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("done", ReadText(assistant.Content));
        Assert.False(toolRan);
        var toolResultMessage = Assert.Single((await harness.Session.BuildContextAsync()).Messages.OfType<ToolResultMessage>());
        Assert.True(toolResultMessage.IsError);
        Assert.Equal("Tool call blocked.", ReadText(toolResultMessage.Content));
    }

    [Fact]
    public async Task PromptAsync_AppliesProviderHooksWhenProviderSpecificOptionsUseStreamPath()
    {
        var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-agent-harness-stream-hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openai": {
                      "models": [
                        {
                          "id": "gpt-5.4",
                          "options": {
                            "toolChoice": {
                              "type": "function",
                              "function": {
                                "name": "read_file"
                              }
                            }
                          }
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new StreamPathHookCapturingProvider();
            var harness = CreateHarness(
                provider,
                model: Model with
                {
                    Id = "gpt-5.4",
                    Name = "GPT-5.4",
                    Api = "openai-chat-completions",
                    Provider = "openai"
                });
            var responseCount = 0;

            using var beforeProviderRequest = harness.OnBeforeProviderRequest((_, _) =>
                Task.FromResult<AgentHarnessBeforeProviderRequestResult?>(new(
                    new AgentHarnessStreamOptionsPatch
                    {
                        MaxTokens = 321,
                        Headers = new Dictionary<string, string?> { ["x-hook"] = "stream" },
                        Metadata = new Dictionary<string, object?> { ["hook"] = "stream-path" }
                    })));
            using var beforeProviderPayload = harness.OnBeforeProviderPayload((evt, _) =>
            {
                var payload = Assert.IsAssignableFrom<IDictionary<string, object>>(evt.Payload);
                payload["hooked"] = true;
                return Task.FromResult<AgentHarnessBeforeProviderPayloadResult?>(new(payload));
            });
            using var afterProviderResponse = harness.OnAfterProviderResponse((evt, _) =>
            {
                responseCount++;
                Assert.Equal(201, evt.Status);
                Assert.Equal("stream", evt.Headers["x-provider"]);
                return Task.CompletedTask;
            });

            var assistant = await harness.PromptAsync("hello stream path").WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("stream path done", ReadText(assistant.Content));
            Assert.Equal(1, provider.StreamCallCount);
            Assert.Equal(0, provider.StreamSimpleCallCount);
            var options = Assert.IsType<OpenAiOptions>(provider.LastOptions);
            Assert.Equal(321, options.MaxTokens);
            Assert.True(options.ToolChoice?.IsFunction);
            Assert.Equal("read_file", options.ToolChoice?.FunctionName);
            Assert.Equal("stream", options.Headers?["x-hook"]);
            Assert.Equal("stream-path", options.Metadata?["hook"]);
            Assert.True(provider.LastPayload?.TryGetValue("hooked", out var hooked) == true && hooked is true);
            Assert.Equal(1, provider.PayloadCallbackCount);
            Assert.Equal(1, responseCount);
        }
        finally
        {
            scope.Dispose();
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static AgentHarness<SessionMetadata> CreateHarness(
        IStreamProvider provider,
        Func<AgentHarnessSystemPromptContext<SessionMetadata>, string>? systemPromptFactory = null,
        IReadOnlyList<IAgentTool>? tools = null,
        Model? model = null)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        return new AgentHarness<SessionMetadata>(new AgentHarnessOptions<SessionMetadata>
        {
            Session = new AgentHarnessSession<SessionMetadata>(new InMemorySessionStorage<SessionMetadata>()),
            ProviderRegistry = registry,
            Model = model ?? Model,
            Tools = tools ?? [],
            Resources = new AgentHarnessResources(
                Skills:
                [
                    new AgentHarnessSkill(
                        "review",
                        "Review code.",
                        "Read code carefully.",
                        @"C:\skills\review\SKILL.md")
                ],
                PromptTemplates:
                [
                    new AgentPromptTemplate(
                        "fix",
                        "Fix file",
                        "Fix $1 then run $2.")
                ]),
            SystemPrompt = "system",
            SystemPromptFactory = systemPromptFactory is null
                ? null
                : (context, _) => Task.FromResult(systemPromptFactory(context))
        });
    }

    private static Model Model { get; } = new()
    {
        Id = "test-model",
        Name = "Test model",
        Api = "capture",
        Provider = "test",
        ContextWindow = 128_000,
        MaxOutputTokens = 4_096
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ReadText(IReadOnlyList<ContentBlock> content) =>
        string.Join("", content.OfType<TextContent>().Select(static text => text.Text));

    private sealed class CapturingProvider(params string[] responses) : IStreamProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public string Api => "capture";
        public LlmContext? LastContext { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            throw new NotSupportedException();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        {
            LastContext = context;
            var stream = new AssistantMessageStream();
            var text = _responses.Count == 0 ? "done" : _responses.Dequeue();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent(text)])));
            return stream;
        }
    }

    private sealed class HookCapturingProvider(params AssistantMessage[] responses) : IStreamProvider
    {
        private readonly Queue<AssistantMessage> _responses = new(responses);

        public string Api => "capture";
        public LlmContext? LastContext { get; private set; }
        public SimpleStreamOptions? LastOptions { get; private set; }
        public IDictionary<string, object>? LastPayload { get; private set; }
        public int PayloadCallbackCount { get; private set; }
        public int StreamSimpleCallCount { get; private set; }
        public List<HookProviderCall> Calls { get; } = [];

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            throw new NotSupportedException();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        {
            StreamSimpleCallCount++;
            LastContext = context;
            LastOptions = options;
            Calls.Add(new HookProviderCall(
                model.Id,
                context.SystemPrompt,
                (context.Tools ?? []).Select(static tool => tool.Name).ToArray(),
                options.Reasoning));
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["request"] = PayloadCallbackCount + 1
            };
            if (options.OnPayload is not null)
            {
                PayloadCallbackCount++;
                var replacement = options.OnPayload(payload, model).GetAwaiter().GetResult();
                LastPayload = Assert.IsAssignableFrom<IDictionary<string, object>>(replacement ?? payload);
            }

            options.OnResponse?.Invoke(
                new ProviderResponse(
                    200,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["x-provider"] = "ok"
                    }),
                model).GetAwaiter().GetResult();

            var stream = new AssistantMessageStream();
            var message = _responses.Count == 0
                ? new AssistantMessage([new TextContent("done")])
                : _responses.Dequeue();
            stream.Push(new DoneEvent(message));
            return stream;
        }
    }

    private sealed record HookProviderCall(
        string ModelId,
        string? SystemPrompt,
        IReadOnlyList<string> ToolNames,
        ThinkingLevel? Reasoning);

    private sealed class StreamPathHookCapturingProvider : IStreamProvider
    {
        public string Api => "openai-chat-completions";
        public StreamOptions? LastOptions { get; private set; }
        public IDictionary<string, object>? LastPayload { get; private set; }
        public int PayloadCallbackCount { get; private set; }
        public int StreamCallCount { get; private set; }
        public int StreamSimpleCallCount { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            StreamCallCount++;
            LastOptions = options;
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["request"] = StreamCallCount
            };
            if (options.OnPayload is not null)
            {
                PayloadCallbackCount++;
                var replacement = options.OnPayload(payload, model).GetAwaiter().GetResult();
                LastPayload = Assert.IsAssignableFrom<IDictionary<string, object>>(replacement ?? payload);
            }

            options.OnResponse?.Invoke(
                new ProviderResponse(
                    201,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["x-provider"] = "stream"
                    }),
                model).GetAwaiter().GetResult();

            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent("stream path done")])));
            return stream;
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        {
            StreamSimpleCallCount++;
            throw new NotSupportedException("Provider-specific harness options must dispatch through Stream.");
        }
    }
}
