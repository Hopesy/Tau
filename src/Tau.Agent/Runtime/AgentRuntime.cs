using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Agent.Runtime;

/// <summary>
/// Agent runtime with double-loop execution:
///   Outer: follow-up messages
///     Inner: tool calls + steering messages
/// Mirrors pi-mono's Agent + agent-loop.ts.
/// </summary>
public sealed class AgentRuntime
{
    private readonly Channel<ChatMessage> _steeringQueue = Channel.CreateUnbounded<ChatMessage>();
    private readonly Channel<ChatMessage> _followUpQueue = Channel.CreateUnbounded<ChatMessage>();
    private CancellationTokenSource? _runCts;
    private readonly TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentState State { get; } = new();
    public AgentQueueMode SteeringMode { get; set; } = AgentQueueMode.OneAtATime;
    public AgentQueueMode FollowUpMode { get; set; } = AgentQueueMode.OneAtATime;
    public bool HasQueuedMessages => _steeringQueue.Reader.TryPeek(out _) || _followUpQueue.Reader.TryPeek(out _);

    public void AddMessage(ChatMessage message) => State.AddMessage(message);
    public void Steer(ChatMessage message) => _steeringQueue.Writer.TryWrite(message);
    public void FollowUp(ChatMessage message) => _followUpQueue.Writer.TryWrite(message);
    public void ClearSteeringQueue() => DrainChannel(_steeringQueue);
    public void ClearFollowUpQueue() => DrainChannel(_followUpQueue);
    public IReadOnlyList<ChatMessage> DrainSteeringMessages() => DrainQueuedMessagesToList(_steeringQueue, SteeringMode);
    public IReadOnlyList<ChatMessage> DrainFollowUpMessages() => DrainQueuedMessagesToList(_followUpQueue, FollowUpMode);

    public void Abort() => _runCts?.Cancel();
    public Task WaitForIdleAsync() => _idleTcs.Task;

    public void Reset()
    {
        State.Reset();
        DrainChannel(_steeringQueue);
        DrainChannel(_followUpQueue);
    }

    public EventStream<AgentEvent, ChatMessage[]> RunStream(
        AgentLoopConfig config,
        CancellationToken ct = default)
    {
        var stream = CreateEventStream();

        _ = Task.Run(async () =>
        {
            var sawAgentEnd = false;
            try
            {
                await foreach (var evt in RunAsync(config, ct).ConfigureAwait(false))
                {
                    stream.Push(evt);
                    if (evt is AgentEndEvent)
                    {
                        sawAgentEnd = true;
                    }
                }

                if (!sawAgentEnd)
                {
                    stream.Fault(new InvalidOperationException("Agent stream completed without agent_end."));
                }
            }
            catch (Exception ex)
            {
                stream.Fault(ex);
            }
        }, CancellationToken.None);

        return stream;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentLoopConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _runCts.Token;
        State.Configure(config.SystemPrompt, config.Model, config.Tools);

        yield return new AgentStartEvent();

        try
        {
            var turnIndex = 0;
            var skipInitialSteeringPoll = config.SkipInitialSteeringPoll;

            // Outer loop: follow-up messages
            do
            {
                // Inner loop: tool calls + steering
                bool hasMoreWork;
                do
                {
                    hasMoreWork = false;

                    if (skipInitialSteeringPoll)
                    {
                        skipInitialSteeringPoll = false;
                    }
                    else
                    {
                        DrainQueuedMessages(_steeringQueue, SteeringMode);
                    }

                    yield return new TurnStartEvent(turnIndex);

                    // Build context and stream LLM response
                    LlmContext context = default;
                    AssistantMessage? contextFailureMessage = null;
                    try
                    {
                        context = await ContextTransformer.BuildAsync(config, State.Messages, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        const string error = "Operation canceled.";
                        var failureMessage = CreateFailureMessage(error, StopReason.Aborted, config.Model);
                        State.SetStreaming(false);
                        State.SetError(error);
                        State.AddMessage(failureMessage);
                        contextFailureMessage = failureMessage;
                    }

                    if (contextFailureMessage is not null)
                    {
                        yield return new MessageStartEvent(contextFailureMessage);
                        yield return new MessageEndEvent(contextFailureMessage);
                        yield return new TurnEndEvent(turnIndex, contextFailureMessage, []);
                        yield return new AgentEndEvent(contextFailureMessage.ErrorMessage, State.Messages.ToArray());
                        yield break;
                    }

                    var stream = StreamFunctions.StreamSimple(
                        config.ProviderRegistry,
                        config.Model,
                        context,
                        config.StreamOptions ?? new SimpleStreamOptions());

                    AssistantMessage? assistantMessage = null;
                    var assistantStarted = false;
                    var turnToolResults = new List<ToolResultMessage>();

                    await using var streamEnumerator = stream.GetAsyncEnumerator(token);
                    while (true)
                    {
                        var streamRead = await MoveNextStreamAsync(streamEnumerator, token).ConfigureAwait(false);
                        if (streamRead.Cancelled)
                        {
                            const string error = "Operation canceled.";
                            assistantMessage = CreateFailureMessage(error, StopReason.Aborted, config.Model, State.StreamingMessage);
                            State.SetStreaming(false);
                            State.SetError(error);
                            if (!assistantStarted)
                            {
                                yield return new MessageStartEvent(assistantMessage);
                            }
                            State.AddMessage(assistantMessage);
                            yield return new MessageEndEvent(assistantMessage);
                            yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);
                            yield return new AgentEndEvent(error, State.Messages.ToArray());
                            yield break;
                        }

                        if (!streamRead.HasEvent)
                        {
                            break;
                        }

                        var evt = streamRead.Event!;
                        if (evt is StartEvent start)
                        {
                            assistantStarted = true;
                            State.SetStreaming(true, start.Partial);
                            yield return new MessageStartEvent(start.Partial);
                        }
                        else if (evt is DoneEvent done)
                        {
                            assistantMessage = done.Message;
                            State.SetStreaming(false);
                            if (!assistantStarted)
                            {
                                yield return new MessageStartEvent(done.Message);
                            }
                            State.AddMessage(assistantMessage);
                            yield return new MessageEndEvent(done.Message);
                        }
                        else if (evt is ErrorEvent error)
                        {
                            assistantMessage = (error.Message ?? error.Partial ?? new AssistantMessage()) with
                            {
                                ErrorMessage = error.Error,
                                StopReason = error.Message?.StopReason ?? error.Partial?.StopReason ?? StopReason.Error
                            };
                            State.SetStreaming(false);
                            State.SetError(error.Error);
                            if (!assistantStarted)
                            {
                                yield return new MessageStartEvent(assistantMessage);
                            }
                            State.AddMessage(assistantMessage);
                            yield return new MessageEndEvent(assistantMessage);
                            yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);
                            yield return new AgentEndEvent(error.Error, State.Messages.ToArray());
                            yield break;
                        }
                        else
                        {
                            yield return new MessageUpdateEvent(evt, GetPartialMessage(evt));
                        }
                    }

                    if (assistantMessage is null)
                    {
                        const string error = "Stream ended without a message.";
                        assistantMessage = CreateFailureMessage(error, StopReason.Error, config.Model);
                        State.SetError(error);
                        if (!assistantStarted)
                        {
                            yield return new MessageStartEvent(assistantMessage);
                        }
                        State.AddMessage(assistantMessage);
                        yield return new MessageEndEvent(assistantMessage);
                        yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);
                        yield return new AgentEndEvent(error, State.Messages.ToArray());
                        yield break;
                    }

                    // Extract tool calls
                    var toolCalls = assistantMessage.Content
                        .OfType<ToolCallContent>()
                        .ToList();

                    if (toolCalls.Count > 0)
                    {
                        State.SetPendingToolCalls(toolCalls);
                        try
                        {
                            await foreach (var toolEvt in ToolExecutor.ExecuteToolCallsAsync(
                                toolCalls, config.Tools, config.Interceptors,
                                State.Messages, config.DefaultExecutionMode, config.LogSink, config.LogContext, token))
                            {
                                yield return toolEvt;

                                // Add tool results to conversation
                                if (toolEvt is ToolExecutionEndEvent endEvt)
                                {
                                    var toolResultMessage = new ToolResultMessage(
                                        endEvt.ToolCallId,
                                        endEvt.Result.Content,
                                        endEvt.Result.IsError);
                                    State.AddMessage(toolResultMessage);
                                    turnToolResults.Add(toolResultMessage);
                                    yield return new MessageStartEvent(toolResultMessage);
                                    yield return new MessageEndEvent(toolResultMessage);
                                }
                            }
                        }
                        finally
                        {
                            State.SetPendingToolCalls([]);
                        }

                        hasMoreWork = true;
                    }

                    // Check for new steering messages
                    if (_steeringQueue.Reader.TryPeek(out _))
                        hasMoreWork = true;

                    yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);
                    turnIndex++;

                } while (hasMoreWork && !token.IsCancellationRequested);

            } while (!token.IsCancellationRequested &&
                     DrainQueuedMessages(_followUpQueue, FollowUpMode));

            yield return new AgentEndEvent(messages: State.Messages.ToArray());
        }
        finally
        {
            State.SetStreaming(false);
            _idleTcs.TrySetResult();
        }
    }

    private static EventStream<AgentEvent, ChatMessage[]> CreateEventStream() =>
        new(
            isComplete: static evt => evt is AgentEndEvent,
            extractResult: static evt => evt is AgentEndEvent end ? end.Messages.ToArray() : null);

    private static async Task<StreamReadResult> MoveNextStreamAsync(
        IAsyncEnumerator<StreamEvent> enumerator,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                return new StreamReadResult(true, false, enumerator.Current);
            }

            return new StreamReadResult(false, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StreamReadResult(false, true, null);
        }
    }

    private static AssistantMessage CreateFailureMessage(
        string error,
        StopReason stopReason,
        Model model,
        AssistantMessage? partial = null)
    {
        return (partial ?? new AssistantMessage([new TextContent(string.Empty)])) with
        {
            ErrorMessage = error,
            StopReason = stopReason,
            Usage = partial?.Usage ?? new Usage(0, 0, 0, 0),
            Api = partial?.Api ?? model.Api,
            Provider = partial?.Provider ?? model.Provider,
            Model = partial?.Model ?? model.Id,
            Timestamp = partial?.Timestamp ?? DateTimeOffset.UtcNow
        };
    }

    private bool DrainQueuedMessages(Channel<ChatMessage> channel, AgentQueueMode mode)
    {
        var messages = DrainQueuedMessagesToList(channel, mode);
        if (messages.Count == 0)
        {
            return false;
        }

        foreach (var message in messages)
        {
            State.AddMessage(message);
        }

        return true;
    }

    private static IReadOnlyList<ChatMessage> DrainQueuedMessagesToList(Channel<ChatMessage> channel, AgentQueueMode mode)
    {
        var messages = new List<ChatMessage>();
        if (mode == AgentQueueMode.All)
        {
            while (channel.Reader.TryRead(out var message))
            {
                messages.Add(message);
            }

            return messages;
        }

        if (channel.Reader.TryRead(out var next))
        {
            messages.Add(next);
        }

        return messages;
    }

    private static void DrainChannel<T>(Channel<T> channel)
    {
        while (channel.Reader.TryRead(out _)) { }
    }

    private static ChatMessage? GetPartialMessage(StreamEvent evt) => evt switch
    {
        TextStartEvent text => text.Partial,
        TextDeltaEvent text => text.Partial,
        TextEndEvent text => text.Partial,
        ThinkingStartEvent thinking => thinking.Partial,
        ThinkingDeltaEvent thinking => thinking.Partial,
        ThinkingEndEvent thinking => thinking.Partial,
        ToolCallStartEvent toolCall => toolCall.Partial,
        ToolCallDeltaEvent toolCall => toolCall.Partial,
        ToolCallEndEvent toolCall => toolCall.Partial,
        DoneEvent done => done.Message,
        ErrorEvent error => error.Partial,
        _ => null
    };

    private readonly record struct StreamReadResult(
        bool HasEvent,
        bool Cancelled,
        StreamEvent? Event);
}
