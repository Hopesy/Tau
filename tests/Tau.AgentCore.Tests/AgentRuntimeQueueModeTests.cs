using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

public sealed class AgentRuntimeQueueModeTests
{
    [Fact]
    public async Task RunAsync_SteeringOneAtATimeProcessesOneQueuedMessagePerTurn()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime
        {
            SteeringMode = AgentQueueMode.OneAtATime
        };
        runtime.AddMessage(new UserMessage("initial"));
        runtime.Steer(new UserMessage("steer 1"));
        runtime.Steer(new UserMessage("steer 2"));
        Assert.Equal(2, runtime.PendingMessageCount);

        await DrainAsync(runtime.RunAsync(CreateConfig(provider)));

        Assert.Equal(0, runtime.PendingMessageCount);
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(["initial", "steer 1"], provider.Calls[0].UserTexts);
        Assert.Equal(["initial", "steer 1", "steer 2"], provider.Calls[1].UserTexts);
    }

    [Fact]
    public async Task RunAsync_SteeringAllProcessesQueuedMessagesInSingleTurn()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime
        {
            SteeringMode = AgentQueueMode.All
        };
        runtime.AddMessage(new UserMessage("initial"));
        runtime.Steer(new UserMessage("steer 1"));
        runtime.Steer(new UserMessage("steer 2"));
        Assert.Equal(2, runtime.PendingMessageCount);

        await DrainAsync(runtime.RunAsync(CreateConfig(provider)));

        Assert.Equal(0, runtime.PendingMessageCount);
        var call = Assert.Single(provider.Calls);
        Assert.Equal(["initial", "steer 1", "steer 2"], call.UserTexts);
    }

    [Fact]
    public async Task RunAsync_SteeringQueuedMessagesEmitLifecycleBeforeAssistantResponse()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime
        {
            SteeringMode = AgentQueueMode.All
        };
        runtime.AddMessage(new UserMessage("initial"));
        runtime.Steer(new UserMessage("steer 1"));
        runtime.Steer(new UserMessage("steer 2"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(provider)));

        Assert.Equal(
            [
                "agent_start",
                "turn_start",
                "message_start",
                "message_end",
                "message_start",
                "message_end",
                "message_start",
                "message_end",
                "turn_end",
                "agent_end"
            ],
            events.Select(evt => evt.Type));
        Assert.Equal(
            ["steer 1", "steer 2", "turn 1"],
            events.OfType<MessageStartEvent>().Select(evt => ReadText(evt.Message)));
        Assert.Equal(
            ["steer 1", "steer 2", "turn 1"],
            events.OfType<MessageEndEvent>().Select(evt => ReadText(evt.Message)));
    }

    [Fact]
    public async Task RunAsync_FollowUpOneAtATimeProcessesOneQueuedMessagePerOuterTurn()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime
        {
            FollowUpMode = AgentQueueMode.OneAtATime
        };
        runtime.AddMessage(new UserMessage("initial"));
        runtime.FollowUp(new UserMessage("follow 1"));
        runtime.FollowUp(new UserMessage("follow 2"));
        Assert.Equal(2, runtime.PendingMessageCount);

        await DrainAsync(runtime.RunAsync(CreateConfig(provider)));

        Assert.Equal(0, runtime.PendingMessageCount);
        Assert.Equal(3, provider.Calls.Count);
        Assert.Equal(["initial"], provider.Calls[0].UserTexts);
        Assert.Equal(["initial", "follow 1"], provider.Calls[1].UserTexts);
        Assert.Equal(["initial", "follow 1", "follow 2"], provider.Calls[2].UserTexts);
    }

    [Fact]
    public async Task RunAsync_FollowUpAllProcessesQueuedMessagesInSingleOuterTurn()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime
        {
            FollowUpMode = AgentQueueMode.All
        };
        runtime.AddMessage(new UserMessage("initial"));
        runtime.FollowUp(new UserMessage("follow 1"));
        runtime.FollowUp(new UserMessage("follow 2"));
        Assert.Equal(2, runtime.PendingMessageCount);

        await DrainAsync(runtime.RunAsync(CreateConfig(provider)));

        Assert.Equal(0, runtime.PendingMessageCount);
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(["initial"], provider.Calls[0].UserTexts);
        Assert.Equal(["initial", "follow 1", "follow 2"], provider.Calls[1].UserTexts);
    }

    [Fact]
    public async Task RunAsync_FollowUpQueuedMessagesEmitLifecycleAtNextTurnStart()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime
        {
            FollowUpMode = AgentQueueMode.All
        };
        runtime.AddMessage(new UserMessage("initial"));
        runtime.FollowUp(new UserMessage("follow 1"));
        runtime.FollowUp(new UserMessage("follow 2"));

        var events = await CollectAsync(runtime.RunAsync(CreateConfig(provider)));

        Assert.Equal(
            [
                "agent_start",
                "turn_start",
                "message_start",
                "message_end",
                "turn_end",
                "turn_start",
                "message_start",
                "message_end",
                "message_start",
                "message_end",
                "message_start",
                "message_end",
                "turn_end",
                "agent_end"
            ],
            events.Select(evt => evt.Type));
        Assert.Equal(
            ["turn 1", "follow 1", "follow 2", "turn 2"],
            events.OfType<MessageStartEvent>().Select(evt => ReadText(evt.Message)));
        Assert.Equal(
            ["turn 1", "follow 1", "follow 2", "turn 2"],
            events.OfType<MessageEndEvent>().Select(evt => ReadText(evt.Message)));
    }

    [Fact]
    public void ClearQueuesResetsPendingMessageCount()
    {
        var runtime = new AgentRuntime();
        runtime.Steer(new UserMessage("steer"));
        runtime.FollowUp(new UserMessage("follow"));
        Assert.Equal(2, runtime.PendingMessageCount);

        runtime.ClearSteeringQueue();
        Assert.Equal(1, runtime.PendingMessageCount);

        runtime.ClearFollowUpQueue();
        Assert.Equal(0, runtime.PendingMessageCount);
    }

    private static AgentLoopConfig CreateConfig(RecordingProvider provider)
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
            Tools = []
        };
    }

    private static async Task<IReadOnlyList<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var collected = new List<AgentEvent>();
        await foreach (var evt in events)
        {
            collected.Add(evt);
        }

        return collected;
    }

    private static async Task DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private static string ReadText(ChatMessage message) =>
        message switch
        {
            UserMessage user => string.Join("\n", user.Content.OfType<TextContent>().Select(content => content.Text)),
            AssistantMessage assistant => string.Join("\n", assistant.Content.OfType<TextContent>().Select(content => content.Text)),
            ToolResultMessage tool => string.Join("\n", tool.Content.OfType<TextContent>().Select(content => content.Text)),
            _ => string.Empty
        };

    private sealed class RecordingProvider : IStreamProvider
    {
        public string Api => "test-recording";
        public List<CallRecord> Calls { get; } = [];

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete(context);

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete(context);

        private AssistantMessageStream Complete(LlmContext context)
        {
            Calls.Add(new CallRecord(
                context.Messages
                    .OfType<UserMessage>()
                    .Select(ReadText)
                    .ToArray()));

            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent($"turn {Calls.Count}")])));
            return stream;
        }
    }

    private sealed record CallRecord(IReadOnlyList<string> UserTexts);
}
