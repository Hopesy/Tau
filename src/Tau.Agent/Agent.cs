using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Agent;

public sealed record AgentOptions
{
    public required Model Model { get; init; }
    public required ProviderRegistry ProviderRegistry { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public IReadOnlyList<IAgentTool> Tools { get; init; } = [];
    public IReadOnlyList<IToolInterceptor> Interceptors { get; init; } = [];
    public SimpleStreamOptions? StreamOptions { get; init; }
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? TransformContext { get; init; }
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContextAsync { get; init; }
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? ConvertToLlm { get; init; }
    public AgentQueueMode SteeringMode { get; init; } = AgentQueueMode.OneAtATime;
    public AgentQueueMode FollowUpMode { get; init; } = AgentQueueMode.OneAtATime;
    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;
    public ITauLogSink LogSink { get; init; } = NullTauLogSink.Instance;
    public TauRuntimeLogContext? LogContext { get; init; }
}

public sealed class Agent
{
    private readonly object _sync = new();
    private readonly List<Func<AgentEvent, CancellationToken, Task>> _listeners = [];
    private readonly AgentRuntime _runtime = new();
    private Task? _activeRun;
    private CancellationTokenSource? _activeCts;
    private Model _model;
    private ProviderRegistry _providerRegistry;
    private string? _systemPrompt;
    private IReadOnlyList<IAgentTool> _tools;
    private IReadOnlyList<IToolInterceptor> _interceptors;

    public Agent(AgentOptions options)
    {
        _model = options.Model;
        _providerRegistry = options.ProviderRegistry;
        _systemPrompt = options.SystemPrompt;
        _tools = options.Tools.ToArray();
        _interceptors = options.Interceptors.ToArray();
        StreamOptions = options.StreamOptions;
        TransformContext = options.TransformContext;
        TransformContextAsync = options.TransformContextAsync;
        ConvertToLlm = options.ConvertToLlm;
        ToolExecution = options.ToolExecution;
        LogSink = options.LogSink;
        LogContext = options.LogContext;
        SteeringMode = options.SteeringMode;
        FollowUpMode = options.FollowUpMode;

        SyncStateConfiguration();
        foreach (var message in options.Messages)
        {
            _runtime.AddMessage(message);
        }
    }

    public AgentState State => _runtime.State;

    public Model Model
    {
        get => _model;
        set
        {
            _model = value;
            SyncStateConfiguration();
        }
    }

    public ProviderRegistry ProviderRegistry
    {
        get => _providerRegistry;
        set => _providerRegistry = value;
    }

    public string? SystemPrompt
    {
        get => _systemPrompt;
        set
        {
            _systemPrompt = value;
            SyncStateConfiguration();
        }
    }

    public IReadOnlyList<IAgentTool> Tools
    {
        get => _tools;
        set
        {
            _tools = value.ToArray();
            SyncStateConfiguration();
        }
    }

    public IReadOnlyList<IToolInterceptor> Interceptors
    {
        get => _interceptors;
        set => _interceptors = value.ToArray();
    }

    public SimpleStreamOptions? StreamOptions { get; set; }
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? TransformContext { get; set; }
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContextAsync { get; set; }
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? ConvertToLlm { get; set; }
    public ToolExecutionMode ToolExecution { get; set; }
    public ITauLogSink LogSink { get; set; }
    public TauRuntimeLogContext? LogContext { get; set; }

    public AgentQueueMode SteeringMode
    {
        get => _runtime.SteeringMode;
        set => _runtime.SteeringMode = value;
    }

    public AgentQueueMode FollowUpMode
    {
        get => _runtime.FollowUpMode;
        set => _runtime.FollowUpMode = value;
    }

    public bool HasQueuedMessages => _runtime.HasQueuedMessages;

    public IDisposable Subscribe(Action<AgentEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Subscribe((evt, _) =>
        {
            listener(evt);
            return Task.CompletedTask;
        });
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_sync)
        {
            _listeners.Add(listener);
        }

        return new Subscription(() =>
        {
            lock (_sync)
            {
                _listeners.Remove(listener);
            }
        });
    }

    public Task PromptAsync(string input, IReadOnlyList<ImageContent>? images = null, CancellationToken cancellationToken = default)
    {
        var content = new List<ContentBlock> { new TextContent(input) };
        if (images is { Count: > 0 })
        {
            content.AddRange(images);
        }

        return PromptAsync(new UserMessage(content), cancellationToken);
    }

    public Task PromptAsync(ChatMessage message, CancellationToken cancellationToken = default) =>
        PromptAsync([message], cancellationToken);

    public Task PromptAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one prompt message is required.", nameof(messages));
        }

        return StartRun(token => RunRuntimeAsync(messages, skipInitialSteeringPoll: false, token), cancellationToken);
    }

    public Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfActive("Agent is already processing. Wait for completion before continuing.");

        var lastMessage = State.Messages.LastOrDefault();
        if (lastMessage is null)
        {
            throw new InvalidOperationException("No messages to continue from.");
        }

        if (lastMessage is AssistantMessage)
        {
            var queuedSteering = _runtime.DrainSteeringMessages();
            if (queuedSteering.Count > 0)
            {
                return StartRun(
                    token => RunRuntimeAsync(queuedSteering, skipInitialSteeringPoll: true, token),
                    cancellationToken);
            }

            var queuedFollowUps = _runtime.DrainFollowUpMessages();
            if (queuedFollowUps.Count > 0)
            {
                return StartRun(
                    token => RunRuntimeAsync(queuedFollowUps, skipInitialSteeringPoll: false, token),
                    cancellationToken);
            }

            throw new InvalidOperationException("Cannot continue from message role: assistant.");
        }

        return StartRun(
            token => RunRuntimeAsync([], skipInitialSteeringPoll: false, token),
            cancellationToken);
    }

    public void Steer(ChatMessage message) => _runtime.Steer(message);
    public void FollowUp(ChatMessage message) => _runtime.FollowUp(message);
    public void ClearSteeringQueue() => _runtime.ClearSteeringQueue();
    public void ClearFollowUpQueue() => _runtime.ClearFollowUpQueue();

    public void ClearAllQueues()
    {
        ClearSteeringQueue();
        ClearFollowUpQueue();
    }

    public void Abort()
    {
        _activeCts?.Cancel();
        _runtime.Abort();
    }

    public Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _activeRun ?? Task.CompletedTask;
        }
    }

    public void Reset()
    {
        _runtime.Reset();
        SyncStateConfiguration();
    }

    private Task StartRun(Func<CancellationToken, Task> executor, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var placeholder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            if (_activeRun is not null)
            {
                cts.Dispose();
                throw new InvalidOperationException(
                    "Agent is already processing a prompt. Use Steer() or FollowUp() to queue messages, or wait for completion.");
            }

            _activeCts = cts;
            _activeRun = placeholder.Task;
        }

        var task = RunAndClearAsync(executor, cts);
        lock (_sync)
        {
            if (ReferenceEquals(_activeCts, cts))
            {
                _activeRun = task;
            }
        }

        return task;
    }

    private async Task RunAndClearAsync(Func<CancellationToken, Task> executor, CancellationTokenSource cts)
    {
        try
        {
            await executor(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeCts, cts))
                {
                    _activeRun = null;
                    _activeCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task RunRuntimeAsync(
        IReadOnlyList<ChatMessage> promptMessages,
        bool skipInitialSteeringPoll,
        CancellationToken cancellationToken)
    {
        var emittedPromptMessages = promptMessages.Count == 0;
        var sawAgentEnd = false;

        SyncStateConfiguration();
        State.SetStreaming(true);
        State.SetError(null);

        foreach (var message in promptMessages)
        {
            _runtime.AddMessage(message);
        }

        try
        {
            await foreach (var runtimeEvent in _runtime.RunAsync(CreateConfig(skipInitialSteeringPoll), cancellationToken)
                               .ConfigureAwait(false))
            {
                var evt = NormalizeRuntimeEvent(runtimeEvent);
                await EmitAsync(evt, cancellationToken).ConfigureAwait(false);
                if (!emittedPromptMessages && evt is TurnStartEvent)
                {
                    foreach (var message in promptMessages)
                    {
                        await EmitAsync(new MessageStartEvent(message), cancellationToken).ConfigureAwait(false);
                        await EmitAsync(new MessageEndEvent(message), cancellationToken).ConfigureAwait(false);
                    }

                    emittedPromptMessages = true;
                }

                if (evt is AgentEndEvent)
                {
                    sawAgentEnd = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string error = "Operation canceled.";
            await EmitFailureAsync(error, aborted: true, sawAgentEnd, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await EmitFailureAsync(ex.Message, aborted: false, sawAgentEnd, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            State.SetStreaming(false);
        }
    }

    private static AgentEvent NormalizeRuntimeEvent(AgentEvent evt)
    {
        if (evt is not AgentEndEvent end || string.IsNullOrWhiteSpace(end.ErrorMessage))
        {
            return evt;
        }

        var failureMessage = end.Messages
            .OfType<AssistantMessage>()
            .LastOrDefault(static message =>
                !string.IsNullOrWhiteSpace(message.ErrorMessage) ||
                message.StopReason is StopReason.Error or StopReason.Aborted);

        return failureMessage is null
            ? evt
            : new AgentEndEvent(end.ErrorMessage, [failureMessage]);
    }

    private AgentLoopConfig CreateConfig(bool skipInitialSteeringPoll) =>
        new()
        {
            Model = _model,
            ProviderRegistry = _providerRegistry,
            Tools = _tools,
            Interceptors = _interceptors,
            LogSink = LogSink,
            LogContext = LogContext,
            SystemPrompt = _systemPrompt,
            StreamOptions = StreamOptions,
            DefaultExecutionMode = ToolExecution,
            TransformContext = TransformContext,
            TransformContextAsync = TransformContextAsync,
            ConvertToLlm = ConvertToLlm,
            SkipInitialSteeringPoll = skipInitialSteeringPoll
        };

    private async Task EmitFailureAsync(
        string error,
        bool aborted,
        bool sawAgentEnd,
        CancellationToken cancellationToken)
    {
        State.SetError(error);
        var failureMessage = CreateFailureMessage(error, aborted);
        _runtime.State.AddMessage(failureMessage);

        if (!sawAgentEnd)
        {
            await EmitAsync(new AgentEndEvent(error, [failureMessage]), cancellationToken).ConfigureAwait(false);
        }
    }

    private AssistantMessage CreateFailureMessage(string error, bool aborted)
    {
        return new AssistantMessage([new TextContent(string.Empty)])
        {
            ErrorMessage = error,
            StopReason = aborted ? StopReason.Aborted : StopReason.Error,
            Usage = new Usage(0, 0, 0, 0),
            Api = _model.Api,
            Provider = _model.Provider,
            Model = _model.Id,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private async Task EmitAsync(AgentEvent evt, CancellationToken cancellationToken)
    {
        Func<AgentEvent, CancellationToken, Task>[] listeners;
        lock (_sync)
        {
            listeners = _listeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            await listener(evt, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ThrowIfActive(string message)
    {
        lock (_sync)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private void SyncStateConfiguration() => State.Configure(_systemPrompt, _model, _tools);

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                unsubscribe();
            }
        }
    }
}
