using Tau.AgentCore.Harness.Session;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;

namespace Tau.AgentCore.Harness;

public enum AgentHarnessPhase
{
    Idle,
    Turn,
    Compaction,
    BranchSummary
}

public sealed class AgentHarnessException : Exception
{
    public AgentHarnessException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record AgentHarnessResources(
    IReadOnlyList<AgentHarnessSkill>? Skills = null,
    IReadOnlyList<AgentPromptTemplate>? PromptTemplates = null);

public sealed record AgentHarnessOptions<TMetadata>
    where TMetadata : SessionMetadata
{
    public required AgentHarnessSession<TMetadata> Session { get; init; }
    public required ProviderRegistry ProviderRegistry { get; init; }
    public required Model Model { get; init; }
    public IReadOnlyList<IAgentTool> Tools { get; init; } = [];
    public IReadOnlyList<string>? ActiveToolNames { get; init; }
    public AgentHarnessResources Resources { get; init; } = new();
    public string? SystemPrompt { get; init; }
    public Func<AgentHarnessSystemPromptContext<TMetadata>, CancellationToken, Task<string>>? SystemPromptFactory { get; init; }
    public SimpleStreamOptions? StreamOptions { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }
    public AgentQueueMode SteeringMode { get; init; } = AgentQueueMode.OneAtATime;
    public AgentQueueMode FollowUpMode { get; init; } = AgentQueueMode.OneAtATime;
    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;
}

public sealed record AgentHarnessSystemPromptContext<TMetadata>(
    AgentHarnessSession<TMetadata> Session,
    Model Model,
    ThinkingLevel? ThinkingLevel,
    IReadOnlyList<IAgentTool> ActiveTools,
    AgentHarnessResources Resources)
    where TMetadata : SessionMetadata;

public sealed record AgentHarnessQueueUpdateEvent(
    IReadOnlyList<ChatMessage> Steer,
    IReadOnlyList<ChatMessage> FollowUp,
    IReadOnlyList<ChatMessage> NextTurn)
{
    public string Type => "queue_update";
}

public sealed record AgentHarnessSavePointEvent(bool HadPendingMutations)
{
    public string Type => "save_point";
}

public sealed record AgentHarnessSettledEvent(int NextTurnCount)
{
    public string Type => "settled";
}

public sealed record AgentHarnessModelUpdateEvent(Model Model, Model PreviousModel)
{
    public string Type => "model_update";
}

public sealed record AgentHarnessThinkingLevelUpdateEvent(ThinkingLevel? Level, ThinkingLevel? PreviousLevel)
{
    public string Type => "thinking_level_update";
}

public sealed record AgentHarnessToolsUpdateEvent(
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> PreviousToolNames,
    IReadOnlyList<string> ActiveToolNames,
    IReadOnlyList<string> PreviousActiveToolNames)
{
    public string Type => "tools_update";
}

public sealed record AgentHarnessResourcesUpdateEvent(
    AgentHarnessResources Resources,
    AgentHarnessResources PreviousResources)
{
    public string Type => "resources_update";
}

public sealed record AgentHarnessSessionCompactEvent(
    CompactionSessionEntry CompactionEntry,
    bool FromHook)
{
    public string Type => "session_compact";
}

public sealed record AgentHarnessSessionTreeEvent(
    string? NewLeafId,
    string? OldLeafId,
    BranchSummarySessionEntry? SummaryEntry,
    bool FromHook)
{
    public string Type => "session_tree";
}

public sealed record AgentHarnessNavigateTreeResult(
    bool Cancelled,
    string? EditorText = null,
    BranchSummarySessionEntry? SummaryEntry = null);

public sealed record AgentHarnessAbortResult(
    IReadOnlyList<ChatMessage> ClearedSteer,
    IReadOnlyList<ChatMessage> ClearedFollowUp);

public sealed class AgentHarness<TMetadata>
    where TMetadata : SessionMetadata
{
    private const string DefaultSystemPrompt = "You are a helpful assistant.";
    private const string OffThinkingLevel = "off";

    private readonly object _sync = new();
    private readonly List<Func<object, CancellationToken, Task>> _listeners = [];
    private readonly List<ChatMessage> _steerQueue = [];
    private readonly List<ChatMessage> _followUpQueue = [];
    private readonly List<ChatMessage> _nextTurnQueue = [];
    private Agent? _activeAgent;
    private Task? _runTask;
    private Model _model;
    private ThinkingLevel? _thinkingLevel;
    private IReadOnlyList<IAgentTool> _tools;
    private IReadOnlyList<string> _activeToolNames;
    private AgentHarnessResources _resources;
    private string? _systemPrompt;
    private Func<AgentHarnessSystemPromptContext<TMetadata>, CancellationToken, Task<string>>? _systemPromptFactory;
    private SimpleStreamOptions? _streamOptions;
    private AgentQueueMode _steeringMode;
    private AgentQueueMode _followUpMode;
    private ToolExecutionMode _toolExecution;

    public AgentHarness(AgentHarnessOptions<TMetadata> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Session = options.Session ?? throw new ArgumentNullException(nameof(options.Session));
        ProviderRegistry = options.ProviderRegistry ?? throw new ArgumentNullException(nameof(options.ProviderRegistry));
        _model = options.Model ?? throw new ArgumentNullException(nameof(options.Model));
        _tools = options.Tools.ToArray();
        ValidateUniqueNames(_tools.Select(static tool => tool.Name), "Duplicate tool name(s)");
        _activeToolNames = options.ActiveToolNames?.ToArray() ?? _tools.Select(static tool => tool.Name).ToArray();
        ValidateToolNames(_activeToolNames, _tools);
        _resources = CloneResources(options.Resources);
        _systemPrompt = options.SystemPrompt;
        _systemPromptFactory = options.SystemPromptFactory;
        _streamOptions = options.StreamOptions;
        _thinkingLevel = options.ThinkingLevel;
        _steeringMode = options.SteeringMode;
        _followUpMode = options.FollowUpMode;
        _toolExecution = options.ToolExecution;
    }

    public AgentHarnessSession<TMetadata> Session { get; }
    public ProviderRegistry ProviderRegistry { get; }
    public AgentHarnessPhase Phase { get; private set; } = AgentHarnessPhase.Idle;

    public Model GetModel() => _model;
    public ThinkingLevel? GetThinkingLevel() => _thinkingLevel;
    public IReadOnlyList<IAgentTool> GetTools() => _tools.ToArray();
    public IReadOnlyList<IAgentTool> GetActiveTools() => ResolveActiveTools(_activeToolNames, _tools);
    public AgentHarnessResources GetResources() => CloneResources(_resources);
    public SimpleStreamOptions? GetStreamOptions() => _streamOptions;

    public IDisposable Subscribe(Action<object> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Subscribe((evt, _) =>
        {
            listener(evt);
            return Task.CompletedTask;
        });
    }

    public IDisposable Subscribe(Func<object, CancellationToken, Task> listener)
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

    public Task<AssistantMessage> PromptAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        PromptAsync(text, images: null, cancellationToken);

    public Task<AssistantMessage> PromptAsync(
        string text,
        IReadOnlyList<ImageContent>? images,
        CancellationToken cancellationToken = default) =>
        PromptAsync(CreateUserMessage(text, images), cancellationToken);

    public async Task<AssistantMessage> PromptAsync(
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowUnlessIdle("AgentHarness is busy");

        Phase = AgentHarnessPhase.Turn;
        try
        {
            var turnMessages = DrainNextTurnQueue();
            turnMessages.Add(message);
            if (turnMessages.Count > 1)
                await EmitQueueUpdateAsync(cancellationToken).ConfigureAwait(false);

            return await ExecuteTurnAsync(turnMessages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not AgentHarnessException)
        {
            throw NormalizeHarnessException(ex, "unknown");
        }
        finally
        {
            Phase = AgentHarnessPhase.Idle;
        }
    }

    public async Task<AssistantMessage> SkillAsync(
        string name,
        string? additionalInstructions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var skill = _resources.Skills?.FirstOrDefault(skill => skill.Name.Equals(name, StringComparison.Ordinal));
        if (skill is null)
            throw new AgentHarnessException("invalid_argument", $"Unknown skill: {name}");

        return await PromptAsync(
            AgentHarnessSkills.FormatSkillInvocation(skill, additionalInstructions),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistantMessage> PromptFromTemplateAsync(
        string name,
        IReadOnlyList<string>? args = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var promptTemplate = _resources.PromptTemplates?.FirstOrDefault(template =>
            template.Name.Equals(name, StringComparison.Ordinal));
        if (promptTemplate is null)
            throw new AgentHarnessException("invalid_argument", $"Unknown prompt template: {name}");

        return await PromptAsync(
            AgentHarnessPromptTemplates.FormatPromptTemplateInvocation(promptTemplate, args ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            await Session.AppendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw NormalizeHarnessException(ex, "session");
        }
    }

    public async Task<AgentCompactionResult> CompactAsync(
        string? customInstructions = null,
        AgentCompactionSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ThrowUnlessIdle("compact() requires idle harness");
        Phase = AgentHarnessPhase.Compaction;
        try
        {
            var branchEntries = await Session.GetBranchAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var preparation = AgentCompaction.PrepareCompaction(branchEntries, settings);
            if (preparation is null)
                throw new AgentHarnessException("compaction", "Nothing to compact");

            var result = await AgentCompactionSummaries.CompactAsync(
                preparation,
                CreateSummaryOptions(customInstructions, replaceInstructions: false, cancellationToken))
                .ConfigureAwait(false);
            var entryId = await Session.AppendCompactionAsync(
                result.Summary,
                result.FirstKeptEntryId,
                result.TokensBefore,
                result.Details,
                fromHook: false,
                cancellationToken).ConfigureAwait(false);

            var entry = await Session.GetEntryAsync(entryId, cancellationToken).ConfigureAwait(false);
            if (entry is CompactionSessionEntry compactionEntry)
            {
                await EmitAsync(
                    new AgentHarnessSessionCompactEvent(compactionEntry, FromHook: false),
                    cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception ex) when (ex is not AgentHarnessException)
        {
            throw NormalizeHarnessException(ex, "compaction");
        }
        finally
        {
            Phase = AgentHarnessPhase.Idle;
        }
    }

    public async Task<AgentHarnessNavigateTreeResult> NavigateTreeAsync(
        string targetId,
        bool summarize = false,
        string? customInstructions = null,
        bool replaceInstructions = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ThrowUnlessIdle("navigateTree() requires idle harness");
        Phase = AgentHarnessPhase.BranchSummary;
        try
        {
            var oldLeafId = await Session.GetLeafIdAsync(cancellationToken).ConfigureAwait(false);
            if (oldLeafId == targetId)
                return new AgentHarnessNavigateTreeResult(Cancelled: false);

            var targetEntry = await Session.GetEntryAsync(targetId, cancellationToken).ConfigureAwait(false);
            if (targetEntry is null)
                throw new AgentHarnessException("invalid_argument", $"Entry {targetId} not found");

            var collected = await AgentBranchSummaries
                .CollectEntriesForBranchSummaryAsync(Session, oldLeafId, targetId, cancellationToken)
                .ConfigureAwait(false);

            AgentBranchSummaryResult? branchSummary = null;
            if (summarize && collected.Entries.Count > 0)
            {
                branchSummary = await AgentBranchSummaries.GenerateBranchSummaryAsync(
                    collected.Entries,
                    CreateSummaryOptions(customInstructions, replaceInstructions, cancellationToken))
                    .ConfigureAwait(false);
            }

            var (newLeafId, editorText) = ResolveNavigationTarget(targetEntry);
            string? summaryId = null;
            if (branchSummary is not null)
            {
                summaryId = await Session.MoveToAsync(
                    newLeafId,
                    new SessionBranchSummary(
                        branchSummary.Summary,
                        new AgentBranchSummaryDetails(
                            branchSummary.ReadFiles,
                            branchSummary.ModifiedFiles),
                        FromHook: false),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Session.MoveToAsync(newLeafId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var summaryEntry = summaryId is null
                ? null
                : await Session.GetEntryAsync(summaryId, cancellationToken).ConfigureAwait(false) as BranchSummarySessionEntry;
            await EmitAsync(
                new AgentHarnessSessionTreeEvent(
                    await Session.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
                    oldLeafId,
                    summaryEntry,
                    FromHook: false),
                cancellationToken).ConfigureAwait(false);

            return new AgentHarnessNavigateTreeResult(
                Cancelled: false,
                editorText,
                summaryEntry);
        }
        catch (Exception ex) when (ex is not AgentHarnessException)
        {
            throw NormalizeHarnessException(ex, "branch_summary");
        }
        finally
        {
            Phase = AgentHarnessPhase.Idle;
        }
    }

    public async Task SetModelAsync(Model model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var previousModel = _model;
        _model = model;
        await Session.AppendModelChangeAsync(model.Provider, model.Id, cancellationToken).ConfigureAwait(false);
        await EmitAsync(new AgentHarnessModelUpdateEvent(model, previousModel), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetThinkingLevelAsync(ThinkingLevel? level, CancellationToken cancellationToken = default)
    {
        var previousLevel = _thinkingLevel;
        _thinkingLevel = level;
        await Session.AppendThinkingLevelChangeAsync(FormatThinkingLevel(level), cancellationToken).ConfigureAwait(false);
        await EmitAsync(new AgentHarnessThinkingLevelUpdateEvent(level, previousLevel), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetToolsAsync(
        IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<string>? activeToolNames = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ValidateUniqueNames(tools.Select(static tool => tool.Name), "Duplicate tool name(s)");
        var nextTools = tools.ToArray();
        var nextActive = activeToolNames?.ToArray() ?? _activeToolNames.ToArray();
        ValidateToolNames(nextActive, nextTools);

        var previousToolNames = _tools.Select(static tool => tool.Name).ToArray();
        var previousActive = _activeToolNames.ToArray();
        _tools = nextTools;
        _activeToolNames = nextActive;
        await Session.AppendActiveToolsChangeAsync(nextActive, cancellationToken).ConfigureAwait(false);
        await EmitAsync(
            new AgentHarnessToolsUpdateEvent(
                _tools.Select(static tool => tool.Name).ToArray(),
                previousToolNames,
                _activeToolNames.ToArray(),
                previousActive),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetActiveToolsAsync(
        IReadOnlyList<string> toolNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        ValidateToolNames(toolNames, _tools);
        var previousToolNames = _tools.Select(static tool => tool.Name).ToArray();
        var previousActive = _activeToolNames.ToArray();
        _activeToolNames = toolNames.ToArray();
        await Session.AppendActiveToolsChangeAsync(_activeToolNames, cancellationToken).ConfigureAwait(false);
        await EmitAsync(
            new AgentHarnessToolsUpdateEvent(
                previousToolNames,
                previousToolNames,
                _activeToolNames.ToArray(),
                previousActive),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetResourcesAsync(
        AgentHarnessResources resources,
        CancellationToken cancellationToken = default)
    {
        var previous = GetResources();
        _resources = CloneResources(resources);
        await EmitAsync(new AgentHarnessResourcesUpdateEvent(GetResources(), previous), cancellationToken).ConfigureAwait(false);
    }

    public Task SetStreamOptionsAsync(SimpleStreamOptions? streamOptions)
    {
        _streamOptions = streamOptions;
        return Task.CompletedTask;
    }

    public async Task SteerAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default)
    {
        ThrowUnlessTurn("Cannot steer unless a turn is running");

        var message = CreateUserMessage(text, images);
        lock (_sync)
        {
            _steerQueue.Add(message);
        }

        _activeAgent!.Steer(message);
        await EmitQueueUpdateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FollowUpAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default)
    {
        ThrowUnlessTurn("Cannot follow up unless a turn is running");

        var message = CreateUserMessage(text, images);
        lock (_sync)
        {
            _followUpQueue.Add(message);
        }

        _activeAgent!.FollowUp(message);
        await EmitQueueUpdateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task NextTurnAsync(
        string text,
        IReadOnlyList<ImageContent>? images = null,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _nextTurnQueue.Add(CreateUserMessage(text, images));
        }

        await EmitQueueUpdateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentHarnessAbortResult> AbortAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> clearedSteer;
        IReadOnlyList<ChatMessage> clearedFollowUp;
        lock (_sync)
        {
            clearedSteer = _steerQueue.ToArray();
            clearedFollowUp = _followUpQueue.ToArray();
            _steerQueue.Clear();
            _followUpQueue.Clear();
        }

        _activeAgent?.Abort();
        await WaitForIdleAsync().ConfigureAwait(false);
        await EmitAsync(new AgentHarnessSettledEvent(GetNextTurnCount()), cancellationToken).ConfigureAwait(false);
        return new AgentHarnessAbortResult(clearedSteer, clearedFollowUp);
    }

    public Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _runTask ?? Task.CompletedTask;
        }
    }

    private async Task<AssistantMessage> ExecuteTurnAsync(
        IReadOnlyList<ChatMessage> promptMessages,
        CancellationToken cancellationToken)
    {
        var context = await Session.BuildContextAsync(cancellationToken).ConfigureAwait(false);
        var activeToolNames = context.ActiveToolNames ?? _activeToolNames;
        var activeTools = ResolveActiveTools(activeToolNames, _tools);
        var systemPrompt = await ResolveSystemPromptAsync(activeTools, cancellationToken).ConfigureAwait(false);
        var streamOptions = BuildStreamOptions(context);
        var agent = new Agent(new AgentOptions
        {
            Model = _model,
            ProviderRegistry = ProviderRegistry,
            SystemPrompt = systemPrompt,
            Messages = context.Messages,
            Tools = activeTools,
            StreamOptions = streamOptions,
            ConvertToLlm = AgentHarnessMessages.ConvertToLlm,
            SteeringMode = _steeringMode,
            FollowUpMode = _followUpMode,
            ToolExecution = _toolExecution
        });

        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe(async (evt, token) =>
        {
            events.Add(evt);
            await EmitAsync(evt, token).ConfigureAwait(false);
        });

        _activeAgent = agent;
        var runTask = agent.PromptAsync(promptMessages, cancellationToken);
        lock (_sync)
        {
            _runTask = runTask;
        }

        try
        {
            await runTask.ConfigureAwait(false);
            await AppendNewMessagesAsync(context.Messages.Count, agent.State.Messages, cancellationToken).ConfigureAwait(false);
            ClearLiveQueues();
            await EmitAsync(new AgentHarnessSavePointEvent(HadPendingMutations: false), cancellationToken).ConfigureAwait(false);
            await EmitAsync(new AgentHarnessSettledEvent(GetNextTurnCount()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_runTask, runTask))
                    _runTask = null;
            }

            _activeAgent = null;
        }

        return agent.State.Messages
            .OfType<AssistantMessage>()
            .LastOrDefault()
            ?? throw new AgentHarnessException("invalid_state", "AgentHarness prompt completed without an assistant message");
    }

    private async Task AppendNewMessagesAsync(
        int existingMessageCount,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        for (var i = existingMessageCount; i < messages.Count; i++)
            await Session.AppendMessageAsync(messages[i], cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveSystemPromptAsync(
        IReadOnlyList<IAgentTool> activeTools,
        CancellationToken cancellationToken)
    {
        if (_systemPromptFactory is not null)
        {
            var generated = await _systemPromptFactory(
                new AgentHarnessSystemPromptContext<TMetadata>(
                    Session,
                    _model,
                    _thinkingLevel,
                    activeTools,
                    GetResources()),
                cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(generated) ? DefaultSystemPrompt : generated;
        }

        return string.IsNullOrWhiteSpace(_systemPrompt)
            ? DefaultSystemPrompt
            : _systemPrompt;
    }

    private SimpleStreamOptions? BuildStreamOptions(SessionContext context)
    {
        var options = _streamOptions;
        var thinkingLevel = ParseThinkingLevel(context.ThinkingLevel) ?? _thinkingLevel;
        if (thinkingLevel is { } level && _model.Reasoning)
        {
            options = options is null
                ? new SimpleStreamOptions { Reasoning = level }
                : options with { Reasoning = level };
        }

        return options;
    }

    private AgentSummaryGenerationOptions CreateSummaryOptions(
        string? customInstructions,
        bool replaceInstructions,
        CancellationToken cancellationToken) =>
        new()
        {
            ProviderRegistry = ProviderRegistry,
            Model = _model,
            CustomInstructions = string.IsNullOrWhiteSpace(customInstructions) ? null : customInstructions.Trim(),
            ReplaceInstructions = replaceInstructions,
            ThinkingLevel = _thinkingLevel,
            CancellationToken = cancellationToken
        };

    private List<ChatMessage> DrainNextTurnQueue()
    {
        lock (_sync)
        {
            if (_nextTurnQueue.Count == 0)
                return [];

            var messages = _nextTurnQueue.ToList();
            _nextTurnQueue.Clear();
            return messages;
        }
    }

    private int GetNextTurnCount()
    {
        lock (_sync)
        {
            return _nextTurnQueue.Count;
        }
    }

    private Task EmitQueueUpdateAsync(CancellationToken cancellationToken) =>
        EmitAsync(
            new AgentHarnessQueueUpdateEvent(
                SnapshotQueue(_steerQueue),
                SnapshotQueue(_followUpQueue),
                SnapshotQueue(_nextTurnQueue)),
            cancellationToken);

    private async Task EmitAsync(object evt, CancellationToken cancellationToken)
    {
        Func<object, CancellationToken, Task>[] listeners;
        lock (_sync)
        {
            listeners = _listeners.ToArray();
        }

        foreach (var listener in listeners)
            await listener(evt, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowUnlessIdle(string message)
    {
        if (Phase != AgentHarnessPhase.Idle)
            throw new AgentHarnessException("busy", message);
    }

    private void ThrowUnlessTurn(string message)
    {
        if (Phase != AgentHarnessPhase.Turn || _activeAgent is null)
            throw new AgentHarnessException("invalid_state", message);
    }

    private void ClearLiveQueues()
    {
        lock (_sync)
        {
            _steerQueue.Clear();
            _followUpQueue.Clear();
        }
    }

    private IReadOnlyList<ChatMessage> SnapshotQueue(List<ChatMessage> queue)
    {
        lock (_sync)
        {
            return queue.ToArray();
        }
    }

    private static AgentHarnessResources CloneResources(AgentHarnessResources? resources) =>
        new(
            resources?.Skills?.ToArray(),
            resources?.PromptTemplates?.ToArray());

    private static UserMessage CreateUserMessage(string text, IReadOnlyList<ImageContent>? images = null)
    {
        var content = new List<ContentBlock> { new TextContent(text) };
        if (images is { Count: > 0 })
            content.AddRange(images);

        return new UserMessage(content);
    }

    private static (string? NewLeafId, string? EditorText) ResolveNavigationTarget(SessionTreeEntry targetEntry)
    {
        switch (targetEntry)
        {
            case MessageSessionEntry { Message: UserMessage user }:
                return (targetEntry.ParentId, ReadText(user.Content));
            case CustomMessageSessionEntry custom:
                return (targetEntry.ParentId, ReadText(custom.Content));
            default:
                return (targetEntry.Id, null);
        }
    }

    private static string ReadText(IReadOnlyList<ContentBlock> content) =>
        string.Join(string.Empty, content.OfType<TextContent>().Select(static text => text.Text));

    private static IReadOnlyList<IAgentTool> ResolveActiveTools(
        IReadOnlyList<string> activeToolNames,
        IReadOnlyList<IAgentTool> tools)
    {
        ValidateToolNames(activeToolNames, tools);
        var toolMap = tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        return activeToolNames.Select(name => toolMap[name]).ToArray();
    }

    private static void ValidateToolNames(
        IReadOnlyList<string> toolNames,
        IReadOnlyList<IAgentTool> tools)
    {
        ValidateUniqueNames(toolNames, "Duplicate active tool name(s)");
        var knownTools = tools.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var missing = toolNames.Where(name => !knownTools.Contains(name)).ToArray();
        if (missing.Length > 0)
            throw new AgentHarnessException("invalid_argument", $"Unknown tool(s): {string.Join(", ", missing)}");
    }

    private static void ValidateUniqueNames(IEnumerable<string> names, string message)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!seen.Add(name))
                duplicates.Add(name);
        }

        if (duplicates.Count > 0)
            throw new AgentHarnessException("invalid_argument", $"{message}: {string.Join(", ", duplicates)}");
    }

    private static string FormatThinkingLevel(ThinkingLevel? level) =>
        level?.ToString().ToLowerInvariant() ?? OffThinkingLevel;

    private static ThinkingLevel? ParseThinkingLevel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(OffThinkingLevel, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Enum.TryParse<ThinkingLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static AgentHarnessException NormalizeHarnessException(Exception error, string fallbackCode)
    {
        if (error is AgentHarnessException harnessError)
            return harnessError;
        if (error is SessionException sessionError)
            return new AgentHarnessException("session", sessionError.Message, sessionError);
        if (error is AgentSummaryException summaryError)
            return new AgentHarnessException(fallbackCode, summaryError.Message, summaryError);

        return new AgentHarnessException(fallbackCode, error.Message, error);
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                unsubscribe();
        }
    }
}
