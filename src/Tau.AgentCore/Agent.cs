using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Registry;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore;

public sealed record AgentOptions
{
    public required Model Model { get; init; }
    public ProviderRegistry ProviderRegistry { get; init; } = new();
    /// <summary>【AgentCore】【流式函数】实例级请求入口，优先于进程默认函数和提供方注册表</summary>
    public AgentStreamFunction? StreamFunction { get; init; }
    /// <summary>【AgentCore】【请求配置】会话绑定的模型配置来源。</summary>
    public ModelConfigurationStore? ConfigurationStore { get; init; }
    /// <summary>【AgentCore】【请求认证】会话绑定的认证解析器。</summary>
    public ProviderAuthResolver? AuthResolver { get; init; }
    /// <summary>初始提示；历史已有首条系统消息时以历史为准，否则与工具共同生成基线。</summary>
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public IReadOnlyList<IAgentTool> Tools { get; init; } = [];
    public IReadOnlyList<IToolInterceptor> Interceptors { get; init; } = [];
    public SimpleStreamOptions? StreamOptions { get; init; }
    public Func<string, CancellationToken, Task<string?>>? GetApiKeyAsync { get; init; }
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? TransformContext { get; init; }
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContextAsync { get; init; }
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? ConvertToLlm { get; init; }
    public Func<AgentLoopTurnContext, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurnAsync { get; init; }
    /// <summary>【AgentCore】【请求准备】每次模型请求前的状态替换钩子</summary>
    public Func<AgentPrepareRequestContext, CancellationToken, Task<AgentRequestUpdate?>>? PrepareRequestAsync { get; init; }
    /// <summary>【AgentCore】【回合完成】最终消息发布后、turn_end 前的调度钩子</summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<AgentTurnDecision?>>? FinishTurnAsync { get; init; }
    /// <summary>兼容旧停止接口；设置 FinishTurnAsync 时忽略此回调</summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<bool>>? ShouldStopAfterTurnAsync { get; init; }
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
    private IReadOnlyList<IAgentTool> _tools;
    private IReadOnlyList<IToolInterceptor> _interceptors;

    /// <summary>【AgentCore】【运行配置】创建代理并初始化模型、工具及生命周期钩子</summary>
    /// <param name="options">代理初始状态和运行配置</param>
    public Agent(AgentOptions options)
    {
        _model = options.Model;
        _providerRegistry = options.ProviderRegistry;
        StreamFunction = options.StreamFunction ?? AgentStreaming.GetDefaultStreamFunction();
        _tools = options.Tools.ToArray();
        _interceptors = options.Interceptors.ToArray();
        StreamOptions = options.StreamOptions;
        ConfigurationStore = options.ConfigurationStore;
        AuthResolver = options.AuthResolver;
        GetApiKeyAsync = options.GetApiKeyAsync;
        TransformContext = options.TransformContext;
        TransformContextAsync = options.TransformContextAsync;
        ConvertToLlm = options.ConvertToLlm;
        PrepareNextTurnAsync = options.PrepareNextTurnAsync;
        PrepareRequestAsync = options.PrepareRequestAsync;
        FinishTurnAsync = options.FinishTurnAsync;
        ShouldStopAfterTurnAsync = options.ShouldStopAfterTurnAsync;
        ToolExecution = options.ToolExecution;
        LogSink = options.LogSink;
        LogContext = options.LogContext;
        SteeringMode = options.SteeringMode;
        FollowUpMode = options.FollowUpMode;

        SyncStateConfiguration();
        // 1. 【AgentCore】【会话基线】已有开场系统消息具有优先权，旧配置仅用于补齐缺失的基线
        var initial = CreateInitialSystemMessage(options.SystemPrompt);
        State.SetMessages(options.Messages.FirstOrDefault() is not SystemMessage && initial is not null
            ? [initial, .. options.Messages] : options.Messages.ToList());
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

    /// <summary>重放后的完整提示；赋值为 Tau 兼容接口，会替换提示文本并保留工具声明。</summary>
    public string? SystemPrompt
    {
        get => State.SystemPrompt;
        set => State.ReplaceSystemPrompt(value);
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
    /// <summary>【AgentCore】【流式函数】后续运行使用的请求入口，每次运行开始时捕获当前函数</summary>
    public AgentStreamFunction? StreamFunction { get; set; }
    /// <summary>【AgentCore】【请求配置】当前会话的模型配置来源。</summary>
    public ModelConfigurationStore? ConfigurationStore { get; set; }
    /// <summary>【AgentCore】【请求认证】当前会话的认证解析器。</summary>
    public ProviderAuthResolver? AuthResolver { get; set; }
    public Func<string, CancellationToken, Task<string?>>? GetApiKeyAsync { get; set; }
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? TransformContext { get; set; }
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContextAsync { get; set; }
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? ConvertToLlm { get; set; }
    public Func<AgentLoopTurnContext, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurnAsync { get; set; }
    /// <summary>【AgentCore】【请求准备】每次模型请求前的状态替换钩子</summary>
    public Func<AgentPrepareRequestContext, CancellationToken, Task<AgentRequestUpdate?>>? PrepareRequestAsync { get; set; }
    /// <summary>【AgentCore】【回合完成】最终消息发布后、turn_end 前的调度钩子</summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<AgentTurnDecision?>>? FinishTurnAsync { get; set; }
    /// <summary>兼容旧停止接口；设置 FinishTurnAsync 时忽略此回调</summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<bool>>? ShouldStopAfterTurnAsync { get; set; }
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
        if (lastMessage is null || State.Messages.All(message => message is SystemMessage))
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

    /// <summary>【AgentCore】【队列预览】按当前模式预览下一回合会选中的消息，不消费队列；引导消息优先</summary>
    /// <returns>包含原消息对象的独立快照数组</returns>
    public IReadOnlyList<ChatMessage> PeekQueuedMessages() => _runtime.PeekQueuedMessages();

    public void ClearSteeringQueue() => _runtime.ClearSteeringQueue();
    public void ClearFollowUpQueue() => _runtime.ClearFollowUpQueue();

    /// <summary>【AgentCore】【队列清空】一次性清除引导和跟进消息，不在两个清空操作之间暴露中间状态。</summary>
    public void ClearAllQueues() => _runtime.ClearAllQueues();

    public void Abort()
    {
        _activeCts?.Cancel();
        _runtime.Abort();
    }

    /// <summary>【AgentCore】【空闲等待】等待当前运行及其事件监听器结束</summary>
    /// <returns>当前运行的稳定完成任务；空闲时返回已完成任务</returns>
    public Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _activeRun ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// 【AgentCore】【会话重置】清除对话、队列和运行状态，保留重放后的系统提示与已声明工具。
    /// </summary>
    /// <exception cref="InvalidOperationException">agent 仍在运行时抛出。</exception>
    public void Reset()
    {
        ThrowIfActive("Cannot reset an agent while it is running.");
        var baseline = Transcript.GetCurrentSystemMessage(State.Messages);
        _runtime.Reset();
        if (baseline is not null) _runtime.AddMessage(baseline);
        SyncStateConfiguration();
    }

    /// <summary>【AgentCore】【运行生命周期】登记唯一运行任务并启动执行器</summary>
    /// <param name="executor">本次运行的异步执行器</param>
    /// <param name="cancellationToken">调用方的取消信号</param>
    /// <returns>执行结束并清理状态后完成的任务</returns>
    private Task StartRun(Func<CancellationToken, Task> executor, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 1. 【AgentCore】【运行生命周期】在发出任何事件前登记稳定任务，供所有等待方共用
        lock (_sync)
        {
            if (_activeRun is not null)
            {
                cts.Dispose();
                throw new InvalidOperationException(
                    "Agent is already processing a prompt. Use Steer() or FollowUp() to queue messages, or wait for completion.");
            }

            _activeCts = cts;
            _activeRun = completion.Task;
        }

        _ = RunAndClearAsync(executor, cts, completion);
        return completion.Task;
    }

    /// <summary>【AgentCore】【运行生命周期】执行运行并将成功、异常或取消传播到稳定任务</summary>
    /// <param name="executor">运行执行器</param>
    /// <param name="cts">本次运行拥有的取消源</param>
    /// <param name="completion">供提示调用与空闲等待共用的完成信号</param>
    /// <returns>执行和状态清理任务</returns>
    private async Task RunAndClearAsync(
        Func<CancellationToken, Task> executor,
        CancellationTokenSource cts,
        TaskCompletionSource completion)
    {
        Exception? error = null;
        try
        {
            await executor(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            // 2. 【AgentCore】【运行生命周期】先释放运行状态，再唤醒等待方
            lock (_sync)
            {
                if (ReferenceEquals(_activeCts, cts))
                {
                    _activeRun = null;
                    _activeCts = null;
                }

                cts.Dispose();
                if (error is OperationCanceledException cancelled)
                {
                    completion.TrySetCanceled(cancelled.CancellationToken);
                }
                else if (error is not null)
                {
                    completion.TrySetException(error);
                }
                else
                {
                    completion.TrySetResult();
                }
            }
        }
    }

    /// <summary>【AgentCore】【运行生命周期】发布提示消息并将运行事件转发给订阅者</summary>
    /// <param name="promptMessages">本次运行新增的提示消息</param>
    /// <param name="skipInitialSteeringPoll">是否跳过已完成的首次 steering 选择</param>
    /// <param name="cancellationToken">本次运行取消信号</param>
    /// <returns>运行及订阅者处理结束后完成的任务</returns>
    private async Task RunRuntimeAsync(
        IReadOnlyList<ChatMessage> promptMessages,
        bool skipInitialSteeringPoll,
        CancellationToken cancellationToken)
    {
        var sawAgentEnd = false;
        var newMessages = new List<ChatMessage>();

        SyncStateConfiguration();
        State.SetStreaming(true);
        State.SetError(null);

        try
        {
            await foreach (var runtimeEvent in _runtime.RunAsync(CreateConfig(skipInitialSteeringPoll, promptMessages), cancellationToken)
                               .ConfigureAwait(false))
            {
                // 1. 【AgentCore】【运行结果】按发布顺序累计新增消息，异常路径同样保留已完成输入与输出
                if (runtimeEvent is MessageEndEvent messageEnd) newMessages.Add(messageEnd.Message);
                if (runtimeEvent is AgentEndEvent) sawAgentEnd = true;
                await EmitAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string error = "Operation canceled.";
            await EmitFailureAsync(error, aborted: true, sawAgentEnd, newMessages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await EmitFailureAsync(ex.Message, aborted: false, sawAgentEnd, newMessages, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            State.SetStreaming(false);
        }
    }

    /// <summary>【AgentCore】【运行配置】快照当前配置并传入本次提示消息以区分新增消息与历史</summary>
    /// <param name="skipInitialSteeringPoll">是否跳过已由继续操作完成的首次队列轮询</param>
    /// <param name="promptMessages">由运行时追加并发布事件的本次提示消息</param>
    /// <returns>本次循环配置</returns>
    private AgentLoopConfig CreateConfig(bool skipInitialSteeringPoll, IReadOnlyList<ChatMessage> promptMessages) =>
        new()
        {
            Model = _model,
            ProviderRegistry = _providerRegistry,
            ConfigurationStore = ConfigurationStore,
            AuthResolver = AuthResolver,
            Tools = _tools,
            Interceptors = _interceptors,
            LogSink = LogSink,
            LogContext = LogContext,
            SystemPromptFromTranscript = true,
            StreamOptions = StreamOptions,
            StreamFunction = StreamFunction,
            GetApiKeyAsync = GetApiKeyAsync,
            DefaultExecutionMode = ToolExecution,
            TransformContext = TransformContext,
            TransformContextAsync = TransformContextAsync,
            ConvertToLlm = ConvertToLlm,
            PrepareNextTurnAsync = PrepareNextTurnAsync,
            PrepareRequestAsync = PrepareRequestAsync,
            FinishTurnAsync = FinishTurnAsync,
            ShouldStopAfterTurnAsync = ShouldStopAfterTurnAsync,
            InitialMessages = promptMessages,
            SkipInitialSteeringPoll = skipInitialSteeringPoll
        };

    /// <summary>【AgentCore】【运行失败】保留本次已发布消息，并追加异常产生的助手结果。</summary>
    /// <param name="error">错误说明。</param>
    /// <param name="aborted">是否由取消引起。</param>
    /// <param name="sawAgentEnd">是否已发布结束事件。</param>
    /// <param name="newMessages">本次已发布的消息。</param>
    /// <param name="cancellationToken">当前取消信号。</param>
    /// <returns>事件处理任务。</returns>
    private async Task EmitFailureAsync(
        string error,
        bool aborted,
        bool sawAgentEnd,
        IReadOnlyList<ChatMessage> newMessages,
        CancellationToken cancellationToken)
    {
        State.SetError(error);
        var failureMessage = CreateFailureMessage(error, aborted);
        _runtime.State.AddMessage(failureMessage);

        if (!sawAgentEnd)
        {
            await EmitAsync(new AgentEndEvent(error, [.. newMessages, failureMessage]), cancellationToken).ConfigureAwait(false);
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

    /// <summary>【AgentCore】【状态同步】同步执行配置，提示始终由会话中的系统声明重放。</summary>
    private void SyncStateConfiguration() => State.Configure(null, _model, _tools);

    /// <summary>【AgentCore】【会话基线】为旧配置复制纯工具定义并创建首条系统消息。</summary>
    /// <param name="systemPrompt">初始或恢复时的默认提示。</param>
    /// <returns>独立的基线；空配置返回 null。</returns>
    private SystemMessage? CreateInitialSystemMessage(string? systemPrompt) => Transcript.CreateInitialSystemMessage(systemPrompt,
        _tools.Select(tool => new Tool(tool.Name, tool.Description, tool.ParameterSchema) { ConstrainedSampling = tool.ConstrainedSampling }).ToArray());

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
