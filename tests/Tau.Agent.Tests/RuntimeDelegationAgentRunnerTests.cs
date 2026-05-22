using System.Text.Json;
using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.CodingAgent.Runtime;
using Tau.Mom;

namespace Tau.Agent.Tests;

public class RuntimeDelegationAgentRunnerTests
{
    [Fact]
    public async Task ExecuteAsync_AggregatesUsage_StopReason_ToolEventsWithDuration()
    {
        var model = new Model
        {
            Provider = "openai",
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Cost = new ModelCost(2m, 8m)
        };

        var fake = new ScriptedRunner(model);
        var delegationRunner = new RuntimeDelegationAgentRunner((_, _) => fake);

        var partial = new AssistantMessage();
        fake.Events =
        [
            new MessageUpdateEvent(new TextDeltaEvent(0, "hello ", partial)),
            new ToolExecutionStartEvent("tool-1", "shell"),
            new ToolExecutionEndEvent("tool-1", new ToolResult([new TextContent("ok")], IsError: false)),
            new MessageUpdateEvent(new TextDeltaEvent(0, "world", partial)),
            new MessageEndEvent(new AssistantMessage([new TextContent("hello world")])
            {
                StopReason = StopReason.EndTurn,
                Usage = new Usage(InputTokens: 1_000_000, OutputTokens: 1_000_000, CacheReadTokens: 0, CacheWriteTokens: 0)
            }),
            new AgentEndEvent()
        ];

        var execution = await delegationRunner.ExecuteAsync(new DelegationRequest(
            "say hi",
            Provider: "openai",
            Model: "gpt-5.4",
            WorkingDirectory: Path.GetTempPath()));

        Assert.Equal("hello world", execution.Response);
        Assert.Null(execution.Error);
        Assert.Equal("end_turn", execution.StopReason);
        Assert.NotNull(execution.Usage);
        Assert.Equal(1_000_000, execution.Usage!.InputTokens);
        Assert.Equal(1_000_000, execution.Usage.OutputTokens);
        Assert.Equal(10m, execution.Usage.TotalCost);

        Assert.Collection(execution.ToolEvents,
            evt =>
            {
                Assert.Equal("start", evt.Phase);
                Assert.Equal("shell", evt.ToolName);
                Assert.Equal("tool-1", evt.ToolCallId);
                Assert.Null(evt.IsError);
                Assert.Null(evt.DurationMs);
            },
            evt =>
            {
                Assert.Equal("end", evt.Phase);
                Assert.Equal("shell", evt.ToolName);
                Assert.Equal("tool-1", evt.ToolCallId);
                Assert.False(evt.IsError);
                Assert.NotNull(evt.DurationMs);
                Assert.True(evt.DurationMs >= 0);
            });
    }

    [Fact]
    public async Task ExecuteAsync_IncludesStructuredContextInRunnerInput_AndSetsSessionName()
    {
        var model = new Model { Provider = "openai", Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-responses" };
        var fake = new ScriptedRunner(model)
        {
            Events = [new AgentEndEvent()]
        };

        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(root, "channel-a");
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(Path.Combine(root, "MEMORY.md"), "global memory: use project conventions");
        await File.WriteAllTextAsync(Path.Combine(root, "SYSTEM.md"), "installed package: ripgrep");
        await File.WriteAllTextAsync(Path.Combine(workingDirectory, "MEMORY.md"), "channel memory: deploy window is Friday");
        var workspaceSkillDirectory = Path.Combine(root, "skills", "deploy-helper");
        Directory.CreateDirectory(workspaceSkillDirectory);
        await File.WriteAllTextAsync(Path.Combine(workspaceSkillDirectory, "SKILL.md"), """
        ---
        name: deploy-helper
        description: Helps inspect deployment state.
        ---

        # Deploy Helper
        """);
        var channelSkillDirectory = Path.Combine(workingDirectory, "skills", "incident-helper");
        Directory.CreateDirectory(channelSkillDirectory);
        await File.WriteAllTextAsync(Path.Combine(channelSkillDirectory, "SKILL.md"), """
        ---
        name: incident-helper
        description: Helps triage channel incidents.
        ---

        # Incident Helper
        """);
        var disabledSkillDirectory = Path.Combine(workingDirectory, "skills", "hidden-helper");
        Directory.CreateDirectory(disabledSkillDirectory);
        await File.WriteAllTextAsync(Path.Combine(disabledSkillDirectory, "SKILL.md"), """
        ---
        name: hidden-helper
        description: Hidden helper.
        disable-model-invocation: true
        ---

        # Hidden Helper
        """);
        await File.WriteAllTextAsync(Path.Combine(workingDirectory, "log.jsonl"), """
        {"date":"2026-05-05T01:00:00Z","ts":"1","user":"U1","userName":"Alice","text":"first human message","isBot":false}
        {"date":"2026-05-05T01:01:00Z","ts":"2","userName":"Tau Bot","text":"bot reply","isBot":true}
        {"date":"2026-05-05T01:02:00Z","ts":"current-3","userName":"Alice","text":"current prompt duplicate","isBot":false}
        {"date":"2026-05-05T01:03:00Z","ts":"4","user":"U2","text":"second human message\n\n<slack_attachments>\n{\"title\":\"large payload\"}","isBot":false}
        []
        {not-valid-json}
        """);
        var attachmentPath = Path.Combine(workingDirectory, "tau-mom-attachment.txt");
        await File.WriteAllTextAsync(attachmentPath, "attachment content");

        var delegationRunner = new RuntimeDelegationAgentRunner((_, _) => fake);

        string? debugJson = null;
        var scratchExists = false;
        var workspaceSkillsExists = false;
        var channelSkillsExists = false;
        try
        {
            await delegationRunner.ExecuteAsync(new DelegationRequest(
                "inspect the attached notes",
                Provider: "openai",
                Model: "gpt-5.4",
                WorkingDirectory: workingDirectory,
                Title: "triage build failure",
                Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["channel"] = "ops",
                    ["requestId"] = "req-42",
                    ["ts"] = "current-3"
                },
                Attachments: [attachmentPath]));
            debugJson = await File.ReadAllTextAsync(Path.Combine(workingDirectory, "last_prompt.jsonl"));
            scratchExists = Directory.Exists(Path.Combine(workingDirectory, "scratch"));
            workspaceSkillsExists = Directory.Exists(Path.Combine(root, "skills"));
            channelSkillsExists = Directory.Exists(Path.Combine(workingDirectory, "skills"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.True(scratchExists);
        Assert.True(workspaceSkillsExists);
        Assert.True(channelSkillsExists);
        Assert.Equal("triage build failure", fake.SessionName);
        Assert.NotNull(fake.LastInput);
        Assert.Contains("<mom_runtime_context>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("role: Tau.Mom local delegation worker", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("workspace_layout:", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(root, "SYSTEM.md"), fake.LastInput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(workingDirectory, "scratch"), fake.LastInput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(root, "skills"), fake.LastInput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(workingDirectory, "skills"), fake.LastInput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(workingDirectory, "attachments"), fake.LastInput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(workingDirectory, "attachments", "attachments.jsonl"), fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("skill_docs:", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("<available_skills>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("<name>deploy-helper</name>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("<description>Helps inspect deployment state.</description>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("<source>workspace</source>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("<name>incident-helper</name>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("<description>Helps triage channel incidents.</description>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("<source>channel</source>", fake.LastInput, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-helper", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("local_rules:", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("Use scratch/ for temporary or generated working files", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("Use SYSTEM.md to record environment modifications", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("Skill docs are discoverable context", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("respond exactly [SILENT]", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("event_files:", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("<delegation_context>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("title: triage build failure", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("- channel: ops", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("- requestId: req-42", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("- ts: current-3", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains($"- {attachmentPath}", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("channel_history:", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("[Alice]: first human message", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("[U2]: second human message", fake.LastInput, StringComparison.Ordinal);
        Assert.DoesNotContain("bot reply", fake.LastInput, StringComparison.Ordinal);
        Assert.DoesNotContain("current prompt duplicate", fake.LastInput, StringComparison.Ordinal);
        Assert.DoesNotContain("<slack_attachments>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("### Current Workspace Memory", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("channel memory: deploy window is Friday", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("### Parent Workspace Memory", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("global memory: use project conventions", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("system_configuration_log:", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("installed package: ripgrep", fake.LastInput, StringComparison.Ordinal);
        Assert.EndsWith("inspect the attached notes", fake.LastInput, StringComparison.Ordinal);

        Assert.NotNull(debugJson);
        using var debugDocument = JsonDocument.Parse(debugJson!);
        var debugRoot = debugDocument.RootElement;
        Assert.Equal("openai", debugRoot.GetProperty("provider").GetString());
        Assert.Equal("gpt-5.4", debugRoot.GetProperty("model").GetString());
        Assert.Equal("triage build failure", debugRoot.GetProperty("sessionName").GetString());
        Assert.Equal("inspect the attached notes", debugRoot.GetProperty("newUserMessage").GetString());
        Assert.Equal(0, debugRoot.GetProperty("restoredMessageCount").GetInt32());
        Assert.Equal(1, debugRoot.GetProperty("attachmentCount").GetInt32());
        Assert.Equal(0, debugRoot.GetProperty("imageAttachmentCount").GetInt32());
        Assert.Contains("<mom_runtime_context>", debugRoot.GetProperty("systemPrompt").GetString(), StringComparison.Ordinal);
        Assert.Contains("<delegation_context>", debugRoot.GetProperty("delegationContext").GetString(), StringComparison.Ordinal);
        Assert.Contains("inspect the attached notes", debugRoot.GetProperty("runnerInput").GetString(), StringComparison.Ordinal);
        Assert.Empty(debugRoot.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task ExecuteAsync_WhenAgentEnd_ReportsErrorStopReason()
    {
        var model = new Model { Provider = "openai", Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-responses" };
        var fake = new ScriptedRunner(model)
        {
            Events =
            [
                new AgentEndEvent("network down")
            ]
        };

        var delegationRunner = new RuntimeDelegationAgentRunner((_, _) => fake);

        var execution = await delegationRunner.ExecuteAsync(new DelegationRequest(
            "ping",
            Provider: "openai",
            Model: "gpt-5.4",
            WorkingDirectory: Path.GetTempPath()));

        Assert.Equal("error", execution.StopReason);
        Assert.Equal("network down", execution.Error);
        Assert.Null(execution.Usage);
        Assert.Empty(execution.ToolEvents);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesDefaultsWhenProviderModelOmitted()
    {
        var fake = new ScriptedRunner(new Model { Provider = "openai", Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-responses" })
        {
            Events = [new AgentEndEvent()]
        };

        string? capturedProvider = null;
        string? capturedModel = null;
        var delegationRunner = new RuntimeDelegationAgentRunner((provider, model) =>
        {
            capturedProvider = provider;
            capturedModel = model;
            return fake;
        });

        var execution = await delegationRunner.ExecuteAsync(new DelegationRequest(
            "hello",
            WorkingDirectory: Path.GetTempPath()));

        Assert.Equal("openai", capturedProvider);
        Assert.False(string.IsNullOrWhiteSpace(capturedModel));
        Assert.Equal("openai", execution.Provider);
        Assert.NotNull(fake.LastInput);
        Assert.Contains("<mom_runtime_context>", fake.LastInput, StringComparison.Ordinal);
        Assert.Contains("local_rules:", fake.LastInput, StringComparison.Ordinal);
        Assert.DoesNotContain("<delegation_context>", fake.LastInput, StringComparison.Ordinal);
        Assert.EndsWith("hello", fake.LastInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkspaceFactory_CapturesAttachedFiles()
    {
        var model = new Model { Provider = "openai", Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-responses" };
        var fake = new ScriptedRunner(model)
        {
            Events = [new AgentEndEvent()]
        };
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var attachedPath = Path.Combine(root, "scratch", "report.txt");

        string? capturedWorkingDirectory = null;
        var delegationRunner = new RuntimeDelegationAgentRunner(
            new MomOptions(),
            (provider, modelId, workingDirectory, attachFile) =>
            {
                capturedWorkingDirectory = workingDirectory;
                Directory.CreateDirectory(Path.GetDirectoryName(attachedPath)!);
                File.WriteAllText(attachedPath, "report");
                attachFile(attachedPath, "report");
                return fake;
            });

        try
        {
            var execution = await delegationRunner.ExecuteAsync(new DelegationRequest(
                "produce report",
                Provider: "openai",
                Model: "gpt-5.4",
                WorkingDirectory: root));

            Assert.Equal(root, capturedWorkingDirectory);
            var attached = Assert.Single(execution.Attachments!);
            Assert.Equal(attachedPath, attached);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LoadsAndSavesChannelContextSnapshot()
    {
        var model = new Model { Provider = "openai", Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-responses" };
        var fake = new ScriptedRunner(model)
        {
            Events =
            [
                new MessageEndEvent(new AssistantMessage([new TextContent("fresh answer")])
                {
                    StopReason = StopReason.EndTurn
                }),
                new AgentEndEvent()
            ]
        };

        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-context-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(root, "channel-a");
        Directory.CreateDirectory(workingDirectory);
        var contextPath = Path.Combine(workingDirectory, ChannelSessionStore.ContextFileName);
        new CodingAgentSessionStore(contextPath).Save(
            [new UserMessage("previous channel turn")],
            model,
            "saved channel session");

        var delegationRunner = new RuntimeDelegationAgentRunner(new MomOptions(), (_, _) => fake);

        string? debugJson = null;
        try
        {
            var execution = await delegationRunner.ExecuteAsync(new DelegationRequest(
                "continue from context",
                Provider: "openai",
                Model: "gpt-5.4",
                WorkingDirectory: workingDirectory));
            debugJson = await File.ReadAllTextAsync(Path.Combine(workingDirectory, "last_prompt.jsonl"));

            Assert.Equal("end_turn", execution.StopReason);
            Assert.NotNull(fake.RestoredSnapshot);
            Assert.Single(fake.RestoredSnapshot!.Messages);
            Assert.Equal("saved channel session", fake.RestoredSnapshot.Name);
            Assert.Equal("saved channel session", fake.SessionName);
            Assert.Contains("continue from context", fake.LastInput, StringComparison.Ordinal);

            var saved = new CodingAgentSessionStore(contextPath).Load();
            Assert.Equal("saved channel session", saved.Name);
            Assert.Equal("openai", saved.Provider);
            Assert.Equal("gpt-5.4", saved.Model);
            Assert.Equal(3, saved.Messages.Count);
            Assert.IsType<UserMessage>(saved.Messages[0]);
            Assert.IsType<UserMessage>(saved.Messages[1]);
            var assistant = Assert.IsType<AssistantMessage>(saved.Messages[2]);
            Assert.Contains("fresh answer", string.Join("\n", assistant.Content.OfType<TextContent>().Select(static text => text.Text)), StringComparison.Ordinal);

            Assert.NotNull(debugJson);
            using var debugDocument = JsonDocument.Parse(debugJson!);
            var debugRoot = debugDocument.RootElement;
            Assert.Equal("saved channel session", debugRoot.GetProperty("sessionName").GetString());
            Assert.Equal(1, debugRoot.GetProperty("restoredMessageCount").GetInt32());
            var messages = debugRoot.GetProperty("messages").EnumerateArray().ToArray();
            var message = Assert.Single(messages);
            Assert.Equal("user", message.GetProperty("role").GetString());
            var content = Assert.Single(message.GetProperty("content").EnumerateArray().ToArray());
            Assert.Equal("previous channel turn", content.GetProperty("text").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ScriptedRunner : ICodingAgentRunner
    {
        public ScriptedRunner(Model model)
        {
            Model = model;
        }

        public List<AgentEvent> Events { get; set; } = [];
        public List<ChatMessage> MutableMessages { get; } = [];
        public IReadOnlyList<ChatMessage> Messages => MutableMessages;
        public Model Model { get; }
        public string? SessionName { get; set; }
        public ThinkingLevel? ThinkingLevel { get; set; }
        public AgentQueueMode SteeringMode { get; set; } = AgentQueueMode.OneAtATime;
        public AgentQueueMode FollowUpMode { get; set; } = AgentQueueMode.OneAtATime;
        public string? LastInput { get; private set; }
        public CodingAgentSessionSnapshot? RestoredSnapshot { get; private set; }

        public IReadOnlyList<string> GetProviders() => [Model.Provider];
        public IReadOnlyList<Model> GetModels(string provider) => [Model];
        public Model SelectModel(string? providerId, string? modelId) => Model;
        public ProviderAuthStatus GetAuthStatus(string? providerId = null) =>
            new(providerId ?? Model.Provider, false, "none", false, false, "test");
        public Tau.Ai.Auth.OAuth.IOAuthProvider? GetOAuthProvider(string providerId) => null;
        public void SaveOAuthCredentials(string providerId, Tau.Ai.Auth.OAuth.OAuthCredentials credentials) { }
        public bool Logout(string providerId) => false;
        public bool RefreshSkills(IReadOnlyList<CodingAgentSkill> skills) => false;
        public bool RefreshSystemPromptResources(
            IReadOnlyList<CodingAgentSkill> skills,
            IReadOnlyList<CodingAgentContextFile> contextFiles) => false;
        public CodingAgentSessionStats GetSessionStats(string? sessionFile = null) =>
            new(Model.Provider, Model.Id, 0, 0, 0, 0, 0, 0, Model.ContextWindow, null, sessionFile);
        public Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CodingAgentBranchSummaryResult> SummarizeBranchAsync(
            IReadOnlyList<ChatMessage> messages,
            string? customInstructions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void Steer(string input) { }
        public void FollowUp(string input) { }
        public void RestoreSession(CodingAgentSessionSnapshot snapshot)
        {
            RestoredSnapshot = snapshot;
            MutableMessages.Clear();
            MutableMessages.AddRange(snapshot.Messages);
            SessionName = snapshot.Name;
        }

        public void ResetSession()
        {
            MutableMessages.Clear();
            SessionName = null;
        }

        public async IAsyncEnumerable<AgentEvent> RunAsync(string input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastInput = input;
            MutableMessages.Add(new UserMessage(input));
            foreach (var evt in Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evt is ToolExecutionStartEvent)
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                }
                else if (evt is MessageEndEvent messageEnd)
                {
                    MutableMessages.Add(messageEnd.Message);
                }

                yield return evt;
            }
        }

        public IAsyncEnumerable<AgentEvent> RunAsync(IReadOnlyList<ContentBlock> input, CancellationToken cancellationToken = default)
        {
            var text = string.Join(Environment.NewLine, input.OfType<TextContent>().Select(content => content.Text));
            return RunAsync(text, cancellationToken);
        }
    }
}
