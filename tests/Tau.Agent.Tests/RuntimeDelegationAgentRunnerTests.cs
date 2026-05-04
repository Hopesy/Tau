using Tau.Agent;
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
    }

    private sealed class ScriptedRunner : ICodingAgentRunner
    {
        public ScriptedRunner(Model model)
        {
            Model = model;
        }

        public List<AgentEvent> Events { get; set; } = [];
        public IReadOnlyList<ChatMessage> Messages { get; } = [];
        public Model Model { get; }
        public string? SessionName { get; set; }

        public IReadOnlyList<string> GetProviders() => [Model.Provider];
        public IReadOnlyList<Model> GetModels(string provider) => [Model];
        public Model SelectModel(string? providerId, string? modelId) => Model;
        public ProviderAuthStatus GetAuthStatus(string? providerId = null) =>
            new(providerId ?? Model.Provider, false, "none", false, false, "test");
        public CodingAgentSessionStats GetSessionStats(string? sessionFile = null) =>
            new(Model.Provider, Model.Id, 0, 0, 0, 0, 0, null, sessionFile);
        public Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void RestoreSession(CodingAgentSessionSnapshot snapshot) { }
        public void ResetSession() { }

        public async IAsyncEnumerable<AgentEvent> RunAsync(string input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var evt in Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evt is ToolExecutionStartEvent)
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                }
                yield return evt;
            }
        }
    }
}
