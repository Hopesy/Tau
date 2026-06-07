using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.Agent.Platform;

public sealed class AgentApplication
{
    private readonly IAgentSessionStore? _sessionStore;
    private readonly Dictionary<string, string> _metadata;
    private readonly DateTimeOffset _createdAt;
    private readonly ITauLogSink _logSink;
    private readonly TauRuntimeLogContext? _baseLogContext;
    private readonly SimpleStreamOptions? _baseStreamOptions;
    private readonly string? _logReference;

    internal AgentApplication(
        Agent agent,
        string? sessionId,
        IAgentSessionStore? sessionStore,
        IReadOnlyDictionary<string, string> metadata,
        DateTimeOffset createdAt,
        ITauLogSink logSink,
        TauRuntimeLogContext? baseLogContext,
        SimpleStreamOptions? baseStreamOptions,
        string? logReference)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        SessionId = sessionId;
        _sessionStore = sessionStore;
        _metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        _createdAt = createdAt;
        _logSink = logSink;
        _baseLogContext = baseLogContext;
        _baseStreamOptions = baseStreamOptions;
        _logReference = logReference;
    }

    public static AgentApplicationBuilder CreateBuilder() => new();

    public Agent Agent { get; }
    public string? SessionId { get; }
    public AgentState State => Agent.State;

    public IDisposable Subscribe(Action<AgentEvent> listener) => Agent.Subscribe(listener);

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener) =>
        Agent.Subscribe(listener);

    public Task<AgentRunResult> PromptAsync(
        string input,
        CancellationToken cancellationToken = default) =>
        PromptAsync(input, images: null, cancellationToken);

    public Task<AgentRunResult> PromptAsync(
        string input,
        IReadOnlyList<ImageContent>? images,
        CancellationToken cancellationToken = default)
    {
        var content = new List<ContentBlock> { new TextContent(input) };
        if (images is { Count: > 0 })
        {
            content.AddRange(images);
        }

        return PromptAsync(new UserMessage(content), cancellationToken);
    }

    public Task<AgentRunResult> PromptAsync(ChatMessage message, CancellationToken cancellationToken = default) =>
        PromptAsync([message], cancellationToken);

    public async Task<AgentRunResult> PromptAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var events = new List<AgentEvent>();
        var previousMessages = Agent.State.Messages.ToArray();
        var runLogContext = CreateRunLogContext();
        Agent.LogSink = _logSink;
        Agent.LogContext = runLogContext;
        Agent.StreamOptions = EnsureStreamSession(_baseStreamOptions, SessionId);

        using var subscription = Agent.Subscribe((evt, _) =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        });

        await Agent.PromptAsync(messages, cancellationToken).ConfigureAwait(false);

        var result = BuildResult(events, runLogContext);
        if (!result.IsSuccess)
        {
            RestoreMessages(previousMessages, result.ErrorMessage);
            return result;
        }

        if (result.IsSuccess && _sessionStore is not null && !string.IsNullOrWhiteSpace(SessionId))
        {
            _sessionStore.Save(new AgentSessionSnapshot
            {
                SessionId = SessionId,
                Messages = result.Messages,
                Metadata = _metadata,
                CreatedAt = _createdAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                LogReference = _logReference
            });

            result = result with { SavedSession = true };
        }

        return result;
    }

    public void Abort() => Agent.Abort();

    public Task WaitForIdleAsync() => Agent.WaitForIdleAsync();

    public void Reset() => Agent.Reset();

    private TauRuntimeLogContext CreateRunLogContext()
    {
        var context = (_baseLogContext ?? new TauRuntimeLogContext()).EnsureCorrelationId();
        if (string.IsNullOrWhiteSpace(context.SessionId) && !string.IsNullOrWhiteSpace(SessionId))
        {
            context = context with { SessionId = SessionId };
        }

        if (string.IsNullOrWhiteSpace(context.MessageId))
        {
            context = context with { MessageId = Guid.NewGuid().ToString("N") };
        }

        return context;
    }

    private AgentRunResult BuildResult(
        IReadOnlyList<AgentEvent> events,
        TauRuntimeLogContext logContext)
    {
        var agentEnd = events.OfType<AgentEndEvent>().LastOrDefault();
        var messages = (agentEnd?.Messages.Count > 0 ? agentEnd.Messages : Agent.State.Messages).ToArray();
        var assistant = messages.OfType<AssistantMessage>().LastOrDefault();
        var errorMessage = agentEnd?.ErrorMessage ?? assistant?.ErrorMessage;

        return new AgentRunResult
        {
            SessionId = SessionId,
            Messages = messages,
            Events = events.ToArray(),
            LogContext = logContext,
            AssistantText = assistant is null ? null : ReadText(assistant.Content),
            Usage = assistant?.Usage,
            StopReason = assistant?.StopReason,
            ErrorMessage = errorMessage,
            SavedSession = false
        };
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

    private void RestoreMessages(IReadOnlyList<ChatMessage> messages, string? errorMessage)
    {
        Agent.State.SetMessages(messages.ToList());
        Agent.State.SetPendingToolCalls([]);
        Agent.State.SetStreaming(false);
        Agent.State.SetError(errorMessage);
    }

    private static string ReadText(IEnumerable<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));
}
