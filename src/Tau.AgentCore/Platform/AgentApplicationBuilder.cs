using System.Text.Json;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;

namespace Tau.AgentCore.Platform;

public sealed class AgentApplicationBuilder
{
    private readonly List<ChatMessage> _messages = [];
    private readonly List<IAgentTool> _tools = [];
    private readonly List<IToolInterceptor> _interceptors = [];
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);
    private Model? _model;
    private ProviderRegistry? _providerRegistry;
    private string? _systemPrompt;
    private string? _sessionId;
    private IAgentSessionStore? _sessionStore;
    private ITauLogSink _logSink = NullTauLogSink.Instance;
    private TauRuntimeLogContext? _logContext;
    private SimpleStreamOptions? _streamOptions;
    private Func<string, CancellationToken, Task<string?>>? _getApiKeyAsync;
    private Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? _transformContext;
    private Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? _transformContextAsync;
    private Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? _convertToLlm;
    private AgentQueueMode _steeringMode = AgentQueueMode.OneAtATime;
    private AgentQueueMode _followUpMode = AgentQueueMode.OneAtATime;
    private ToolExecutionMode _toolExecution = ToolExecutionMode.Parallel;
    private string? _logReference;

    public AgentApplicationBuilder UseProviderRegistry(ProviderRegistry registry)
    {
        _providerRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
        return this;
    }

    public AgentApplicationBuilder UseModel(Model model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        return this;
    }

    public AgentApplicationBuilder UseSystemPrompt(string? systemPrompt)
    {
        _systemPrompt = systemPrompt;
        return this;
    }

    public AgentApplicationBuilder UseSessionId(string? sessionId)
    {
        _sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        return this;
    }

    public AgentApplicationBuilder UseSessionStore(IAgentSessionStore sessionStore)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        return this;
    }

    public AgentApplicationBuilder UseLogSink(ITauLogSink logSink)
    {
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        return this;
    }

    public AgentApplicationBuilder UseLogContext(TauRuntimeLogContext? logContext)
    {
        _logContext = logContext;
        return this;
    }

    public AgentApplicationBuilder UseLogReference(string? logReference)
    {
        _logReference = string.IsNullOrWhiteSpace(logReference) ? null : logReference.Trim();
        return this;
    }

    public AgentApplicationBuilder UseStreamOptions(SimpleStreamOptions? streamOptions)
    {
        _streamOptions = streamOptions;
        return this;
    }

    public AgentApplicationBuilder UseApiKeyResolver(
        Func<string, CancellationToken, Task<string?>>? getApiKeyAsync)
    {
        _getApiKeyAsync = getApiKeyAsync;
        return this;
    }

    public AgentApplicationBuilder UseTransformContext(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? transformContext)
    {
        _transformContext = transformContext;
        return this;
    }

    public AgentApplicationBuilder UseTransformContext(Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? transformContext)
    {
        _transformContextAsync = transformContext;
        return this;
    }

    public AgentApplicationBuilder UseConvertToLlm(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? convertToLlm)
    {
        _convertToLlm = convertToLlm;
        return this;
    }

    public AgentApplicationBuilder UseQueueModes(
        AgentQueueMode steeringMode,
        AgentQueueMode followUpMode)
    {
        _steeringMode = steeringMode;
        _followUpMode = followUpMode;
        return this;
    }

    public AgentApplicationBuilder UseToolExecution(ToolExecutionMode executionMode)
    {
        _toolExecution = executionMode;
        return this;
    }

    public AgentApplicationBuilder AddMessage(ChatMessage message)
    {
        _messages.Add(message ?? throw new ArgumentNullException(nameof(message)));
        return this;
    }

    public AgentApplicationBuilder AddMessages(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.AddRange(messages);
        return this;
    }

    public AgentApplicationBuilder AddTool(IAgentTool tool)
    {
        _tools.Add(tool ?? throw new ArgumentNullException(nameof(tool)));
        return this;
    }

    public AgentApplicationBuilder AddTool(
        string name,
        string label,
        string description,
        JsonElement parameterSchema,
        Func<AgentToolContext, CancellationToken, ToolResult> execute,
        ToolExecutionMode executionMode = ToolExecutionMode.Parallel,
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>>? prepareArguments = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return AddTool(name, label, description, parameterSchema,
            (context, ct) => Task.FromResult(execute(context, ct)),
            executionMode,
            prepareArguments);
    }

    public AgentApplicationBuilder AddTool(
        string name,
        string label,
        string description,
        JsonElement parameterSchema,
        AgentToolDelegate execute,
        ToolExecutionMode executionMode = ToolExecutionMode.Parallel,
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>>? prepareArguments = null)
    {
        _tools.Add(new DelegateAgentTool(name, label, description, parameterSchema, execute, executionMode, prepareArguments));
        return this;
    }

    public AgentApplicationBuilder AddInterceptor(IToolInterceptor interceptor)
    {
        _interceptors.Add(interceptor ?? throw new ArgumentNullException(nameof(interceptor)));
        return this;
    }

    public AgentApplicationBuilder AddMetadata(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _metadata[key] = value;
        return this;
    }

    public AgentApplication Build()
    {
        var model = _model ?? throw new InvalidOperationException("Agent model is required. Call UseModel(...) before Build().");
        var providerRegistry = _providerRegistry ?? throw new InvalidOperationException("Provider registry is required. Call UseProviderRegistry(...) before Build().");
        if (_sessionStore is not null && string.IsNullOrWhiteSpace(_sessionId))
        {
            throw new InvalidOperationException("A session id is required when a session store is configured.");
        }

        AgentSessionSnapshot? restored = null;
        if (_sessionStore is not null && _sessionId is not null)
        {
            restored = _sessionStore.Load(_sessionId);
        }

        var metadata = new Dictionary<string, string>(
            restored?.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var item in _metadata)
        {
            metadata[item.Key] = item.Value;
        }

        var streamOptions = EnsureStreamSession(_streamOptions, _sessionId);
        var agent = new Agent(new AgentOptions
        {
            Model = model,
            ProviderRegistry = providerRegistry,
            SystemPrompt = _systemPrompt,
            Messages = restored?.Messages ?? _messages.ToArray(),
            Tools = _tools.ToArray(),
            Interceptors = _interceptors.ToArray(),
            StreamOptions = streamOptions,
            GetApiKeyAsync = _getApiKeyAsync,
            TransformContext = _transformContext,
            TransformContextAsync = _transformContextAsync,
            ConvertToLlm = _convertToLlm,
            SteeringMode = _steeringMode,
            FollowUpMode = _followUpMode,
            ToolExecution = _toolExecution,
            LogSink = _logSink,
            LogContext = _logContext
        });

        return new AgentApplication(
            agent,
            _sessionId,
            _sessionStore,
            metadata,
            restored?.CreatedAt ?? DateTimeOffset.UtcNow,
            _logSink,
            _logContext,
            streamOptions,
            _logReference);
    }

    private static SimpleStreamOptions? EnsureStreamSession(SimpleStreamOptions? options, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return options;
        }

        if (options is null)
        {
            return new SimpleStreamOptions { SessionId = sessionId };
        }

        return string.IsNullOrWhiteSpace(options.SessionId)
            ? options with { SessionId = sessionId }
            : options;
    }
}
