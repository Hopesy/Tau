using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

public enum CodingAgentSdkNoToolsMode
{
    None,
    All,
    BuiltIn
}

public sealed record CodingAgentSdkScopedModel(Model Model, string? ThinkingLevel = null);

public sealed class CodingAgentSdkCreateSessionOptions
{
    public string? Cwd { get; init; }
    public string? AgentDirectory { get; init; }
    public string? SettingsPath { get; init; }
    public string? SessionPath { get; init; }
    public bool NoSession { get; init; }
    public bool ContinueSession { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }
    public IReadOnlyList<CodingAgentSdkScopedModel>? ScopedModels { get; init; }
    public CodingAgentSdkNoToolsMode NoTools { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
    public IReadOnlyList<string>? ExcludeTools { get; init; }
    public IReadOnlyList<IAgentTool>? CustomTools { get; init; }
    public bool IncludeExtensions { get; init; } = true;
    public bool IncludeSkills { get; init; } = true;
    public bool IncludePromptTemplates { get; init; } = true;
    public bool IncludeContextFiles { get; init; } = true;
    public bool AutoResizeImages { get; init; } = true;
    public string? SystemPrompt { get; init; }
    public string? AppendSystemPrompt { get; init; }
    public string? ApiKey { get; init; }
    public ProviderRegistry? ProviderRegistry { get; init; }
    public ModelCatalog? ModelCatalog { get; init; }
    public ITauLogSink? LogSink { get; init; }
    public TauRuntimeLogContext? LogContext { get; init; }
}

public sealed class CodingAgentSdkSession
{
    internal CodingAgentSdkSession(
        RuntimeCodingAgentRunner runner,
        CodingAgentSettingsStore settingsStore,
        CodingAgentSessionStore? sessionStore,
        CodingAgentTreeSessionController? treeSessionController,
        CodingAgentPackageResourceState packageResourceState,
        CodingAgentExtensionCommandStore extensionCommandStore,
        CodingAgentExtensionStatus extensionStatus,
        CodingAgentPromptTemplateStore promptTemplateStore,
        CodingAgentSkillStore skillStore,
        CodingAgentContextFileStore contextFileStore,
        string? modelFallbackMessage)
    {
        Runner = runner;
        SettingsStore = settingsStore;
        SessionStore = sessionStore;
        TreeSessionController = treeSessionController;
        PackageResourceState = packageResourceState;
        ExtensionCommandStore = extensionCommandStore;
        ExtensionStatus = extensionStatus;
        PromptTemplateStore = promptTemplateStore;
        SkillStore = skillStore;
        ContextFileStore = contextFileStore;
        ModelFallbackMessage = modelFallbackMessage;
    }

    public RuntimeCodingAgentRunner Runner { get; }
    public CodingAgentSettingsStore SettingsStore { get; }
    public CodingAgentSessionStore? SessionStore { get; }
    public CodingAgentTreeSessionController? TreeSessionController { get; }
    public CodingAgentPackageResourceState PackageResourceState { get; }
    public CodingAgentExtensionCommandStore ExtensionCommandStore { get; }
    public CodingAgentExtensionStatus ExtensionStatus { get; }
    public CodingAgentPromptTemplateStore PromptTemplateStore { get; }
    public CodingAgentSkillStore SkillStore { get; }
    public CodingAgentContextFileStore ContextFileStore { get; }
    public string? ModelFallbackMessage { get; }

    public IReadOnlyList<ChatMessage> Messages => Runner.Messages;

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default) =>
        Runner.RunAsync(input, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ContentBlock> input,
        CancellationToken cancellationToken = default) =>
        Runner.RunAsync(input, cancellationToken);

    public void Save()
    {
        SessionStore?.Save(Runner.Messages, Runner.Model, Runner.SessionName);
        TreeSessionController?.SyncFromRunner(Runner);
    }
}

public static class CodingAgentSdk
{
    public static Task<CodingAgentSdkSession> CreateSessionAsync(
        CodingAgentSdkCreateSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CodingAgentSdkCreateSessionOptions();

        var cwd = ResolveDirectory(options.Cwd, Environment.CurrentDirectory);
        var agentDir = ResolveDirectory(options.AgentDirectory, GetDefaultAgentDirectory());
        CodingAgentMigrations.Run(new CodingAgentMigrationOptions(
            AgentDirectory: agentDir,
            Cwd: cwd,
            AuthPath: Path.Combine(agentDir, "auth.json"),
            KeybindingsPath: Path.Combine(agentDir, "coding-agent-keybindings.json"),
            BinDirectory: Path.Combine(agentDir, "bin")));

        var settingsPath = ResolvePath(options.SettingsPath, Path.Combine(cwd, ".tau", "coding-agent-settings.json"));
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        var settings = settingsStore.Load();

        var sessionPath = ResolvePath(options.SessionPath, Path.Combine(cwd, ".tau", "coding-agent-session.jsonl"));
        var treeSessionController = options.NoSession
            ? null
            : CodingAgentTreeSessionController.OpenOrCreate(sessionPath);
        var sessionStore = options.NoSession
            ? null
            : new CodingAgentSessionStore(Path.ChangeExtension(sessionPath, ".json"));
        var sessionSnapshot = options.NoSession || treeSessionController is null
            ? new CodingAgentSessionSnapshot([], null, null, null)
            : treeSessionController.LoadSnapshot().ToFlatSnapshot();
        if (!options.ContinueSession && string.IsNullOrWhiteSpace(options.SessionPath))
        {
            sessionSnapshot = new CodingAgentSessionSnapshot([], null, null, null);
        }

        var packageManager = new CodingAgentPackageManager(
            cwd,
            userSettingsPath: Path.Combine(agentDir, "coding-agent-settings.json"),
            projectSettingsPath: settingsPath);
        var packageResourceState = new CodingAgentPackageResourceState(packageManager.ResolveResources());
        var extensionCommandStore = new CodingAgentExtensionCommandStore(
            cwd,
            userExtensionsDirectory: Path.Combine(agentDir, "extensions"),
            additionalPathsProvider: () => packageResourceState.ExtensionPaths,
            includeDefaults: options.IncludeExtensions);
        var extensionStatus = extensionCommandStore.LoadStatus();
        var extensionResources = extensionStatus.Resources;

        var promptTemplateStore = new CodingAgentPromptTemplateStore(
            cwd,
            userPromptsDirectory: Path.Combine(agentDir, "prompts"),
            additionalPathsProvider: () => CombineResourcePaths(packageResourceState.PromptPaths, extensionResources.PromptPaths),
            includeDefaults: options.IncludePromptTemplates);
        var skillStore = new CodingAgentSkillStore(
            cwd,
            userSkillsDirectory: Path.Combine(agentDir, "skills"),
            additionalPathsProvider: () => CombineResourcePaths(packageResourceState.SkillPaths, extensionResources.SkillPaths),
            includeDefaults: options.IncludeSkills);
        var contextFileStore = new CodingAgentContextFileStore(
            cwd,
            userContextDirectory: agentDir,
            includeDefaults: options.IncludeContextFiles);

        var modelCatalog = options.ModelCatalog ?? new ModelCatalog();
        var (providerId, modelId, modelFallbackMessage) = ResolveModelSelection(options, settings, sessionSnapshot, modelCatalog);
        var thinkingLevel = ResolveThinkingLevel(options, settings, providerId, modelId, modelCatalog);
        var selectedBuiltInToolNames = ResolveSelectedBuiltInToolNames(options);
        var extensionTools = options.NoTools == CodingAgentSdkNoToolsMode.All
            ? []
            : MergeTools(extensionCommandStore.LoadTools(), options.CustomTools);
        var runnerTools = options.NoTools == CodingAgentSdkNoToolsMode.All && options.Tools is null
            ? []
            : RuntimeCodingAgentRunner.CreateDefaultTools(
                options.AutoResizeImages,
                extensionTools,
                selectedBuiltInToolNames);
        if (options.ExcludeTools is { Count: > 0 } excludeTools)
        {
            var excluded = excludeTools.ToHashSet(StringComparer.Ordinal);
            runnerTools = runnerTools.Where(tool => !excluded.Contains(tool.Name)).ToArray();
        }

        var runner = RuntimeCodingAgentRunner.Create(
            providerId,
            modelId,
            sessionSnapshot.Messages,
            runnerTools,
            options.SystemPrompt,
            skillStore.Load(),
            contextFileStore.Load(),
            options.LogSink,
            options.LogContext,
            options.ProviderRegistry,
            modelCatalog,
            options.AutoResizeImages,
            extensionCommandStore.LoadToolInterceptors(),
            extensionCommandStore.LoadLifecycleEventSink(),
            options.AppendSystemPrompt,
            options.ApiKey);
        runner.SessionName = sessionSnapshot.Name;
        runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(settings.SteeringMode);
        runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(settings.FollowUpMode);
        runner.ThinkingLevel = thinkingLevel;

        return Task.FromResult(new CodingAgentSdkSession(
            runner,
            settingsStore,
            sessionStore,
            treeSessionController,
            packageResourceState,
            extensionCommandStore,
            extensionStatus,
            promptTemplateStore,
            skillStore,
            contextFileStore,
            modelFallbackMessage));
    }

    private static (string? ProviderId, string? ModelId, string? FallbackMessage) ResolveModelSelection(
        CodingAgentSdkCreateSessionOptions options,
        CodingAgentSettingsSnapshot settings,
        CodingAgentSessionSnapshot sessionSnapshot,
        ModelCatalog modelCatalog)
    {
        if (!string.IsNullOrWhiteSpace(options.ProviderId) || !string.IsNullOrWhiteSpace(options.ModelId))
        {
            return (options.ProviderId, options.ModelId, null);
        }

        if (!string.IsNullOrWhiteSpace(sessionSnapshot.Provider) || !string.IsNullOrWhiteSpace(sessionSnapshot.Model))
        {
            try
            {
                var restored = modelCatalog.ResolveSelection(sessionSnapshot.Provider, sessionSnapshot.Model);
                return (restored.Provider, restored.ModelId, null);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                var fallbackMessage = $"Could not restore model {sessionSnapshot.Provider}/{sessionSnapshot.Model}";
                return (settings.DefaultProvider, settings.DefaultModel, fallbackMessage);
            }
        }

        return (settings.DefaultProvider, settings.DefaultModel, null);
    }

    private static ThinkingLevel? ResolveThinkingLevel(
        CodingAgentSdkCreateSessionOptions options,
        CodingAgentSettingsSnapshot settings,
        string? providerId,
        string? modelId,
        ModelCatalog modelCatalog)
    {
        var requested = options.ThinkingLevel ??
            CodingAgentThinkingLevels.ParseOrNull(settings.DefaultThinkingLevel) ??
            ThinkingLevel.Medium;
        try
        {
            var selection = modelCatalog.ResolveSelection(providerId, modelId);
            var model = modelCatalog.GetModel(selection.Provider, selection.ModelId);
            return CodingAgentThinkingLevels.ClampForModel(model, requested);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return requested;
        }
    }

    private static IReadOnlyList<string>? ResolveSelectedBuiltInToolNames(CodingAgentSdkCreateSessionOptions options)
    {
        var selected = options.Tools is null
            ? options.NoTools == CodingAgentSdkNoToolsMode.All || options.NoTools == CodingAgentSdkNoToolsMode.BuiltIn
                ? []
                : null
            : options.Tools.Select(NormalizeToolName).Where(static name => name is not null).Select(static name => name!).ToArray();
        return selected;
    }

    private static string? NormalizeToolName(string value)
    {
        var trimmed = value.Trim();
        return CodingAgentCliArguments.CliToolNameToTauToolName.TryGetValue(trimmed, out var mapped)
            ? mapped
            : string.IsNullOrWhiteSpace(trimmed)
                ? null
                : trimmed;
    }

    private static IReadOnlyList<IAgentTool> MergeTools(
        IReadOnlyList<IAgentTool> extensionTools,
        IReadOnlyList<IAgentTool>? customTools)
    {
        if (customTools is not { Count: > 0 })
        {
            return extensionTools;
        }

        return extensionTools.Concat(customTools).ToArray();
    }

    private static IReadOnlyList<string> CombineResourcePaths(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        return first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string GetDefaultAgentDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.CurrentDirectory, ".tau")
            : Path.Combine(home, ".tau");
    }

    private static string ResolvePath(string? path, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? fallback : path);

    private static string ResolveDirectory(string? path, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? fallback : path);
}
