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

    public void AddMessage(ChatMessage message) => State.AddMessage(message);
    public void Steer(ChatMessage message) => _steeringQueue.Writer.TryWrite(message);
    public void FollowUp(ChatMessage message) => _followUpQueue.Writer.TryWrite(message);

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

        yield return new AgentStartEvent();

        try
        {
            var turnIndex = 0;

            // Outer loop: follow-up messages
            do
            {
                // Inner loop: tool calls + steering
                bool hasMoreWork;
                do
                {
                    hasMoreWork = false;

                    // Drain steering messages
                    while (_steeringQueue.Reader.TryRead(out var steeringMsg))
                    {
                        State.AddMessage(steeringMsg);
                        hasMoreWork = true;
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

                    await foreach (var evt in stream.WithCancellation(token))
                    {
                        if (evt is StartEvent start)
                        {
                            State.SetStreaming(true, start.Partial);
                            yield return new MessageStartEvent(start.Partial);
                        }
                        else if (evt is DoneEvent done)
                        {
                            assistantMessage = done.Message;
                            State.SetStreaming(false);
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

                    State.AddMessage(assistantMessage);

                    // Extract tool calls
                    var toolCalls = assistantMessage.Content
                        .OfType<ToolCallContent>()
                        .ToList();

                    if (toolCalls.Count > 0)
                    {
                        State.SetPendingToolCalls(toolCalls);

                        await foreach (var toolEvt in ToolExecutor.ExecuteToolCallsAsync(
                            toolCalls, config.Tools, config.Interceptors,
                            State.Messages, config.DefaultExecutionMode, token))
                        {
                            yield return toolEvt;

                            // Add tool results to conversation
                            if (toolEvt is ToolExecutionEndEvent endEvt)
                            {
                                State.AddMessage(new ToolResultMessage(
                                    endEvt.ToolCallId,
                                    endEvt.Result.Content,
                                    endEvt.Result.IsError));
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

            } while (_followUpQueue.Reader.TryRead(out var followUp) &&
                     !token.IsCancellationRequested &&
                     TryInjectFollowUp(followUp));

            yield return new AgentEndEvent();
        }
        finally
        {
            State.SetStreaming(false);
            _idleTcs.TrySetResult();
        }
    }

    private bool TryInjectFollowUp(ChatMessage message)
    {
        State.AddMessage(message);
        return true;
    }

    private static void DrainChannel<T>(Channel<T> channel)
    {
        while (channel.Reader.TryRead(out _)) { }
    }
}
