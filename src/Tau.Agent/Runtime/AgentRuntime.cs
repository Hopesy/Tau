using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Tau.Ai;
using Tau.Ai.Providers;

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
                    var context = ContextTransformer.Build(config, State.Messages);
                    var stream = StreamFunctions.StreamSimple(
                        config.ProviderRegistry,
                        config.Model,
                        context,
                        config.StreamOptions ?? new SimpleStreamOptions());

                    AssistantMessage? assistantMessage = null;
                    var assistantStarted = false;

                    await foreach (var evt in stream.WithCancellation(token))
                    {
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
                            State.SetStreaming(false);
                            State.SetError(error.Error);
                            yield return new AgentEndEvent(error.Error);
                            yield break;
                        }
                        else
                        {
                            yield return new MessageUpdateEvent(evt);
                        }
                    }

                    if (assistantMessage is null)
                    {
                        yield return new AgentEndEvent("Stream ended without a message.");
                        yield break;
                    }

                    // Extract tool calls
                    var toolCalls = assistantMessage.Content
                        .OfType<ToolCallContent>()
                        .ToList();

                    if (toolCalls.Count > 0)
                    {
                        State.SetPendingToolCalls(toolCalls);

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
                                yield return new MessageStartEvent(toolResultMessage);
                                yield return new MessageEndEvent(toolResultMessage);
                            }
                        }

                        State.SetPendingToolCalls([]);
                        hasMoreWork = true;
                    }

                    // Check for new steering messages
                    if (_steeringQueue.Reader.TryPeek(out _))
                        hasMoreWork = true;

                    yield return new TurnEndEvent(turnIndex);
                    turnIndex++;

                } while (hasMoreWork && !token.IsCancellationRequested);

            } while (!token.IsCancellationRequested &&
                     DrainQueuedMessages(_followUpQueue, FollowUpMode));

            yield return new AgentEndEvent();
        }
        finally
        {
            State.SetStreaming(false);
            _idleTcs.TrySetResult();
        }
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
}
