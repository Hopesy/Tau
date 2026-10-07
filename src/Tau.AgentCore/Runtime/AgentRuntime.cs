using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private readonly object _queueGate = new();
    private readonly Queue<ChatMessage> _steeringQueue = new();
    private readonly Queue<ChatMessage> _followUpQueue = new();
    private CancellationTokenSource? _runCts;
    private readonly object _idleGate = new();
    private TaskCompletionSource _idleTcs = CompletedIdleSource();

    public AgentState State { get; } = new();
    public AgentQueueMode SteeringMode { get; set; } = AgentQueueMode.OneAtATime;
    public AgentQueueMode FollowUpMode { get; set; } = AgentQueueMode.OneAtATime;
    public bool HasQueuedMessages => PendingMessageCount > 0;
    public int PendingMessageCount
    {
        get { lock (_queueGate) return _steeringQueue.Count + _followUpQueue.Count; }
    }

    public void AddMessage(ChatMessage message) => State.AddMessage(message);

    /// <summary>【AgentCore】【会话基线】为直接运行时入口补齐开场声明，已有系统开场时以历史为准。</summary>
    /// <param name="systemPrompt">缺少开场声明时使用的系统提示。</param>
    /// <param name="model">当前执行模型。</param>
    /// <param name="tools">当前执行工具；声明保存为独立快照。</param>
    public void InitializeTranscript(string? systemPrompt, Model model, IReadOnlyList<IAgentTool> tools)
    {
        State.Configure(null, model, tools);
        if (State.Messages.FirstOrDefault() is SystemMessage) return;
        var initial = Transcript.CreateInitialSystemMessage(systemPrompt, tools.Select(tool =>
            new Tool(tool.Name, tool.Description, tool.ParameterSchema) { ConstrainedSampling = tool.ConstrainedSampling }).ToArray());
        if (initial is not null) State.SetMessages([initial, .. State.Messages]);
    }

    /// <summary>【AgentCore】【结构化基线】为新会话保留命名段落，并声明当前可执行工具；已有开场声明时保留历史。</summary>
    /// <param name="initial">包含系统文本或命名段落的开场消息。</param>
    /// <param name="model">当前模型。</param>
    /// <param name="tools">当前可执行工具。</param>
    public void InitializeStructuredTranscript(SystemMessage initial, Model model, IReadOnlyList<IAgentTool> tools)
    {
        State.Configure(null, model, tools);
        if (State.Messages.FirstOrDefault() is SystemMessage) return;
        var declarations = tools.Select(tool => new Tool(tool.Name, tool.Description, tool.ParameterSchema)
            { ConstrainedSampling = tool.ConstrainedSampling }).ToArray();
        State.SetMessages([initial with { ToolsAdded = declarations.Length == 0 ? null : declarations }, .. State.Messages]);
    }

    /// <summary>【AgentCore】【提示替换】替换会话的完整系统提示，同时保留工具声明与待执行队列。</summary>
    /// <param name="systemPrompt">完整提示；null 表示清空提示。</param>
    public void ReplaceSystemPrompt(string? systemPrompt) => State.ReplaceSystemPrompt(systemPrompt);

    /// <summary>【AgentCore】【队列调度】追加将在下一回合优先选取的引导消息</summary>
    /// <param name="message">待发送的引导消息</param>
    public void Steer(ChatMessage message)
    {
        lock (_queueGate) _steeringQueue.Enqueue(message);
    }

    /// <summary>【AgentCore】【队列调度】追加在引导队列消费完后选取的后续消息</summary>
    /// <param name="message">待发送的后续消息</param>
    public void FollowUp(ChatMessage message)
    {
        lock (_queueGate) _followUpQueue.Enqueue(message);
    }

    /// <summary>【AgentCore】【队列预览】按当前模式预览下一回合选中的消息，不消费队列；引导消息优先</summary>
    /// <returns>独立的消息数组；消息对象与队列中的对象保持一致</returns>
    public IReadOnlyList<ChatMessage> PeekQueuedMessages()
    {
        lock (_queueGate)
        {
            var queue = _steeringQueue.Count > 0 ? _steeringQueue : _followUpQueue;
            var mode = _steeringQueue.Count > 0 ? SteeringMode : FollowUpMode;
            return mode == AgentQueueMode.All ? queue.ToArray() : queue.TryPeek(out var first) ? [first] : [];
        }
    }

    /// <summary>【AgentCore】【队列调度】清空尚未选取的引导消息</summary>
    public void ClearSteeringQueue() { lock (_queueGate) _steeringQueue.Clear(); }

    /// <summary>【AgentCore】【队列调度】清空尚未选取的后续消息</summary>
    public void ClearFollowUpQueue() { lock (_queueGate) _followUpQueue.Clear(); }

    /// <summary>【AgentCore】【队列清空】在同一个同步边界清除全部等待消息，不受逐回合消费模式影响。</summary>
    public void ClearAllQueues()
    {
        lock (_queueGate) { _steeringQueue.Clear(); _followUpQueue.Clear(); }
    }

    /// <summary>【AgentCore】【完整队列快照】读取全部等待消息，不消费条目，也不套用单回合选择模式。</summary>
    /// <returns>引导与跟进队列的独立数组。</returns>
    public (IReadOnlyList<ChatMessage> Steering, IReadOnlyList<ChatMessage> FollowUp) GetQueuedMessages()
    {
        lock (_queueGate) return (_steeringQueue.ToArray(), _followUpQueue.ToArray());
    }

    /// <summary>【AgentCore】【队列提取】原子提取并清除全部等待消息，用于用户取消后的输入恢复。</summary>
    /// <returns>引导与跟进队列的独立数组；保留消息对象和各队列原始顺序。</returns>
    public (IReadOnlyList<ChatMessage> Steering, IReadOnlyList<ChatMessage> FollowUp) DrainAllQueuedMessages()
    {
        lock (_queueGate)
        {
            var steering = _steeringQueue.ToArray(); var followUp = _followUpQueue.ToArray();
            _steeringQueue.Clear(); _followUpQueue.Clear();
            return (steering, followUp);
        }
    }

    /// <summary>【AgentCore】【队列调度】按当前引导模式选取并移除消息</summary>
    /// <returns>本次选中的引导消息</returns>
    public IReadOnlyList<ChatMessage> DrainSteeringMessages() =>
        DrainQueuedMessagesToList(_steeringQueue, SteeringMode);

    /// <summary>【AgentCore】【队列调度】按当前后续模式选取并移除消息</summary>
    /// <returns>本次选中的后续消息</returns>
    public IReadOnlyList<ChatMessage> DrainFollowUpMessages() =>
        DrainQueuedMessagesToList(_followUpQueue, FollowUpMode);

    public void Abort() => _runCts?.Cancel();
    public Task WaitForIdleAsync()
    {
        lock (_idleGate)
        {
            return _idleTcs.Task;
        }
    }

    /// <summary>【AgentCore】【运行重置】清除会话状态并一次性清空两类输入队列。</summary>
    public void Reset()
    {
        State.Reset();
        ClearAllQueues();
    }

    /// <summary>【AgentCore】【上下文替换】替换已完成的会话消息，保留待处理的引导与后续输入。</summary>
    /// <param name="messages">新的上下文消息；保存为独立列表。</param>
    public void ReplaceMessages(IEnumerable<ChatMessage> messages) => State.SetMessages(messages.ToList());

    public EventStream<AgentEvent, ChatMessage[]> RunStream(
        AgentLoopConfig config,
        CancellationToken ct = default)
    {
        BeginIdleWait();
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

    /// <summary>【AgentCore】【运行循环】执行模型回合和工具批次，并维护完整消息生命周期</summary>
    /// <param name="config">模型、工具、上下文转换及生命周期配置</param>
    /// <param name="ct">运行取消信号</param>
    /// <returns>模型、工具、回合及运行结束事件</returns>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentLoopConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        BeginIdleWait();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _runCts.Token;
        var currentConfig = config with { StreamFunction = config.StreamFunction ?? AgentStreaming.GetDefaultStreamFunction() };
        State.Configure(currentConfig.SystemPrompt, currentConfig.Model, currentConfig.Tools);

        yield return new AgentStartEvent();

        try
        {
            var turnIndex = 0;
            var skipInitialSteeringPoll = config.SkipInitialSteeringPoll;
            IReadOnlyList<ChatMessage> pendingQueuedMessages = [];
            AgentLoopTurnContext? lastCompletedTurn = null;
            var newMessages = new List<ChatMessage>();
            var explicitContinuation = false;

            // Outer loop: follow-up messages
            do
            {
                // Inner loop: tool calls + steering
                bool hasMoreWork;
                do
                {
                    hasMoreWork = false;
                    IReadOnlyList<ChatMessage> preparedMessages = [];

                    // 1. 【AgentCore】【回合准备】仅在已确定进入下一回合时执行准备钩子
                    if (lastCompletedTurn is not null)
                    {
                        if (currentConfig.PrepareNextTurnAsync is not null)
                        {
                            var update = await currentConfig.PrepareNextTurnAsync(lastCompletedTurn, token).ConfigureAwait(false);
                            if (update is not null)
                            {
                                currentConfig = ApplyRequestUpdate(currentConfig, update);
                                preparedMessages = update.Messages;
                            }
                        }

                        lastCompletedTurn = null;
                    }

                    if (skipInitialSteeringPoll)
                    {
                        skipInitialSteeringPoll = false;
                    }
                    else if (pendingQueuedMessages.Count > 0)
                    {
                        // 2. 【AgentCore】【队列调度】已选中的队列消息直接消费，保持逐条模式
                    }
                    else
                    {
                        pendingQueuedMessages = DrainQueuedMessagesToList(
                            _steeringQueue,
                            SteeringMode);
                    }

                    yield return new TurnStartEvent(turnIndex);

                    // 3. 【AgentCore】【工具声明】首次提示先发布，再处理已选队列消息
                    if (turnIndex == 0 && config.InitialMessages.Count > 0)
                    {
                        foreach (var message in ToolDeclarations.Declare(State.Messages, config.InitialMessages, currentConfig.Tools))
                        {
                            yield return new MessageStartEvent(message);
                            var finalized = await TransformMessageEndAsync(currentConfig, message, token).ConfigureAwait(false);
                            State.AddMessage(finalized);
                            newMessages.Add(finalized);
                            yield return new MessageEndEvent(finalized);
                        }
                    }

                    foreach (var message in ToolDeclarations.Declare(State.Messages, [.. preparedMessages, .. pendingQueuedMessages], currentConfig.Tools))
                    {
                        yield return new MessageStartEvent(message);
                        var finalizedMessage = await TransformMessageEndAsync(currentConfig, message, token).ConfigureAwait(false);
                        yield return new MessageEndEvent(finalizedMessage);
                        State.AddMessage(finalizedMessage);
                        newMessages.Add(finalizedMessage);
                    }

                    pendingQueuedMessages = [];

                    // 3. 【AgentCore】【请求准备】当前输入发布后替换请求状态，此后不再轮询队列
                    if (currentConfig.PrepareRequestAsync is not null)
                    {
                        var request = new AgentPrepareRequestContext(
                            State.Messages.ToArray(), currentConfig.Model,
                            currentConfig.StreamOptions?.Reasoning ?? ThinkingLevel.Off,
                            State.SystemPrompt, currentConfig.Tools.ToArray());
                        var update = await currentConfig.PrepareRequestAsync(request, token).ConfigureAwait(false);
                        if (update is not null) currentConfig = ApplyRequestUpdate(currentConfig, update);

                        // 4. 【AgentCore】【请求准备】保留 Tau 的 Tools 更新接口，替换上下文后重新声明实际工具
                        if (update?.Tools is not null)
                        {
                            foreach (var message in ToolDeclarations.Declare(State.Messages, [], currentConfig.Tools))
                            {
                                yield return new MessageStartEvent(message);
                                State.AddMessage(message);
                                newMessages.Add(message);
                                yield return new MessageEndEvent(message);
                            }
                        }
                    }

                    // 4. 【AgentCore】【模型请求】准备完成后再转换上下文和解析模型凭据
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
                        contextFailureMessage = failureMessage;
                    }

                    if (contextFailureMessage is not null)
                    {
                        yield return new MessageStartEvent(contextFailureMessage);
                        contextFailureMessage = (AssistantMessage)await TransformMessageEndAsync(
                            currentConfig,
                            contextFailureMessage,
                            token).ConfigureAwait(false);
                        State.AddMessage(contextFailureMessage);
                        newMessages.Add(contextFailureMessage);
                        yield return new MessageEndEvent(contextFailureMessage);
                        await FinishTurnAsync(currentConfig,
                            CreateTurnContext(contextFailureMessage, [], currentConfig, newMessages), token).ConfigureAwait(false);
                        yield return new TurnEndEvent(turnIndex, contextFailureMessage, []);
                        yield return new AgentEndEvent(contextFailureMessage.ErrorMessage, newMessages.ToArray());
                        yield break;
                    }

                    var streamOptions = await ResolveStreamOptionsAsync(currentConfig, token).ConfigureAwait(false);
                    // 1. 【AgentCore】【请求取消】运行取消必须到达提供方，同时保留调用方单独配置的取消信号
                    using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, streamOptions.Signal);
                    streamOptions = streamOptions with { Signal = providerCancellation.Token };
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

                    var stream = await StartProviderStreamAsync(
                        currentConfig,
                        context,
                        streamOptions,
                        ex => LogProviderEnd(false, "exception", State.StreamingMessage, ex.GetType().Name)).ConfigureAwait(false);

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
                            assistantMessage = (AssistantMessage)await TransformMessageEndAsync(
                                currentConfig,
                                RecordThinkingLevel(currentConfig, assistantMessage),
                                token).ConfigureAwait(false);
                            State.AddMessage(assistantMessage);
                            newMessages.Add(assistantMessage);
                            yield return new MessageEndEvent(assistantMessage);
                            await FinishTurnAsync(currentConfig,
                                CreateTurnContext(assistantMessage, turnToolResults, currentConfig, newMessages), token).ConfigureAwait(false);
                            yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);
                            yield return new AgentEndEvent(error, newMessages.ToArray());
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
                            assistantMessage = (AssistantMessage)await TransformMessageEndAsync(
                                currentConfig,
                                RecordThinkingLevel(currentConfig, assistantMessage),
                                token).ConfigureAwait(false);
                            State.AddMessage(assistantMessage);
                            newMessages.Add(assistantMessage);
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
                            assistantMessage = (AssistantMessage)await TransformMessageEndAsync(
                                currentConfig,
                                RecordThinkingLevel(currentConfig, assistantMessage),
                                token).ConfigureAwait(false);
                            State.AddMessage(assistantMessage);
                            newMessages.Add(assistantMessage);
                            yield return new MessageEndEvent(assistantMessage);
                            await FinishTurnAsync(currentConfig,
                                CreateTurnContext(assistantMessage, turnToolResults, currentConfig, newMessages), token).ConfigureAwait(false);
                            yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);
                            yield return new AgentEndEvent(error.Error, newMessages.ToArray());
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
                        assistantMessage = (AssistantMessage)await TransformMessageEndAsync(
                            currentConfig,
                            RecordThinkingLevel(currentConfig, assistantMessage),
                            token).ConfigureAwait(false);
                        State.AddMessage(assistantMessage);
                        newMessages.Add(assistantMessage);
                        yield return new MessageEndEvent(assistantMessage);
                        await FinishTurnAsync(currentConfig,
                            CreateTurnContext(assistantMessage, turnToolResults, currentConfig, newMessages), token).ConfigureAwait(false);
                        yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);
                        yield return new AgentEndEvent(error, newMessages.ToArray());
                        yield break;
                    }

                    // 5. 【AgentCore】【回合完成】错误和中止响应强制结束，钩子决策不能触发工具或下一请求
                    if (assistantMessage.StopReason is StopReason.Error or StopReason.Aborted)
                    {
                        State.SetError(assistantMessage.ErrorMessage);
                        await FinishTurnAsync(currentConfig,
                            CreateTurnContext(assistantMessage, [], currentConfig, newMessages), token).ConfigureAwait(false);
                        yield return new TurnEndEvent(turnIndex, assistantMessage, []);
                        yield return new AgentEndEvent(assistantMessage.ErrorMessage, newMessages.ToArray());
                        yield break;
                    }

                    // 6. 【AgentCore】【工具执行】执行正常响应的工具批次并收集最终工具消息
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
                                State.Messages, currentConfig.DefaultExecutionMode, currentConfig.LogSink, currentConfig.LogContext, token,
                                responseTruncated: assistantMessage.StopReason == StopReason.MaxTokens))
                            {
                                yield return toolEvt;

                                // 1. 【AgentCore】【工具结果】将完整结果元数据写入事件、历史及下一轮上下文
                                if (toolEvt is ToolExecutionEndEvent endEvt)
                                {
                                    toolEndResults.Add(endEvt.Result);
                                    var toolResultMessage = new ToolResultMessage(
                                        endEvt.ToolCallId,
                                        endEvt.Result.Content,
                                        endEvt.IsError)
                                    {
                                        ToolName = endEvt.ToolName,
                                        Details = endEvt.Result.Details,
                                        Usage = endEvt.Result.Usage,
                                        Timestamp = DateTimeOffset.UtcNow
                                    };
                                    yield return new MessageStartEvent(toolResultMessage);
                                    toolResultMessage = (ToolResultMessage)await TransformMessageEndAsync(
                                        currentConfig,
                                        toolResultMessage,
                                        token).ConfigureAwait(false);
                                    State.AddMessage(toolResultMessage);
                                    newMessages.Add(toolResultMessage);
                                    turnToolResults.Add(toolResultMessage);
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

                    // 7. 【AgentCore】【回合完成】钩子观察最终消息，在 turn_end 发布之后应用决策
                    var turnContext = CreateTurnContext(assistantMessage, turnToolResults, currentConfig, newMessages);
                    var decision = await FinishTurnAsync(currentConfig, turnContext, token).ConfigureAwait(false);
                    yield return new TurnEndEvent(turnIndex, assistantMessage, turnToolResults);

                    if (decision == AgentTurnDecision.End ||
                        (currentConfig.FinishTurnAsync is null && currentConfig.ShouldStopAfterTurnAsync is not null &&
                         await currentConfig.ShouldStopAfterTurnAsync(turnContext, token).ConfigureAwait(false)))
                    {
                        yield return new AgentEndEvent(messages: newMessages.ToArray());
                        yield break;
                    }

                    // 8. 【AgentCore】【队列调度】先选定 steering，避免准备期间新增输入抢占已选消息
                    pendingQueuedMessages = token.IsCancellationRequested
                        ? []
                        : DrainQueuedMessagesToList(_steeringQueue, SteeringMode);
                    hasMoreWork |= pendingQueuedMessages.Count > 0;
                    explicitContinuation = decision == AgentTurnDecision.Continue && !hasMoreWork;
                    lastCompletedTurn = turnContext;

                    turnIndex++;

                } while (hasMoreWork && !token.IsCancellationRequested);

                pendingQueuedMessages = token.IsCancellationRequested
                    ? []
                    : DrainQueuedMessagesToList(_followUpQueue, FollowUpMode);
                if (pendingQueuedMessages.Count > 0) explicitContinuation = false;

            } while (!token.IsCancellationRequested && (pendingQueuedMessages.Count > 0 || explicitContinuation));

            yield return new AgentEndEvent(messages: newMessages.ToArray());
        }
        finally
        {
            State.SetStreaming(false);
            lock (_idleGate)
            {
                _idleTcs.TrySetResult();
            }
        }
    }

    private void BeginIdleWait()
    {
        lock (_idleGate)
        {
            if (_idleTcs.Task.IsCompleted)
            {
                _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private static TaskCompletionSource CompletedIdleSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult();
        return source;
    }

    private static EventStream<AgentEvent, ChatMessage[]> CreateEventStream() =>
        new(
            isComplete: static evt => evt is AgentEndEvent,
            extractResult: static evt => evt is AgentEndEvent end ? end.Messages.ToArray() : null);

    /// <summary>
    /// 执行 message_end 转换钩子，并拒绝 role 不一致或类型不兼容的替换结果。
    /// </summary>
    /// <param name="config">当前 agent 循环配置。</param>
    /// <param name="message">已经完成、准备写入会话状态的消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>转换后的最终消息；无转换或转换非法时返回原消息。</returns>
    private static async Task<ChatMessage> TransformMessageEndAsync(
        AgentLoopConfig config,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        if (config.MessageEndTransformAsync is null)
        {
            return message;
        }

        var replacement = await config.MessageEndTransformAsync(message, cancellationToken).ConfigureAwait(false);
        return IsCompatibleMessageReplacement(message, replacement) ? replacement : message;
    }

    /// <summary>【AgentCore】【请求记录】在完成钩子之前记录实际请求的推理等级，不修改输入的历史消息。</summary>
    /// <param name="config">实际请求配置。</param><param name="message">提供方完成或失败的响应。</param><returns>包含请求等级的响应。</returns>
    private static AssistantMessage RecordThinkingLevel(AgentLoopConfig config, AssistantMessage message) => message with
    {
        ThinkingLevel = config.StreamOptions?.Reasoning switch
        {
            null or ThinkingLevel.Off => "off",
            ThinkingLevel.ExtraHigh => "xhigh",
            var level => level.ToString()!.ToLowerInvariant()
        }
    };

    /// <summary>
    /// 判断 message_end 替换结果是否可以安全替代原消息。
    /// </summary>
    /// <param name="original">原始消息。</param>
    /// <param name="replacement">扩展返回的替换消息。</param>
    /// <returns>role 与运行时类型均兼容时返回 <see langword="true"/>。</returns>
    private static bool IsCompatibleMessageReplacement(ChatMessage original, ChatMessage? replacement) =>
        replacement is not null &&
        replacement.Role.Equals(original.Role, StringComparison.Ordinal) &&
        replacement.GetType() == original.GetType();

    /// <summary>【AgentCore】【回合完成】创建当前上下文及本次新增消息的独立快照</summary>
    /// <param name="message">完成的助手消息</param>
    /// <param name="toolResults">本回合工具结果</param>
    /// <param name="config">当前运行配置</param>
    /// <param name="newMessages">本次运行已发布的新增消息</param>
    /// <returns>供完成和准备钩子读取的回合快照</returns>
    private AgentLoopTurnContext CreateTurnContext(
        AssistantMessage message,
        IReadOnlyList<ToolResultMessage> toolResults,
        AgentLoopConfig config,
        IReadOnlyList<ChatMessage> newMessages) =>
        new(
            message,
            toolResults.ToArray(),
            State.Messages.ToArray(),
            newMessages.ToArray())
        {
            SystemPrompt = State.SystemPrompt,
            Tools = config.Tools.ToArray()
        };

    /// <summary>【AgentCore】【回合完成】调用完成钩子，缺省时保持循环原有调度</summary>
    /// <param name="config">当前配置</param>
    /// <param name="turn">已完成的回合快照</param>
    /// <param name="token">当前运行取消信号，即使已取消也传给钩子</param>
    /// <returns>可选的继续或结束决策</returns>
    private static async ValueTask<AgentTurnDecision?> FinishTurnAsync(
        AgentLoopConfig config, AgentLoopTurnContext turn, CancellationToken token) =>
        config.FinishTurnAsync is null ? null : await config.FinishTurnAsync(turn, token).ConfigureAwait(false);

    /// <summary>【AgentCore】【请求准备】统一应用回合准备和请求准备返回的状态替换</summary>
    /// <param name="config">当前配置</param>
    /// <param name="update">待应用的状态更新</param>
    /// <returns>应用更新后的配置</returns>
    private AgentLoopConfig ApplyRequestUpdate(AgentLoopConfig config, AgentRequestUpdate update)
    {
        var streamOptions = update.StreamOptions ?? config.StreamOptions;
        if (update.ClearReasoning && streamOptions is not null)
        {
            streamOptions = streamOptions with { Reasoning = null };
        }

        if (update.Reasoning is { } reasoning)
        {
            streamOptions = (streamOptions ?? new SimpleStreamOptions()) with
            {
                Reasoning = reasoning == ThinkingLevel.Off ? null : reasoning
            };
        }

        var updated = config with
        {
            Model = update.Model ?? config.Model,
            SystemPrompt = config.SystemPromptFromTranscript ? null : update.SystemPrompt ?? config.SystemPrompt,
            Tools = update.Tools ?? config.Tools,
            StreamOptions = streamOptions
        };
        if (update.Context is not null) State.SetMessages(update.Context.ToList());
        if (config.SystemPromptFromTranscript && update.SystemPrompt is not null) State.ReplaceSystemPrompt(update.SystemPrompt);
        State.Configure(updated.SystemPrompt, update.PreserveModelSelection ? State.Model ?? updated.Model : updated.Model, updated.Tools);
        return updated;
    }

    private static async Task<SimpleStreamOptions> ResolveStreamOptionsAsync(
        AgentLoopConfig config,
        CancellationToken cancellationToken)
    {
        var options = config.StreamOptions ?? new SimpleStreamOptions();
        if (config.GetApiKeyAsync is null)
        {
            return options;
        }

        var apiKey = await config.GetApiKeyAsync(config.Model.Provider, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(apiKey)
            ? options
            : options with { ApiKey = apiKey };
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
        fields["logMessage"] = "【AgentCore】【ProviderRun】开始执行模型流";
        config.LogContext?.AddTo(fields);
        config.LogSink.Log(new TauLogEvent("provider", "run.start", DateTimeOffset.UtcNow, fields));
        return startedAt;
    }

    /// <summary>【AgentCore】【模型请求】使用会话绑定的配置和认证启动流式请求。</summary>
    /// <param name="config">当前回合配置。</param>
    /// <param name="context">发送给模型的上下文。</param>
    /// <param name="streamOptions">已解析的请求选项。</param>
    /// <param name="onException">请求启动失败的记录回调。</param>
    /// <returns>模型响应事件流。</returns>
    private static async ValueTask<AssistantMessageStream> StartProviderStreamAsync(
        AgentLoopConfig config,
        LlmContext context,
        SimpleStreamOptions streamOptions,
        Action<Exception> onException)
    {
        try
        {
            var streamFunction = config.StreamFunction;
            if (streamFunction is not null) return await streamFunction(config.Model, context, streamOptions).ConfigureAwait(false);
            return StreamFunctions.StreamSimple(config.ProviderRegistry, config.Model, context, streamOptions,
                config.ConfigurationStore, config.AuthResolver);
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

    /// <summary>【AgentCore】【请求观测】生成不含正文的日志字段，消息数沿用排除系统声明的口径。</summary>
    /// <param name="config">本次代理配置。</param>
    /// <param name="context">保留系统声明的上下文。</param>
    /// <param name="streamOptions">实际请求选项。</param>
    /// <returns>消息统计、当前有效工具数和传输配置。</returns>
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
            ["messageCount"] = context.Messages.Count(message => message is not SystemMessage).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["toolCount"] = Transcript.GetCurrentTools(Transcript.NormalizeContext(context).Messages).Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            Usage = EnrichUsageCost(message, model)
        };

    private static Usage? EnrichUsageCost(AssistantMessage message, Model model)
    {
        var usage = message.Usage;
        if (usage is not { } value || value.Cost is not null)
        {
            return usage;
        }

        // Anthropic server-side fallback 会在 message_start 返回实际模型，计费必须使用该模型的成本
        var usageModel = model;
        var responseModel = message.ResponseModel ?? message.Model;
        if (!string.IsNullOrWhiteSpace(responseModel) &&
            !responseModel.Equals(model.Id, StringComparison.OrdinalIgnoreCase) &&
            model.Compat?.AllowedFallbackModels is { Count: > 0 } fallbacks)
        {
            var fallback = fallbacks.FirstOrDefault(candidate =>
                candidate.Provider.Equals(model.Provider, StringComparison.OrdinalIgnoreCase) &&
                candidate.Model.Equals(responseModel, StringComparison.OrdinalIgnoreCase));
            if (fallback?.Cost is { } cost)
            {
                usageModel = model with { Id = responseModel, Cost = cost };
            }
        }

        if (usageModel.Cost is null)
        {
            return usage;
        }

        return value with { Cost = ModelCatalog.CalculateCost(usageModel, value) };
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

    /// <summary>【AgentCore】【队列调度】在同一锁内选取并移除消息，使预览、计数与消费使用相同状态</summary>
    /// <param name="queue">目标消息队列</param>
    /// <param name="mode">逐条或全部消费模式</param>
    /// <returns>本次选取的独立消息数组</returns>
    private IReadOnlyList<ChatMessage> DrainQueuedMessagesToList(Queue<ChatMessage> queue, AgentQueueMode mode)
    {
        lock (_queueGate)
        {
            if (mode == AgentQueueMode.All)
            {
                var messages = queue.ToArray();
                queue.Clear();
                return messages;
            }
            return queue.TryDequeue(out var next) ? [next] : [];
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
