using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class FakeCodingAgentRunner : ICodingAgentRunner, ICodingAgentToolResultDetailsProvider
{
    private readonly Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> _run;
    private readonly object _gate = new();
    private readonly List<string> _pendingSteeringMessages = [];
    private readonly List<string> _pendingFollowUpMessages = [];
    private readonly List<ChatMessage> _pendingSteeringChatMessages = [];
    private readonly List<ChatMessage> _pendingFollowUpChatMessages = [];
    private int _activeCompactionCount;
    private readonly Dictionary<string, List<Model>> _models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] =
        [
            new Model
            {
                Provider = "openai",
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-responses",
                ContextWindow = 128_000,
                Reasoning = true
            }
        ],
        ["google"] =
        [
            new Model
            {
                Provider = "google",
                Id = "gemini-2.5-pro",
                Name = "Gemini 2.5 Pro",
                Api = "google-gemini",
                ContextWindow = 1_048_576,
                Reasoning = true
            }
        ]
    };

    public FakeCodingAgentRunner(Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> run)
    {
        _run = run;
        Model = _models["openai"][0];
    }

    public List<string> Inputs { get; } = [];
    public List<IReadOnlyList<ContentBlock>> ContentInputs { get; } = [];
    public List<TauRuntimeLogContext?> RunLogContexts { get; } = [];
    public List<string> SteeringInputs { get; } = [];
    public List<IReadOnlyList<ContentBlock>> SteeringContentInputs { get; } = [];
    public List<ChatMessage> SteeringChatInputs { get; } = [];
    public List<string> FollowUpInputs { get; } = [];
    public List<IReadOnlyList<ContentBlock>> FollowUpContentInputs { get; } = [];
    public List<ChatMessage> FollowUpChatInputs { get; } = [];
    public List<ChatMessage> MutableMessages { get; } = [];
    public List<ChatMessage> ChatInputs { get; } = [];
    public List<IReadOnlyList<ChatMessage>> ChatBatchInputs { get; } = [];
    public List<AgentEvent> PublishedLifecycleEvents { get; } = [];
    public Dictionary<string, object?> MutableToolResultDetailsByToolCallId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ChatMessage> Messages => MutableMessages;
    public IReadOnlyDictionary<string, object?> ToolResultDetailsByToolCallId => MutableToolResultDetailsByToolCallId;
    public Model Model { get; private set; }
    public string? SessionName { get; set; }
    public ThinkingLevel? ThinkingLevel { get; set; }
    public AgentQueueMode SteeringMode { get; set; } = AgentQueueMode.OneAtATime;
    public AgentQueueMode FollowUpMode { get; set; } = AgentQueueMode.OneAtATime;
    public int PendingMessageCount
    {
        get
        {
            lock (_gate)
            {
                return _pendingSteeringMessages.Count
                    + _pendingFollowUpMessages.Count
                    + _pendingSteeringChatMessages.Count
                    + _pendingFollowUpChatMessages.Count;
            }
        }
    }

    public bool IsCompacting => Volatile.Read(ref _activeCompactionCount) > 0;
    public bool IsStreaming { get; set; }
    public Func<string?, CancellationToken, Task<CodingAgentCompactionResult>>? CompactHandler { get; set; }
    public Func<IReadOnlyList<ChatMessage>, string?, bool, CancellationToken, Task<CodingAgentBranchSummaryResult>>? BranchSummaryHandler { get; set; }
    public Action<string>? SteeringObserver { get; set; }
    public Action<string>? FollowUpObserver { get; set; }
    public string? LastCompactInstructions { get; private set; }
    public string? LastBranchSummaryInstructions { get; private set; }
    public bool LastBranchSummaryReplaceInstructions { get; private set; }
    public IReadOnlyList<ChatMessage>? LastBranchSummaryMessages { get; private set; }
    public IReadOnlyList<CodingAgentSkill>? LastRefreshedSkills { get; private set; }
    public IReadOnlyList<CodingAgentContextFile>? LastRefreshedContextFiles { get; private set; }
    public bool RefreshSkillsResult { get; set; } = true;
    public List<string> LoggedOutProviders { get; } = [];
    public bool LogoutResult { get; set; } = true;
    public int ResetSessionCalls { get; private set; }

    public IReadOnlyList<string> GetProviders() => [.. _models.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<Model> GetModels(string provider) =>
        _models.TryGetValue(provider, out var models) ? models : [];

    /// <summary>
    /// 向测试 runner 注册一个额外模型，用于覆盖模型选择相关分支。
    /// </summary>
    /// <param name="model">需要注册到测试模型目录中的模型。</param>
    public void AddModel(Model model)
    {
        if (!_models.TryGetValue(model.Provider, out var models))
        {
            models = [];
            _models[model.Provider] = models;
        }

        models.Add(model);
    }

    public void SetModelReasoning(string provider, string modelId, bool reasoning)
    {
        if (!_models.TryGetValue(provider, out var models))
        {
            throw new KeyNotFoundException($"Provider '{provider}' is not registered.");
        }

        var index = models.FindIndex(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new KeyNotFoundException($"Model '{provider}/{modelId}' is not registered.");
        }

        var updated = models[index] with { Reasoning = reasoning };
        models[index] = updated;
        if (Model.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
            Model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
        {
            Model = updated;
        }
    }

    public Model SelectModel(string? providerId, string? modelId)
    {
        var provider = string.IsNullOrWhiteSpace(providerId) ? Model.Provider : providerId;
        if (!string.IsNullOrWhiteSpace(modelId) && modelId.Contains('/'))
        {
            var parts = modelId.Split('/', 2);
            provider = parts[0];
            modelId = parts[1];
        }

        if (!_models.TryGetValue(provider, out var models))
        {
            throw new KeyNotFoundException($"Provider '{provider}' is not registered.");
        }

        Model = string.IsNullOrWhiteSpace(modelId)
            ? models[0]
            : models.Single(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return Model;
    }

    public ProviderAuthStatus AuthStatus { get; set; } = new("openai", false, "none", false, false, "No credentials found.");
    public Dictionary<string, ProviderAuthStatus> AuthStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void ConfigureAuth(params string[] providers)
    {
        foreach (var provider in providers)
        {
            AuthStatuses[provider] = new ProviderAuthStatus(
                provider,
                true,
                "environment",
                false,
                false,
                "Credentials are available.");
        }
    }

    public ProviderAuthStatus GetAuthStatus(string? providerId = null)
    {
        var provider = string.IsNullOrWhiteSpace(providerId) ? Model.Provider : providerId;
        return AuthStatuses.TryGetValue(provider, out var status)
            ? status with { Provider = provider }
            : AuthStatus with { Provider = provider };
    }

    public IOAuthProvider? GetOAuthProvider(string providerId) =>
        OAuthProvider is not null && OAuthProvider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)
            ? OAuthProvider
            : null;

    public void SaveOAuthCredentials(string providerId, OAuthCredentials credentials)
    {
        SavedOAuthCredentials = (providerId, credentials);
    }

    public IOAuthProvider? OAuthProvider { get; set; }
    public (string ProviderId, OAuthCredentials Credentials)? SavedOAuthCredentials { get; private set; }

    public bool Logout(string providerId)
    {
        LoggedOutProviders.Add(providerId);
        return LogoutResult;
    }

    public bool RefreshSkills(IReadOnlyList<CodingAgentSkill> skills)
    {
        return RefreshSystemPromptResources(skills, LastRefreshedContextFiles ?? []);
    }

    public bool RefreshSystemPromptResources(
        IReadOnlyList<CodingAgentSkill> skills,
        IReadOnlyList<CodingAgentContextFile> contextFiles)
    {
        LastRefreshedSkills = skills;
        LastRefreshedContextFiles = contextFiles;
        return RefreshSkillsResult;
    }

    public CodingAgentSessionStats GetSessionStats(string? sessionFile = null)
    {
        return new CodingAgentSessionStats(
            Model.Provider,
            Model.Id,
            Messages.Count,
            Messages.OfType<UserMessage>().Count(),
            Messages.OfType<AssistantMessage>().Count(),
            Messages.OfType<ToolResultMessage>().Count(),
            Messages.OfType<AssistantMessage>().Sum(message => message.Content.OfType<ToolCallContent>().Count()),
            CodingAgentTokenEstimator.Estimate(Messages),
            Model.ContextWindow,
            SessionName,
            sessionFile)
            .WithUsage(CodingAgentSessionUsageSummary.FromMessages(Messages));
    }

    public async Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default)
    {
        LastCompactInstructions = customInstructions;
        Interlocked.Increment(ref _activeCompactionCount);
        try
        {
            if (CompactHandler is not null)
            {
                return await CompactHandler(customInstructions, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Compaction is not configured for this test runner.");
        }
        finally
        {
            Interlocked.Decrement(ref _activeCompactionCount);
        }
    }

    public async Task<CodingAgentBranchSummaryResult> SummarizeBranchAsync(
        IReadOnlyList<ChatMessage> messages,
        string? customInstructions = null,
        bool replaceInstructions = false,
        CancellationToken cancellationToken = default)
    {
        LastBranchSummaryMessages = messages;
        LastBranchSummaryInstructions = customInstructions;
        LastBranchSummaryReplaceInstructions = replaceInstructions;
        Interlocked.Increment(ref _activeCompactionCount);
        try
        {
            if (BranchSummaryHandler is not null)
            {
                return await BranchSummaryHandler(messages, customInstructions, replaceInstructions, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Branch summarization is not configured for this test runner.");
        }
        finally
        {
            Interlocked.Decrement(ref _activeCompactionCount);
        }
    }

    public void ResetSession()
    {
        ResetSessionCalls++;
        MutableMessages.Clear();
        SessionName = null;
        ClearPendingMessages();
    }

    public void RestoreSession(CodingAgentSessionSnapshot snapshot)
    {
        SelectModel(snapshot.Provider, snapshot.Model);
        MutableMessages.Clear();
        MutableMessages.AddRange(snapshot.Messages);
        SessionName = snapshot.Name;
        ClearPendingMessages();
    }

    public void Steer(string input)
    {
        SteeringInputs.Add(input);
        TrackPendingSteeringMessage(input);
        SteeringObserver?.Invoke(input);
    }

    public void Steer(IReadOnlyList<ContentBlock> input)
    {
        SteeringContentInputs.Add(input);
        var text = ContentText(input);
        SteeringInputs.Add(text);
        TrackPendingSteeringMessage(text);
        SteeringObserver?.Invoke(text);
    }

    public void Steer(ChatMessage input)
    {
        ArgumentNullException.ThrowIfNull(input);
        SteeringChatInputs.Add(input);
        var text = MessageText(input);
        if (text.Length > 0)
        {
            SteeringInputs.Add(text);
            SteeringObserver?.Invoke(text);
        }

        TrackPendingSteeringMessage(input);
    }

    public void FollowUp(string input)
    {
        FollowUpInputs.Add(input);
        TrackPendingFollowUpMessage(input);
        FollowUpObserver?.Invoke(input);
    }

    public void FollowUp(IReadOnlyList<ContentBlock> input)
    {
        FollowUpContentInputs.Add(input);
        var text = ContentText(input);
        FollowUpInputs.Add(text);
        TrackPendingFollowUpMessage(text);
        FollowUpObserver?.Invoke(text);
    }

    public void FollowUp(ChatMessage input)
    {
        ArgumentNullException.ThrowIfNull(input);
        FollowUpChatInputs.Add(input);
        var text = MessageText(input);
        if (text.Length > 0)
        {
            FollowUpInputs.Add(text);
            FollowUpObserver?.Invoke(text);
        }

        TrackPendingFollowUpMessage(input);
    }

    public CodingAgentQueuedMessages DrainQueuedMessages()
    {
        lock (_gate)
        {
            var queued = new CodingAgentQueuedMessages(
                _pendingSteeringMessages
                    .Concat(_pendingSteeringChatMessages.Select(MessageText))
                    .Where(static text => text.Length > 0)
                    .ToArray(),
                _pendingFollowUpMessages
                    .Concat(_pendingFollowUpChatMessages.Select(MessageText))
                    .Where(static text => text.Length > 0)
                    .ToArray());
            _pendingSteeringMessages.Clear();
            _pendingFollowUpMessages.Clear();
            _pendingSteeringChatMessages.Clear();
            _pendingFollowUpChatMessages.Clear();
            return queued;
        }
    }

    public void AppendMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        MutableMessages.Add(message);
    }

    public Task PublishLifecycleEventAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        cancellationToken.ThrowIfCancellationRequested();
        PublishedLifecycleEvents.Add(agentEvent);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        string input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        Inputs.Add(input);
        RunLogContexts.Add(logContext);
        return TrackPendingMessageConsumption(_run(input, cancellationToken), cancellationToken);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ContentBlock> input,
        CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ContentBlock> input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        ContentInputs.Add(input);
        RunLogContexts.Add(logContext);
        return TrackPendingMessageConsumption(_run(ContentText(input), cancellationToken), cancellationToken);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(ChatMessage input, CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        ChatMessage input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ChatInputs.Add(input);
        RunLogContexts.Add(logContext);
        return TrackPendingMessageConsumption(_run(MessageText(input), cancellationToken), cancellationToken);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ChatMessage> input,
        CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ChatMessage> input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ChatBatchInputs.Add(input);
        RunLogContexts.Add(logContext);
        return TrackPendingMessageConsumption(
            _run(string.Join(Environment.NewLine, input.Select(MessageText).Where(static text => text.Length > 0)), cancellationToken),
            cancellationToken);
    }

    private async IAsyncEnumerable<AgentEvent> TrackPendingMessageConsumption(
        IAsyncEnumerable<AgentEvent> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (evt is MessageStartEvent { Partial: UserMessage user })
            {
                MarkPendingMessageConsumed(ContentText(user.Content));
            }

            yield return evt;
        }
    }

    private void TrackPendingSteeringMessage(string input)
    {
        lock (_gate)
        {
            _pendingSteeringMessages.Add(input);
        }
    }

    private void TrackPendingSteeringMessage(ChatMessage input)
    {
        lock (_gate)
        {
            _pendingSteeringChatMessages.Add(input);
        }
    }

    private void TrackPendingFollowUpMessage(string input)
    {
        lock (_gate)
        {
            _pendingFollowUpMessages.Add(input);
        }
    }

    private void TrackPendingFollowUpMessage(ChatMessage input)
    {
        lock (_gate)
        {
            _pendingFollowUpChatMessages.Add(input);
        }
    }

    private void MarkPendingMessageConsumed(string input)
    {
        lock (_gate)
        {
            var steeringIndex = _pendingSteeringMessages.IndexOf(input);
            if (steeringIndex >= 0)
            {
                _pendingSteeringMessages.RemoveAt(steeringIndex);
                return;
            }

            var steeringChatIndex = _pendingSteeringChatMessages.FindIndex(message =>
                string.Equals(MessageText(message), input, StringComparison.Ordinal));
            if (steeringChatIndex >= 0)
            {
                _pendingSteeringChatMessages.RemoveAt(steeringChatIndex);
                return;
            }

            var followUpIndex = _pendingFollowUpMessages.IndexOf(input);
            if (followUpIndex >= 0)
            {
                _pendingFollowUpMessages.RemoveAt(followUpIndex);
                return;
            }

            var followUpChatIndex = _pendingFollowUpChatMessages.FindIndex(message =>
                string.Equals(MessageText(message), input, StringComparison.Ordinal));
            if (followUpChatIndex >= 0)
            {
                _pendingFollowUpChatMessages.RemoveAt(followUpChatIndex);
            }
        }
    }

    private void ClearPendingMessages()
    {
        lock (_gate)
        {
            _pendingSteeringMessages.Clear();
            _pendingFollowUpMessages.Clear();
            _pendingSteeringChatMessages.Clear();
            _pendingFollowUpChatMessages.Clear();
        }
    }

    private static string ContentText(IReadOnlyList<ContentBlock> input) =>
        string.Join(Environment.NewLine, input.OfType<TextContent>().Select(content => content.Text));

    private static string MessageText(ChatMessage message) => message switch
    {
        UserMessage user => ContentText(user.Content),
        AgentCustomMessage custom => ContentText(custom.Content),
        _ => string.Empty
    };
}
