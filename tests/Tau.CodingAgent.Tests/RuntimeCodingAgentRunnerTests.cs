using Tau.Ai;
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
}
