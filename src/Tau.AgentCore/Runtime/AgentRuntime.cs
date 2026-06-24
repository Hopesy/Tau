using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Runtime;

/// <summary>
/// Agent runtime with double-loop execution:
///   Outer: follow-up messages
///     Inner: tool calls + steering messages
/// Mirrors pi-main's Agent + agent-loop.ts.
/// </summary>
public sealed class AgentRuntime
{
    private readonly Channel<ChatMessage> _steeringQueue = Channel.CreateUnbounded<ChatMessage>();
    private readonly Channel<ChatMessage> _followUpQueue = Channel.CreateUnbounded<ChatMessage>();
    private CancellationTokenSource? _runCts;
    private readonly TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingSteeringMessageCount;
    private int _pendingFollowUpMessageCount;

    public AgentState State { get; } = new();
    public AgentQueueMode SteeringMode { get; set; } = AgentQueueMode.OneAtATime;
    public AgentQueueMode FollowUpMode { get; set; } = AgentQueueMode.OneAtATime;
    public bool HasQueuedMessages => _steeringQueue.Reader.TryPeek(out _) || _followUpQueue.Reader.TryPeek(out _);
    public int PendingMessageCount =>
        Volatile.Read(ref _pendingSteeringMessageCount) + Volatile.Read(ref _pendingFollowUpMessageCount);

    public void AddMessage(ChatMessage message) => State.AddMessage(message);
    public void Steer(ChatMessage message)
    {
        if (_steeringQueue.Writer.TryWrite(message))
        {
            Interlocked.Increment(ref _pendingSteeringMessageCount);
        }
    }

    public void FollowUp(ChatMessage message)
    {
        if (_followUpQueue.Writer.TryWrite(message))
        {
            Interlocked.Increment(ref _pendingFollowUpMessageCount);
        }
    }

    public void ClearSteeringQueue() => DrainChannel(_steeringQueue, ref _pendingSteeringMessageCount);
    public void ClearFollowUpQueue() => DrainChannel(_followUpQueue, ref _pendingFollowUpMessageCount);
    public IReadOnlyList<ChatMessage> DrainSteeringMessages() =>
        DrainQueuedMessagesToList(_steeringQueue, SteeringMode, ref _pendingSteeringMessageCount);
    public IReadOnlyList<ChatMessage> DrainFollowUpMessages() =>
        DrainQueuedMessagesToList(_followUpQueue, FollowUpMode, ref _pendingFollowUpMessageCount);

    public void Abort() => _runCts?.Cancel();
    public Task WaitForIdleAsync() => _idleTcs.Task;

    public void Reset()
    {
        State.Reset();
        ClearSteeringQueue();
        ClearFollowUpQueue();
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
        var currentConfig = config;
        State.Configure(currentConfig.SystemPrompt, currentConfig.Model, currentConfig.Tools);

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
                        DrainQueuedMessages(_steeringQueue, SteeringMode, ref _pendingSteeringMessageCount);
                    }

                    yield return new TurnStartEvent(turnIndex);

                    // Build context and stream LLM response
                    LlmContext context = default;
                    AssistantMessage? contextFailureMessage = null;
                    try
                    {
                        context = await ContextTransformer.BuildAsync(currentConfig, State.Messages, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        const string error = "Operation canceled.";
                        var failureMessage = CreateFailureMessage(error, StopReason.Aborted, currentConfig.Model);
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

                    var streamOptions = currentConfig.StreamOptions ?? new SimpleStreamOptions();
                    var providerRunStartedAt = LogProviderRunStart(currentConfig, context, streamOptions);
                    var providerRunEnded = false;
                    void LogProviderEnd(
                        bool success,
                        string failureKind,
                        AssistantMessage? message,
                        string? exceptionType = null)
                    {
                        if (providerRunEnded)
                        {
                            return;
                        }

                        providerRunEnded = true;
                        LogProviderRunEnd(
                            currentConfig,
                            context,
                            streamOptions,
                            providerRunStartedAt,
                            success,
                            failureKind,
                            message,
                            exceptionType);
                    }

                    var stream = StartProviderStream(
                        currentConfig.ProviderRegistry,
                        currentConfig.Model,
                        context,
                        streamOptions,
                        ex => LogProviderEnd(false, "exception", State.StreamingMessage, ex.GetType().Name));

                    AssistantMessage? assistantMessage = null;
                    var assistantStarted = false;
                    var turnToolResults = new List<ToolResultMessage>();

                    await using var streamEnumerator = stream.GetAsyncEnumerator(token);
                    while (true)
                    {
                        var streamRead = await MoveNextStreamAsync(
                            streamEnumerator,
                            token,
                            ex => LogProviderEnd(false, "exception", State.StreamingMessage, ex.GetType().Name))
                            .ConfigureAwait(false);
                        if (streamRead.Cancelled)
                        {
                            const string error = "Operation canceled.";
                            assistantMessage = CreateFailureMessage(error, StopReason.Aborted, currentConfig.Model, State.StreamingMessage);
                            LogProviderEnd(false, "cancelled", assistantMessage);
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
                            assistantMessage = EnrichAssistantMessage(done.Message, currentConfig.Model);
                            var success = IsSuccessfulProviderMessage(assistantMessage);
                            LogProviderEnd(success, success ? "none" : GetProviderFailureKind(assistantMessage), assistantMessage);
                            State.SetStreaming(false);
                            if (!assistantStarted)
                            {
                                yield return new MessageStartEvent(assistantMessage);
                            }
                            State.AddMessage(assistantMessage);
                            yield return new MessageEndEvent(assistantMessage);
                        }
                        else if (evt is ErrorEvent error)
                        {
                            assistantMessage = (error.Message ?? error.Partial ?? new AssistantMessage()) with
                            {
                                ErrorMessage = error.Error,
                                StopReason = error.Message?.StopReason ?? error.Partial?.StopReason ?? StopReason.Error
                            };
                            assistantMessage = EnrichAssistantMessage(assistantMessage, currentConfig.Model);
                            LogProviderEnd(false, "stream-error", assistantMessage);
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
                        assistantMessage = CreateFailureMessage(error, StopReason.Error, currentConfig.Model);
                        LogProviderEnd(false, "stream-ended-without-message", assistantMessage);
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
                        var toolEndResults = new List<ToolResult>();
                        try
                        {
                            await foreach (var toolEvt in ToolExecutor.ExecuteToolCallsAsync(
                                toolCalls, currentConfig.Tools, currentConfig.Interceptors,
                                State.Messages, currentConfig.DefaultExecutionMode, currentConfig.LogSink, currentConfig.LogContext, token))
                            {
                                yield return toolEvt;

                                // Add tool results to conversation
                                if (toolEvt is ToolExecutionEndEvent endEvt)
                                {
                                    toolEndResults.Add(endEvt.Result);
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

                        var terminatedByTools = toolEndResults.Count > 0 &&
                            toolEndResults.All(static result => result.Terminate);
                        hasMoreWork = !terminatedByTools;
                    }

                    yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);

                    var turnContext = CreateTurnContext(assistantMessage, turnToolResults);
                    if (currentConfig.PrepareNextTurnAsync is not null)
                    {
                        var update = await currentConfig.PrepareNextTurnAsync(turnContext, token).ConfigureAwait(false);
                        if (update is not null)
                        {
                            currentConfig = ApplyTurnUpdate(currentConfig, update);
                            if (update.Context is not null)
                            {
                                State.SetMessages(update.Context.ToList());
                            }

                            State.Configure(currentConfig.SystemPrompt, currentConfig.Model, currentConfig.Tools);
                        }
                    }

                    if (currentConfig.ShouldStopAfterTurnAsync is not null &&
                        await currentConfig.ShouldStopAfterTurnAsync(CreateTurnContext(assistantMessage, turnToolResults), token).ConfigureAwait(false))
                    {
                        yield return new AgentEndEvent(messages: State.Messages.ToArray());
                        yield break;
                    }

                    // Check for new steering messages after turn hooks have had a chance to stop.
                    if (_steeringQueue.Reader.TryPeek(out _))
                        hasMoreWork = true;

                    turnIndex++;

                } while (hasMoreWork && !token.IsCancellationRequested);

            } while (!token.IsCancellationRequested &&
                     DrainQueuedMessages(_followUpQueue, FollowUpMode, ref _pendingFollowUpMessageCount));

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

    private AgentLoopTurnContext CreateTurnContext(
        AssistantMessage message,
        IReadOnlyList<ToolResultMessage> toolResults) =>
        new(
            message,
            toolResults.ToArray(),
            State.Messages.ToArray(),
            State.Messages.ToArray());

    private static AgentLoopConfig ApplyTurnUpdate(AgentLoopConfig config, AgentLoopTurnUpdate update)
    {
        var streamOptions = update.StreamOptions ?? config.StreamOptions;
        if (update.ClearReasoning && streamOptions is not null)
        {
            streamOptions = streamOptions with { Reasoning = null };
        }

        if (update.Reasoning is { } reasoning)
        {
            streamOptions = (streamOptions ?? new SimpleStreamOptions()) with { Reasoning = reasoning };
        }

        return config with
        {
            Model = update.Model ?? config.Model,
            SystemPrompt = update.SystemPrompt ?? config.SystemPrompt,
            Tools = update.Tools ?? config.Tools,
            StreamOptions = streamOptions
        };
    }

    private static async Task<StreamReadResult> MoveNextStreamAsync(
        IAsyncEnumerator<StreamEvent> enumerator,
        CancellationToken cancellationToken,
        Action<Exception>? onException = null)
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
        catch (Exception ex)
        {
            onException?.Invoke(ex);
            throw;
        }
    }

    private static long LogProviderRunStart(
        AgentLoopConfig config,
        LlmContext context,
        SimpleStreamOptions streamOptions)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var fields = CreateProviderRunFields(config, context, streamOptions);
        config.LogContext?.AddTo(fields);
        config.LogSink.Log(new TauLogEvent("provider", "run.start", DateTimeOffset.UtcNow, fields));
        return startedAt;
    }

    private static AssistantMessageStream StartProviderStream(
        ProviderRegistry providerRegistry,
        Model model,
        LlmContext context,
        SimpleStreamOptions streamOptions,
        Action<Exception> onException)
    {
        try
        {
            return StreamFunctions.StreamSimple(providerRegistry, model, context, streamOptions);
        }
        catch (Exception ex)
        {
            onException(ex);
            throw;
        }
    }

    private static void LogProviderRunEnd(
        AgentLoopConfig config,
        LlmContext context,
        SimpleStreamOptions streamOptions,
        long startedAt,
        bool success,
        string failureKind,
        AssistantMessage? message,
        string? exceptionType = null)
    {
        var fields = CreateProviderRunFields(config, context, streamOptions);
        fields["success"] = success ? "true" : "false";
        fields["failureKind"] = failureKind;
        fields["durationMs"] = Stopwatch.GetElapsedTime(startedAt)
            .TotalMilliseconds
            .ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        if (message?.StopReason is { } stopReason)
        {
            fields["stopReason"] = FormatEnum(stopReason);
        }

        if (message?.Usage is { } usage)
        {
            AddUsageFields(fields, config.Model, usage);
        }

        if (!string.IsNullOrWhiteSpace(exceptionType))
        {
            fields["exceptionType"] = exceptionType.Trim();
        }

        config.LogContext?.AddTo(fields);
        config.LogSink.Log(new TauLogEvent("provider", "run.end", DateTimeOffset.UtcNow, fields));
    }

    private static Dictionary<string, string?> CreateProviderRunFields(
        AgentLoopConfig config,
        LlmContext context,
        SimpleStreamOptions streamOptions)
    {
        var fields = new Dictionary<string, string?>
        {
            ["provider"] = config.Model.Provider,
            ["model"] = config.Model.Id,
            ["api"] = config.Model.Api,
            ["messageCount"] = context.Messages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["toolCount"] = (context.Tools?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["transport"] = FormatEnum(streamOptions.Transport),
            ["cacheRetention"] = FormatEnum(streamOptions.CacheRetention)
        };

        AddOptionalField(fields, "providerSessionId", streamOptions.SessionId);
        if (streamOptions.Reasoning is { } reasoning)
        {
            fields["reasoning"] = FormatEnum(reasoning);
        }

        return fields;
    }

    private static void AddUsageFields(
        IDictionary<string, string?> fields,
        Model model,
        Usage usage)
    {
        fields["inputTokens"] = usage.InputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        fields["outputTokens"] = usage.OutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AddOptionalInt(fields, "cacheReadTokens", usage.CacheReadTokens);
        AddOptionalInt(fields, "cacheWriteTokens", usage.CacheWriteTokens);
        AddOptionalField(fields, "serviceTier", usage.ServiceTier);

        UsageCost? cost = usage.Cost;
        if (cost is null && model.Cost is not null)
        {
            cost = ModelCatalog.CalculateCost(model, usage);
        }

        if (cost is { } resolvedCost)
        {
            fields["inputCost"] = resolvedCost.Input.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["outputCost"] = resolvedCost.Output.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["cacheReadCost"] = resolvedCost.CacheRead.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["cacheWriteCost"] = resolvedCost.CacheWrite.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["totalCost"] = resolvedCost.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static void AddOptionalInt(IDictionary<string, string?> fields, string name, int? value)
    {
        if (value.HasValue)
        {
            fields[name] = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static void AddOptionalField(IDictionary<string, string?> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[name] = value.Trim();
        }
    }

    private static bool IsSuccessfulProviderMessage(AssistantMessage message) =>
        string.IsNullOrWhiteSpace(message.ErrorMessage) &&
        message.StopReason is not StopReason.Error and not StopReason.Aborted;

    private static string GetProviderFailureKind(AssistantMessage message) =>
        message.StopReason == StopReason.Aborted ? "cancelled" : "error";

    private static string FormatEnum<T>(T value) where T : struct, Enum =>
        value.ToString().ToLowerInvariant();

    private static AssistantMessage EnrichAssistantMessage(AssistantMessage message, Model model) =>
        message with
        {
            Api = string.IsNullOrWhiteSpace(message.Api) ? model.Api : message.Api,
            Provider = string.IsNullOrWhiteSpace(message.Provider) ? model.Provider : message.Provider,
            Model = string.IsNullOrWhiteSpace(message.Model) ? model.Id : message.Model,
            Timestamp = message.Timestamp ?? DateTimeOffset.UtcNow,
            Usage = EnrichUsageCost(message.Usage, model)
        };

    private static Usage? EnrichUsageCost(Usage? usage, Model model)
    {
        if (usage is not { } value ||
            value.Cost is not null ||
            model.Cost is null)
        {
            return usage;
        }

        return value with { Cost = ModelCatalog.CalculateCost(model, value) };
    }

    private static AssistantMessage CreateFailureMessage(
        string error,
        StopReason stopReason,
        Model model,
        AssistantMessage? partial = null)
    {
        var message = (partial ?? new AssistantMessage([new TextContent(string.Empty)])) with
        {
            ErrorMessage = error,
            StopReason = stopReason,
            Usage = partial?.Usage ?? new Usage(0, 0, 0, 0)
        };
        return EnrichAssistantMessage(message, model);
    }

    private bool DrainQueuedMessages(Channel<ChatMessage> channel, AgentQueueMode mode, ref int pendingCount)
    {
        var messages = DrainQueuedMessagesToList(channel, mode, ref pendingCount);
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

    private static IReadOnlyList<ChatMessage> DrainQueuedMessagesToList(
        Channel<ChatMessage> channel,
        AgentQueueMode mode,
        ref int pendingCount)
    {
        var messages = new List<ChatMessage>();
        if (mode == AgentQueueMode.All)
        {
            while (channel.Reader.TryRead(out var message))
            {
                messages.Add(message);
                Interlocked.Decrement(ref pendingCount);
            }

            return messages;
        }

        if (channel.Reader.TryRead(out var next))
        {
            messages.Add(next);
            Interlocked.Decrement(ref pendingCount);
        }

        return messages;
    }

    private static void DrainChannel(Channel<ChatMessage> channel, ref int pendingCount)
    {
        while (channel.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref pendingCount);
        }
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
