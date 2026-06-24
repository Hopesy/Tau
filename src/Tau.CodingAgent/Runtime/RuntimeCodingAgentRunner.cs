using System.Runtime.CompilerServices;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

public sealed class RuntimeCodingAgentRunner : ICodingAgentRunner, ICodingAgentToolResultDetailsProvider
{
    private readonly AgentRuntime _runtime;
    private readonly ModelCatalog _modelCatalog;
    private readonly ProviderAuthResolver _authResolver;
    private readonly ITauLogSink _logSink;
    private readonly CodingAgentExtensionLifecycleEventSink? _extensionLifecycleEventSink;
    private readonly Dictionary<string, object?> _toolResultDetailsByToolCallId = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _refreshesGeneratedSystemPrompt;
    private readonly string? _appendSystemPrompt;
    private AgentLoopConfig _config;
    private IReadOnlyList<CodingAgentContextFile> _contextFiles;
    private int _activeCompactionCount;

    public RuntimeCodingAgentRunner(
        AgentRuntime runtime,
        AgentLoopConfig config,
        ModelCatalog modelCatalog,
        ProviderAuthResolver? authResolver = null,
        ITauLogSink? logSink = null,
        TauRuntimeLogContext? logContext = null,
        bool refreshesGeneratedSystemPrompt = false,
        IReadOnlyList<CodingAgentContextFile>? contextFiles = null,
        CodingAgentExtensionLifecycleEventSink? extensionLifecycleEventSink = null,
        string? appendSystemPrompt = null)
    {
        _runtime = runtime;
        var effectiveLogSink = logSink ?? config.LogSink;
        _config = config with
        {
            LogSink = effectiveLogSink,
            LogContext = logContext ?? config.LogContext
        };
        _modelCatalog = modelCatalog;
        _authResolver = authResolver ?? new ProviderAuthResolver(logSink: effectiveLogSink);
        _logSink = effectiveLogSink;
        _extensionLifecycleEventSink = extensionLifecycleEventSink;
        _refreshesGeneratedSystemPrompt = refreshesGeneratedSystemPrompt;
        _contextFiles = contextFiles ?? [];
        _appendSystemPrompt = string.IsNullOrWhiteSpace(appendSystemPrompt) ? null : appendSystemPrompt;
    }

    public IReadOnlyList<ChatMessage> Messages => _runtime.State.Messages;
    public Model Model => _config.Model;
    public string? SessionName { get; set; }
    public IReadOnlyDictionary<string, object?> ToolResultDetailsByToolCallId => _toolResultDetailsByToolCallId;
    public int PendingMessageCount => _runtime.PendingMessageCount;
    public bool IsCompacting => Volatile.Read(ref _activeCompactionCount) > 0;

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

    public ThinkingLevel? ThinkingLevel
    {
        get => _config.StreamOptions?.Reasoning;
        set => _config = _config with
        {
            StreamOptions = (_config.StreamOptions ?? new SimpleStreamOptions()) with { Reasoning = value }
        };
    }

    public IReadOnlyList<string> GetProviders() => _modelCatalog.GetProviders();

    public IReadOnlyList<Model> GetModels(string provider) => _modelCatalog.GetModels(provider);

    public Model SelectModel(string? providerId, string? modelId)
    {
        var selection = _modelCatalog.ResolveSelection(providerId, modelId, defaultProvider: _config.Model.Provider);
        var model = _modelCatalog.GetModel(selection.Provider, selection.ModelId);
        _config = _config with { Model = model };
        return model;
    }

    public ProviderAuthStatus GetAuthStatus(string? providerId = null)
    {
        var provider = string.IsNullOrWhiteSpace(providerId) ? _config.Model.Provider : providerId;
        var model = provider.Equals(_config.Model.Provider, StringComparison.OrdinalIgnoreCase)
            ? _config.Model
            : _modelCatalog.GetModels(provider).FirstOrDefault();
        return _authResolver.GetStatus(provider, model);
    }

    public IOAuthProvider? GetOAuthProvider(string providerId) =>
        _authResolver.GetOAuthProvider(providerId);

    public void SaveOAuthCredentials(string providerId, OAuthCredentials credentials) =>
        _authResolver.SaveOAuthCredentials(providerId, credentials);

    public bool Logout(string providerId) =>
        _authResolver.Logout(providerId);

    public bool RefreshSkills(IReadOnlyList<CodingAgentSkill> skills)
    {
        return RefreshSystemPromptResources(skills, _contextFiles);
    }

    public bool RefreshSystemPromptResources(
        IReadOnlyList<CodingAgentSkill> skills,
        IReadOnlyList<CodingAgentContextFile> contextFiles)
    {
        if (!_refreshesGeneratedSystemPrompt)
        {
            return false;
        }

        _contextFiles = contextFiles;
        _config = _config with { SystemPrompt = BuildSystemPrompt(_config.Tools, skills, _contextFiles, _appendSystemPrompt) };
        return true;
    }

    public CodingAgentSessionStats GetSessionStats(string? sessionFile = null)
    {
        var messages = Messages;
        return new CodingAgentSessionStats(
            _config.Model.Provider,
            _config.Model.Id,
            messages.Count,
            messages.OfType<UserMessage>().Count(),
            messages.OfType<AssistantMessage>().Count(),
            messages.OfType<ToolResultMessage>().Count(),
            messages.OfType<AssistantMessage>().Sum(message => message.Content.OfType<ToolCallContent>().Count()),
            CodingAgentTokenEstimator.Estimate(messages),
            _config.Model.ContextWindow,
            SessionName,
            sessionFile)
            .WithUsage(CodingAgentSessionUsageSummary.FromMessages(messages));
    }

    public async Task<CodingAgentCompactionResult> CompactAsync(
        string? customInstructions = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activeCompactionCount);
        try
        {
            if (Messages.Count == 0)
            {
                throw new InvalidOperationException("Nothing to compact (session is empty)");
            }

            if (Messages.Count == 1 && IsCompactionSummaryMessage(Messages[0]))
            {
                throw new InvalidOperationException("Already compacted");
            }

            if (Messages.Count < 2)
            {
                throw new InvalidOperationException("Nothing to compact (session too small)");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var tokensBefore = CodingAgentTokenEstimator.Estimate(Messages);
            var previousSummary = ExtractPreviousSummary(Messages);
            var messagesToSummarize = previousSummary is null
                ? Messages
                : Messages.Skip(1).ToArray();
            var summaryOptions = CreateSummaryGenerationOptions(
                customInstructions,
                replaceInstructions: false,
                Math.Min(_config.StreamOptions?.MaxTokens ?? 16_384, 4_096),
                cancellationToken);
            var summary = previousSummary is null
                ? await AgentCompactionSummaries
                    .GenerateSummaryAsync(messagesToSummarize, summaryOptions)
                    .ConfigureAwait(false)
                : await AgentCompactionSummaries
                    .GenerateUpdatedSummaryAsync(messagesToSummarize, previousSummary, summaryOptions)
                    .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new InvalidOperationException("Compaction produced no text summary");
            }

            var fileDetails = AgentCompaction.ComputeFileLists(CollectFileOperations(messagesToSummarize));
            summary += AgentCompaction.FormatFileOperations(fileDetails.ReadFiles, fileDetails.ModifiedFiles);

            var messagesBefore = Messages.Count;
            _runtime.Reset();
            _runtime.AddMessage(CodingAgentCompactionMessages.CreateSummaryMessage(summary));

            return new CodingAgentCompactionResult(summary, messagesBefore, Messages.Count, tokensBefore);
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
        Interlocked.Increment(ref _activeCompactionCount);
        try
        {
            if (messages.Count == 0)
            {
                throw new InvalidOperationException("No branch content to summarize");
            }

            var tokensBefore = CodingAgentTokenEstimator.Estimate(messages);
            var branchSummary = await AgentBranchSummaries.GenerateBranchSummaryAsync(
                ToSessionEntries(messages),
                CreateSummaryGenerationOptions(
                    customInstructions,
                    replaceInstructions,
                    Math.Min(_config.StreamOptions?.MaxTokens ?? 16_384, 2_048),
                    cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var summary = branchSummary.Summary;
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new InvalidOperationException("Branch summarization produced no text summary");
            }

            return new CodingAgentBranchSummaryResult(
                summary,
                messages.Count,
                tokensBefore,
                branchSummary.ReadFiles,
                branchSummary.ModifiedFiles);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCompactionCount);
        }
    }

    private AgentSummaryGenerationOptions CreateSummaryGenerationOptions(
        string? customInstructions,
        bool replaceInstructions,
        int maxTokens,
        CancellationToken cancellationToken) =>
        new()
        {
            ProviderRegistry = _config.ProviderRegistry,
            Model = _config.Model,
            CustomInstructions = string.IsNullOrWhiteSpace(customInstructions) ? null : customInstructions.Trim(),
            ReplaceInstructions = replaceInstructions,
            MaxTokens = maxTokens,
            ThinkingLevel = _config.StreamOptions?.Reasoning,
            CancellationToken = cancellationToken
        };

    private static AgentFileOperations CollectFileOperations(IEnumerable<ChatMessage> messages)
    {
        var fileOperations = AgentCompaction.CreateFileOperations();
        foreach (var message in messages)
            AgentCompaction.ExtractFileOperationsFromMessage(message, fileOperations);

        return fileOperations;
    }

    private static IReadOnlyList<SessionTreeEntry> ToSessionEntries(IReadOnlyList<ChatMessage> messages)
    {
        var entries = new SessionTreeEntry[messages.Count];
        string? parentId = null;
        var timestamp = DateTimeOffset.UtcNow;
        for (var i = 0; i < messages.Count; i++)
        {
            var id = $"message-{i}";
            entries[i] = new MessageSessionEntry(id, parentId, timestamp, messages[i]);
            parentId = id;
        }

        return entries;
    }

    private static string? ExtractPreviousSummary(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) return null;
        var first = messages[0];
        if (!CodingAgentCompactionMessages.IsSummaryMessage(first)) return null;

        var text = ((TextContent)((UserMessage)first).Content[0]).Text;
        var startIndex = text.IndexOf(CodingAgentCompactionMessages.SummaryPrefix, StringComparison.Ordinal);
        if (startIndex < 0) return null;

        var contentStart = startIndex + CodingAgentCompactionMessages.SummaryPrefix.Length;
        var endIndex = text.IndexOf(CodingAgentCompactionMessages.SummarySuffix, contentStart, StringComparison.Ordinal);
        if (endIndex < 0) return text[contentStart..].Trim();

        return text[contentStart..endIndex].Trim();
    }

    public void ResetSession()
    {
        _runtime.Reset();
        _toolResultDetailsByToolCallId.Clear();
        SessionName = null;
    }

    public void Steer(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        _runtime.Steer(new UserMessage(input));
    }

    public void Steer(IReadOnlyList<ContentBlock> input)
    {
        if (input.Count == 0)
        {
            return;
        }

        _runtime.Steer(new UserMessage(input));
    }

    public void FollowUp(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        _runtime.FollowUp(new UserMessage(input));
    }

    public void FollowUp(IReadOnlyList<ContentBlock> input)
    {
        if (input.Count == 0)
        {
            return;
        }

        _runtime.FollowUp(new UserMessage(input));
    }

    public void RestoreSession(CodingAgentSessionSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Provider) || !string.IsNullOrWhiteSpace(snapshot.Model))
        {
            SelectModel(snapshot.Provider, snapshot.Model);
        }

        _runtime.Reset();
        _toolResultDetailsByToolCallId.Clear();
        foreach (var message in snapshot.Messages)
        {
            _runtime.AddMessage(message);
        }

        SessionName = snapshot.Name;
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        string input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        _runtime.AddMessage(new UserMessage(input));
        var runLogContext = CreateRunLogContext(logContext);
        return InstrumentRun(
            _runtime.RunAsync(_config with { LogContext = runLogContext }, cancellationToken),
            inputBytes: input?.Length ?? 0,
            runLogContext,
            cancellationToken);
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
        if (input.Count == 0)
        {
            throw new ArgumentException("Input content must not be empty.", nameof(input));
        }

        _runtime.AddMessage(new UserMessage(input));
        var sizeHint = input.OfType<TextContent>().Sum(static block => block.Text.Length);
        var runLogContext = CreateRunLogContext(logContext);
        return InstrumentRun(
            _runtime.RunAsync(_config with { LogContext = runLogContext }, cancellationToken),
            inputBytes: sizeHint,
            runLogContext,
            cancellationToken);
    }

    private TauRuntimeLogContext CreateRunLogContext(TauRuntimeLogContext? logContext = null)
    {
        var baseContext = _config.LogContext;
        var effectiveContext = logContext is null || baseContext is null
            ? logContext ?? baseContext
            : new TauRuntimeLogContext(
                FirstNonWhiteSpace(logContext.CorrelationId, baseContext.CorrelationId),
                FirstNonWhiteSpace(logContext.SessionId, baseContext.SessionId),
                FirstNonWhiteSpace(logContext.MessageId, baseContext.MessageId));
        return (effectiveContext ?? new TauRuntimeLogContext()).EnsureCorrelationId();
    }

    private static string? FirstNonWhiteSpace(string? primary, string? fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    private async IAsyncEnumerable<AgentEvent> InstrumentRun(
        IAsyncEnumerable<AgentEvent> inner,
        int inputBytes,
        TauRuntimeLogContext logContext,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _logSink.Log(new TauLogEvent(
            "agent",
            "run.start",
            startedAt,
            new Dictionary<string, string?>
            {
                ["provider"] = _config.Model.Provider,
                ["model"] = _config.Model.Id,
                ["inputBytes"] = inputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }.WithLogContext(logContext)));

        var hadError = false;
        var hadCancel = false;
        await using var enumerator = inner.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    hadCancel = true;
                    throw;
                }
                catch (Exception ex)
                {
                    hadError = true;
                    LogRunError(ex, logContext);
                    throw;
                }

                if (!hasNext) break;
                if (enumerator.Current is ToolExecutionEndEvent toolEnd)
                {
                    CaptureToolResultDetails(toolEnd);
                }

                if (_extensionLifecycleEventSink is not null)
                {
                    try
                    {
                        var extensionErrors = await _extensionLifecycleEventSink
                            .PublishAsync(enumerator.Current, cancellationToken)
                            .ConfigureAwait(false);
                        foreach (var extensionError in extensionErrors)
                        {
                            LogExtensionEventError(extensionError, logContext);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        hadCancel = true;
                        throw;
                    }
                    catch (Exception ex)
                    {
                        hadError = true;
                        LogRunError(ex, logContext);
                        throw;
                    }
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            if (!hadError)
            {
                var endedAt = DateTimeOffset.UtcNow;
                _logSink.Log(new TauLogEvent(
                    "agent",
                    hadCancel ? "run.cancel" : "run.end",
                    endedAt,
                    new Dictionary<string, string?>
                    {
                        ["provider"] = _config.Model.Provider,
                        ["model"] = _config.Model.Id,
                        ["elapsedMs"] = ((long)(endedAt - startedAt).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }.WithLogContext(logContext)));
            }
        }
    }

    private void LogExtensionEventError(
        CodingAgentExtensionLifecycleEventError error,
        TauRuntimeLogContext logContext)
    {
        _logSink.Log(new TauLogEvent(
            "extension",
            "event.error",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["provider"] = _config.Model.Provider,
                ["model"] = _config.Model.Id,
                ["eventType"] = error.EventType,
                ["path"] = error.FilePath,
                ["scope"] = error.Scope,
                ["runtime"] = error.Runtime,
                ["error"] = error.Error
            }.WithLogContext(logContext)));
    }

    private void LogRunError(Exception ex, TauRuntimeLogContext logContext)
    {
        _logSink.Log(new TauLogEvent(
            "agent",
            "run.error",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["provider"] = _config.Model.Provider,
                ["model"] = _config.Model.Id,
                ["error"] = ex.GetType().Name,
                ["message"] = ex.Message
            }.WithLogContext(logContext)));
    }

    private void CaptureToolResultDetails(ToolExecutionEndEvent toolEnd)
    {
        if (string.IsNullOrWhiteSpace(toolEnd.ToolCallId))
        {
            return;
        }

        if (toolEnd.Result.Details is null)
        {
            _toolResultDetailsByToolCallId.Remove(toolEnd.ToolCallId);
            return;
        }

        _toolResultDetailsByToolCallId[toolEnd.ToolCallId] = toolEnd.Result.Details;
    }

    public static RuntimeCodingAgentRunner CreateDefault()
    {
        return Create();
    }

    public static RuntimeCodingAgentRunner Create(
        string? providerId = null,
        string? modelId = null,
        IReadOnlyList<ChatMessage>? initialMessages = null,
        IReadOnlyList<IAgentTool>? toolsOverride = null,
        string? systemPromptOverride = null,
        IReadOnlyList<CodingAgentSkill>? skills = null,
        IReadOnlyList<CodingAgentContextFile>? contextFiles = null,
        ITauLogSink? logSink = null,
        TauRuntimeLogContext? logContext = null,
        ProviderRegistry? providerRegistryOverride = null,
        ModelCatalog? modelCatalogOverride = null,
        bool autoResizeImages = true,
        IReadOnlyList<IToolInterceptor>? interceptors = null,
        CodingAgentExtensionLifecycleEventSink? extensionLifecycleEventSink = null,
        string? appendSystemPrompt = null,
        string? apiKey = null)
    {
        var registry = providerRegistryOverride ?? new ProviderRegistry();
        if (providerRegistryOverride is null)
        {
            BuiltInProviders.RegisterAll(registry);
        }

        var modelCatalog = modelCatalogOverride ?? new ModelCatalog();
        var selection = modelCatalog.ResolveSelection(
            providerId,
            modelId,
            defaultProvider: Environment.GetEnvironmentVariable("TAU_PROVIDER"));

        var model = modelCatalog.GetModel(selection.Provider, selection.ModelId);
        var tools = toolsOverride ?? CreateDefaultTools(autoResizeImages);
        var config = new AgentLoopConfig
        {
            Model = model,
            ProviderRegistry = registry,
            Tools = tools,
            Interceptors = interceptors ?? [],
            LogSink = logSink ?? NullTauLogSink.Instance,
            LogContext = logContext,
            SystemPrompt = string.IsNullOrWhiteSpace(systemPromptOverride)
                ? BuildSystemPrompt(tools, skills ?? [], contextFiles ?? [], appendSystemPrompt)
                : AppendToSystemPrompt(systemPromptOverride, appendSystemPrompt),
            StreamOptions = new SimpleStreamOptions
            {
                MaxTokens = 16_384,
                ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey
            }
        };

        var runtime = new AgentRuntime();
        if (initialMessages is not null)
        {
            foreach (var message in initialMessages)
            {
                runtime.AddMessage(message);
            }
        }

        return new RuntimeCodingAgentRunner(
            runtime,
            config,
            modelCatalog,
            logSink: logSink,
            logContext: logContext,
            refreshesGeneratedSystemPrompt: string.IsNullOrWhiteSpace(systemPromptOverride),
            contextFiles: contextFiles ?? [],
            extensionLifecycleEventSink: extensionLifecycleEventSink,
            appendSystemPrompt: appendSystemPrompt);
    }

    public static string GetDefaultProviderId() => ModelCatalog.GetDefaultProviderId();

    public static string GetDefaultModelId(string providerId)
    {
        return ModelCatalog.GetDefaultModelId(providerId);
    }

    public static IAgentTool[] CreateDefaultTools(
        bool autoResizeImages = true,
        IReadOnlyList<IAgentTool>? extensionTools = null,
        IReadOnlyList<string>? selectedBuiltInToolNames = null)
    {
        IAgentTool[] allBuiltInTools =
        [
            new ReadFileTool(autoResizeImages),
            new WriteFileTool(),
            new EditFileTool(),
            new ShellTool(),
            new GlobTool(),
            new GrepTool(),
            new ListDirectoryTool()
        ];

        // A null selection keeps Tau's full default tool set. An explicit (possibly empty) selection
        // mirrors upstream `--tools` / `--no-tools`, which only enables the named built-ins; extension
        // tools always load regardless, matching upstream `createAgentSession` behavior.
        IAgentTool[] builtInTools = selectedBuiltInToolNames is null
            ? allBuiltInTools
            : allBuiltInTools
                .Where(tool => selectedBuiltInToolNames.Contains(tool.Name, StringComparer.Ordinal))
                .ToArray();

        if (extensionTools is not { Count: > 0 })
        {
            return builtInTools;
        }

        var toolsByName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in builtInTools)
        {
            toolsByName[tool.Name] = tool;
        }

        foreach (var tool in extensionTools)
        {
            if (!string.IsNullOrWhiteSpace(tool.Name))
            {
                toolsByName[tool.Name] = tool;
            }
        }

        return toolsByName.Values.ToArray();
    }

    private static string BuildSystemPrompt(
        IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<CodingAgentSkill> skills,
        IReadOnlyList<CodingAgentContextFile> contextFiles,
        string? appendSystemPrompt = null)
    {
        var cwd = Directory.GetCurrentDirectory().Replace('\\', '/');
        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var toolsList = string.Join(", ", tools.Select(t => t.Name));

        var prompt = $"""
            You are Tau, an expert coding assistant. You help users by reading files, executing commands, editing code, and writing new files.

            Available tools: {toolsList}

            Guidelines:
            - Prefer grep/find/ls tools over bash for file exploration when available
            - Be concise in your responses
            - Show file paths clearly when working with files
            - Read relevant code before making changes
            - Run tests after making changes to verify correctness
            - Use tools to explore the codebase rather than guessing

            Current date: {date}
            Current working directory: {cwd}
            Platform: {Environment.OSVersion}
            """;
        return AppendToSystemPrompt(prompt, appendSystemPrompt)
               + CodingAgentContextFileStore.FormatForSystemPrompt(contextFiles)
               + CodingAgentSkillStore.FormatForSystemPrompt(skills);
    }

    /// <summary>
    /// Appends <paramref name="appendSystemPrompt"/> to <paramref name="basePrompt"/> separated by a
    /// blank line, mirroring upstream <c>buildSystemPrompt</c>'s <c>appendSection</c> (<c>\n\n</c>).
    /// </summary>
    internal static string AppendToSystemPrompt(string basePrompt, string? appendSystemPrompt)
    {
        return string.IsNullOrWhiteSpace(appendSystemPrompt)
            ? basePrompt
            : basePrompt + "\n\n" + appendSystemPrompt;
    }

    private static bool IsCompactionSummaryMessage(ChatMessage message)
    {
        return CodingAgentCompactionMessages.IsSummaryMessage(message);
    }
}

file static class RuntimeLogFieldExtensions
{
    public static Dictionary<string, string?> WithLogContext(
        this Dictionary<string, string?> fields,
        TauRuntimeLogContext logContext)
    {
        logContext.AddTo(fields);
        return fields;
    }
}
