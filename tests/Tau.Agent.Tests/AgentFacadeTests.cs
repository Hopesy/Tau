using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Agent.Tests;

public sealed class AgentFacadeTests
{
    [Fact]
    public async Task PromptAsync_AddsPromptAndEmitsMessageLifecycle()
    {
        var provider = new RecordingProvider();
        var agent = CreateAgent(provider);
        var events = new List<AgentEvent>();
        agent.Subscribe(events.Add);

        await agent.PromptAsync("hello").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["hello"], Assert.Single(provider.Calls).UserTexts);
        Assert.Collection(
            agent.State.Messages,
            message => Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(message))),
            message => Assert.Equal("turn 1", ReadText(Assert.IsType<AssistantMessage>(message))));

        Assert.Equal(["agent_start", "turn_start", "message_start", "message_end", "message_start", "message_end", "turn_end", "agent_end"], events.Select(evt => evt.Type));
        Assert.Equal(["user", "assistant"], events.OfType<MessageStartEvent>().Select(evt => evt.Message.Role));
        Assert.Equal(["user", "assistant"], events.OfType<MessageEndEvent>().Select(evt => evt.Message.Role));
        Assert.False(agent.State.IsStreaming);
    }

    [Fact]
    public async Task PromptAsync_RejectsConcurrentPromptWhileActive()
    {
        var provider = new BlockingProvider();
        var agent = CreateAgent(provider);

        var promptTask = agent.PromptAsync("first");
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.PromptAsync("second"));
        Assert.Contains("already processing", error.Message, StringComparison.OrdinalIgnoreCase);

        provider.CurrentStream.Push(new DoneEvent(new AssistantMessage([new TextContent("done")])));
        await promptTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ContinueAsync_FromAssistantTailRequiresQueuedMessage()
    {
        var provider = new RecordingProvider();
        var agent = CreateAgent(provider);

        await agent.PromptAsync("first").WaitAsync(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ContinueAsync());
        Assert.Equal("Cannot continue from message role: assistant.", error.Message);

        agent.Steer(new UserMessage("queued steering"));
        await agent.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(["first"], provider.Calls[0].UserTexts);
        Assert.Equal(["first", "queued steering"], provider.Calls[1].UserTexts);
        Assert.Collection(
            agent.State.Messages,
            message => Assert.Equal("first", ReadText(Assert.IsType<UserMessage>(message))),
            message => Assert.Equal("turn 1", ReadText(Assert.IsType<AssistantMessage>(message))),
            message => Assert.Equal("queued steering", ReadText(Assert.IsType<UserMessage>(message))),
            message => Assert.Equal("turn 2", ReadText(Assert.IsType<AssistantMessage>(message))));
    }

    [Fact]
    public async Task PromptAsync_WaitsForAgentEndListenersBeforeIdle()
    {
        var provider = new RecordingProvider();
        var agent = CreateAgent(provider);
        var listenerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseListener = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        agent.Subscribe(async (evt, _) =>
        {
            if (evt is AgentEndEvent)
            {
                listenerEntered.SetResult();
                await releaseListener.Task.ConfigureAwait(false);
            }
        });

        var promptTask = agent.PromptAsync("hello");
        await listenerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(promptTask.IsCompleted);
        Assert.Same(promptTask, agent.WaitForIdleAsync());

        releaseListener.SetResult();
        await promptTask.WaitAsync(TimeSpan.FromSeconds(5));
        await agent.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PromptAsync_EmitsToolResultMessageLifecycle()
    {
        var provider = new ToolThenTextProvider();
        var agent = CreateAgent(provider, tools: [new EchoTool()]);
        var events = new List<AgentEvent>();
        agent.Subscribe(events.Add);

        await agent.PromptAsync("use tool").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(events.OfType<MessageEndEvent>(), evt => evt.Message is ToolResultMessage);
        Assert.Collection(
            agent.State.Messages,
            message => Assert.Equal("use tool", ReadText(Assert.IsType<UserMessage>(message))),
            message => Assert.IsType<AssistantMessage>(message),
            message => Assert.Equal("hello", ReadText(Assert.IsType<ToolResultMessage>(message))),
            message => Assert.Equal("done", ReadText(Assert.IsType<AssistantMessage>(message))));
    }

    [Fact]
    public async Task PromptAsync_StreamFaultAppendsFailureAssistantMessageAndReportsItAtAgentEnd()
    {
        var provider = new BlockingProvider();
        var agent = CreateAgent(provider);
        var events = new List<AgentEvent>();
        agent.Subscribe(events.Add);

        var promptTask = agent.PromptAsync("hello");
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        provider.CurrentStream.Fault(new InvalidOperationException("boom"));
        await promptTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Collection(
            agent.State.Messages,
            message => Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(message))),
            message =>
            {
                var assistant = Assert.IsType<AssistantMessage>(message);
                Assert.Equal("boom", assistant.ErrorMessage);
                Assert.Equal(StopReason.Error, assistant.StopReason);
                Assert.Equal(string.Empty, ReadText(assistant));
            });
        Assert.Equal("boom", agent.State.ErrorMessage);
        Assert.False(agent.State.IsStreaming);

        var end = Assert.IsType<AgentEndEvent>(events.Last());
        Assert.Equal("boom", end.ErrorMessage);
        var failure = Assert.Single(end.Messages);
        var failureAssistant = Assert.IsType<AssistantMessage>(failure);
        Assert.Equal("boom", failureAssistant.ErrorMessage);
        Assert.Equal(StopReason.Error, failureAssistant.StopReason);
        Assert.Equal(string.Empty, ReadText(failureAssistant));
    }

    [Fact]
    public async Task PromptAsync_CancellationAppendsAbortedAssistantMessageAndReportsItAtAgentEnd()
    {
        var provider = new BlockingProvider();
        var agent = CreateAgent(provider);
        var events = new List<AgentEvent>();
        agent.Subscribe(events.Add);
        using var cancellationTokenSource = new CancellationTokenSource();

        var promptTask = agent.PromptAsync("hello", cancellationToken: cancellationTokenSource.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellationTokenSource.Cancel();
        await promptTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Collection(
            agent.State.Messages,
            message => Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(message))),
            message =>
            {
                var assistant = Assert.IsType<AssistantMessage>(message);
                Assert.Equal("Operation canceled.", assistant.ErrorMessage);
                Assert.Equal(StopReason.Aborted, assistant.StopReason);
                Assert.Equal(string.Empty, ReadText(assistant));
            });
        Assert.Equal("Operation canceled.", agent.State.ErrorMessage);
        Assert.False(agent.State.IsStreaming);

        var end = Assert.IsType<AgentEndEvent>(events.Last());
        Assert.Equal("Operation canceled.", end.ErrorMessage);
        var failure = Assert.Single(end.Messages);
        var failureAssistant = Assert.IsType<AssistantMessage>(failure);
        Assert.Equal("Operation canceled.", failureAssistant.ErrorMessage);
        Assert.Equal(StopReason.Aborted, failureAssistant.StopReason);
        Assert.Equal(string.Empty, ReadText(failureAssistant));
    }

    [Fact]
    public void Reset_ClearsMessagesAndQueuesButKeepsConfiguration()
    {
        var provider = new RecordingProvider();
        var agent = CreateAgent(provider, [new UserMessage("existing")]);
        agent.Steer(new UserMessage("steer"));
        agent.FollowUp(new UserMessage("follow"));

        agent.Reset();

        Assert.Empty(agent.State.Messages);
        Assert.False(agent.HasQueuedMessages);
        Assert.Equal("system", agent.State.SystemPrompt);
        Assert.Equal("test-model", agent.State.Model?.Id);
        Assert.Empty(agent.State.Tools);
    }

    private static Tau.Agent.Agent CreateAgent(
        IStreamProvider provider,
        IReadOnlyList<ChatMessage>? messages = null,
        IReadOnlyList<IAgentTool>? tools = null)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        var model = new Model
        {
            Provider = "test",
            Id = "test-model",
            Name = "Test Model",
            Api = provider.Api
        };

        return new Tau.Agent.Agent(new AgentOptions
        {
            Model = model,
            ProviderRegistry = registry,
            SystemPrompt = "system",
            Messages = messages ?? [],
            Tools = tools ?? []
        });
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
        public string Api => "test-recording-agent-facade";
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

    private sealed class BlockingProvider : IStreamProvider
    {
        public string Api => "test-blocking-agent-facade";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AssistantMessageStream CurrentStream { get; private set; } = new();

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            CreateStream();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            CreateStream();

        private AssistantMessageStream CreateStream()
        {
            CurrentStream = new AssistantMessageStream();
            Started.TrySetResult();
            return CurrentStream;
        }
    }

    private sealed class ToolThenTextProvider : IStreamProvider
    {
        private int _calls;

        public string Api => "test-tool-agent-facade";

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete();

        private AssistantMessageStream Complete()
        {
            var stream = new AssistantMessageStream();
            var message = _calls++ == 0
                ? new AssistantMessage([new ToolCallContent("tool-1", "echo", """{"text":"hello"}""")])
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
        public System.Text.Json.JsonElement ParameterSchema => System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            string toolCallId,
            System.Text.Json.JsonElement args,
            CancellationToken ct = default,
            Func<ToolUpdate, Task>? onUpdate = null)
        {
            var text = args.TryGetProperty("text", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            return Task.FromResult(new ToolResult([new TextContent(text)]));
        }
    }

    private sealed record CallRecord(IReadOnlyList<string> UserTexts);
}
