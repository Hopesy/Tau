using Tau.Ai.Observability;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

var packageManager = new CodingAgentPackageManager();
if (CodingAgentPackageCli.TryHandle(args, Console.Out, Console.Error, packageManager, out var packageCommandExitCode))
{
    return packageCommandExitCode;
}

CodingAgentCliArguments cli;
try
{
    cli = CodingAgentCliArguments.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var printMode = cli.PrintMode;
var rpcMode = cli.RpcMode;
var noContextFiles = cli.NoContextFiles;
var noThemes = cli.NoThemes;
var explicitThemePaths = cli.ThemePaths;
if (rpcMode && cli.FileArguments.Count > 0)
{
    Console.Error.WriteLine("error: @file arguments are not supported in RPC mode");
    return 1;
}

var stdinContent = await TryReadRedirectedStdinAsync(rpcMode).ConfigureAwait(false);
if (!rpcMode && !printMode && stdinContent is not null)
{
    printMode = true;
}

var keyReader = new SystemConsoleKeyReader();
var useCompositionUi = ShouldUseCompositionUi(printMode, rpcMode);
var compositionSession = useCompositionUi
    ? new TuiCompositionSession(TuiAnsiRenderSurface.ForConsole(), keyReader)
    : null;
ITerminal terminal = compositionSession is null ? new SystemConsoleTerminal() : new TuiPassiveTerminal();
var editor = !printMode && !rpcMode ? CreateInteractiveEditorIfAttached(keyReader, compositionSession, useCompositionUi) : null;
var ui = new InteractiveConsoleSession(
    terminal,
    editor,
    clearScreenAction: compositionSession is null
        ? null
        : () =>
        {
            compositionSession.TranscriptHost.ResetFrame();
            compositionSession.Render(force: true);
        });

JsonlTauLogSink? logSink;
try
{
    logSink = JsonlTauLogSink.FromEnvironment();
}
catch
{
    // A misconfigured log path must not prevent the CLI from starting.
    logSink = null;
}

static bool ShouldUseCompositionUi(bool printMode, bool rpcMode) =>
    !printMode &&
    !rpcMode &&
    !Console.IsInputRedirected &&
    !Console.IsOutputRedirected &&
    !string.Equals(Environment.GetEnvironmentVariable("TAU_CODING_AGENT_DISABLE_INPUT_EDITOR"), "1", StringComparison.Ordinal);

static async Task<string?> TryReadRedirectedStdinAsync(bool rpcMode)
{
    if (rpcMode || !Console.IsInputRedirected)
    {
        return null;
    }

    var content = await Console.In.ReadToEndAsync().ConfigureAwait(false);
    var trimmed = content.Trim();
    return string.IsNullOrEmpty(trimmed) ? null : trimmed;
}

static InteractiveInputEditor? CreateInteractiveEditorIfAttached(
    IConsoleKeyReader keyReader,
    TuiCompositionSession? compositionSession,
    bool useCompositionUi)
{
    if (!useCompositionUi)
    {
        return null;
    }

    var historyPath = ResolveHistoryPath();
    var history = historyPath is not null
        ? new InputHistory(new FileInputHistoryStore(historyPath))
        : new InputHistory();

    var bindings = KeyBindingFileStore.LoadOrDefault(ResolveKeyBindingsPath());

    return new InteractiveInputEditor(
        keyReader,
        compositionSession is null
            ? new SystemConsoleInteractiveRenderer()
            : new TuiCompositionInteractiveRenderer(compositionSession),
        history: history,
        bindings: bindings);
}

static string? ResolveHistoryPath()
{
    var explicitPath = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_HISTORY_FILE");
    if (!string.IsNullOrWhiteSpace(explicitPath))
    {
        return explicitPath;
    }

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".tau", "coding-agent-history");
}

static string? ResolveKeyBindingsPath()
{
    var explicitPath = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_KEYBINDINGS_FILE");
    if (!string.IsNullOrWhiteSpace(explicitPath))
    {
        return explicitPath;
    }

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".tau", "coding-agent-keybindings.json");
}

static IReadOnlyList<string> CombineResourcePaths(
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

static Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>> CreateTreeNavigator(
    IConsoleKeyReader keyReader,
    CodingAgentSettingsStore settingsStore,
    CodingAgentTreeSessionController treeSessionController,
    TuiCompositionSession? compositionSession)
{
    var navigator = new CodingAgentTreeInteractiveNavigator();
    Action<IReadOnlySet<string>> saveFoldedEntryIds = foldedEntryIds =>
    {
        var normalized = foldedEntryIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var current = settingsStore.Load();
        settingsStore.Save(current with { TreeCollapsedEntryIds = normalized.Length == 0 ? null : normalized });
    };

    return async (items, initialSelectedEntryId, cancellationToken) =>
    {
        var initialFoldedEntryIds =
            treeSessionController.LoadTreeFoldState()?.CollapsedEntryIds ?? settingsStore.Load().TreeCollapsedEntryIds;

        if (compositionSession is not null)
        {
            return await CodingAgentTreeCompositionNavigator.RunAsync(
                items,
                compositionSession,
                initialFoldedEntryIds,
                saveFoldedEntryIds,
                entryId => treeSessionController.GetMetadataSnapshot(entryId),
                initialSelectedEntryId,
                cancellationToken).ConfigureAwait(false);
        }

        return await navigator.NavigateAsync(
            items,
            keyReader,
            Console.Out,
            clearScreen: () => Console.Write("[2J[H"),
            initialFoldedEntryIds: initialFoldedEntryIds,
            initialSelectedEntryId: initialSelectedEntryId,
            foldedEntryIdsChanged: saveFoldedEntryIds,
            cancellationToken).ConfigureAwait(false);
    };
}
var sessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
var sessionStore = CodingAgentTreeSessionStore.IsJsonlPath(sessionFile)
    ? null
    : new CodingAgentSessionStore();
var treeSessionController = CodingAgentTreeSessionController.OpenOrCreate();
var settingsStore = new CodingAgentSettingsStore();
var packageResourceState = new CodingAgentPackageResourceState(packageManager.ResolveResources());
var extensionCommandStore = new CodingAgentExtensionCommandStore(
    additionalPathsProvider: () => packageResourceState.ExtensionPaths);
if (cli.ExtensionFlags.Count > 0)
{
    var hasExtensionFlagErrors = false;
    foreach (var diagnostic in extensionCommandStore.ApplyExtensionFlagValues(cli.ExtensionFlags))
    {
        if (diagnostic.Severity.Equals("error", StringComparison.Ordinal))
        {
            hasExtensionFlagErrors = true;
            Console.Error.WriteLine($"error: {diagnostic.Message}");
        }
        else
        {
            Console.Error.WriteLine($"warning: {diagnostic.Message}");
        }
    }

    if (hasExtensionFlagErrors)
    {
        return 1;
    }
}
var extensionResourceState = new CodingAgentExtensionResourceState(extensionCommandStore.LoadResources());
var promptTemplateStore = new CodingAgentPromptTemplateStore(
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.PromptPaths, extensionResourceState.PromptPaths));
var skillStore = new CodingAgentSkillStore(
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.SkillPaths, extensionResourceState.SkillPaths));
var contextFileStore = new CodingAgentContextFileStore(includeDefaults: !noContextFiles);
var themeStore = new CodingAgentThemeStore(
    explicitPaths: explicitThemePaths.Count == 0 ? null : explicitThemePaths,
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.ThemePaths, extensionResourceState.ThemePaths),
    includeDefaults: !noThemes);
editor?.SetAutocompleteProvider(CodingAgentAutocompleteProviderFactory.Create(
    promptTemplateStore,
    skillStore,
    extensionCommandStore));
var flatSession = sessionStore?.Load() ?? new CodingAgentSessionSnapshot([], null, null, null);
var treeSession = treeSessionController.LoadSnapshot();
var session = CodingAgentTreeSessionStore.HasExplicitTreeSessionPath || treeSession.Messages.Count > 0
    ? treeSession.ToFlatSnapshot()
    : flatSession;
var settings = settingsStore.Load();
var changelogStore = new CodingAgentChangelogStore();
CodingAgentInitialPrompt? initialPrompt = null;
try
{
    initialPrompt = await CodingAgentInitialMessageBuilder.BuildAsync(
            cli.Messages,
            cli.FileArguments,
            stdinContent,
            options: new CodingAgentInitialMessageOptions(
                AutoResizeImages: settings.ImagesAutoResize ?? true,
                BlockImages: settings.ImagesBlockImages ?? false),
            cancellationToken: CancellationToken.None)
        .ConfigureAwait(false);
}
catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

var providerId = cli.Provider ?? Environment.GetEnvironmentVariable("TAU_PROVIDER") ?? session.Provider ?? settings.DefaultProvider;
var modelId = cli.Model ?? Environment.GetEnvironmentVariable("TAU_MODEL")
              ?? (string.Equals(providerId, session.Provider, StringComparison.OrdinalIgnoreCase) ? session.Model : null)
              ?? (string.Equals(providerId, settings.DefaultProvider, StringComparison.OrdinalIgnoreCase) ? settings.DefaultModel : null);
var runnerTools = RuntimeCodingAgentRunner.CreateDefaultTools(
    settings.ImagesAutoResize ?? true,
    extensionCommandStore.LoadTools());
var runnerInterceptors = extensionCommandStore.LoadToolInterceptors();
var runnerExtensionLifecycleEvents = extensionCommandStore.LoadLifecycleEventSink();
var runner = RuntimeCodingAgentRunner.Create(
    providerId,
    modelId,
    session.Messages,
    toolsOverride: runnerTools,
    systemPromptOverride: cli.SystemPrompt,
    skills: skillStore.Load(),
    contextFiles: contextFileStore.Load(),
    logSink: logSink,
    autoResizeImages: settings.ImagesAutoResize ?? true,
    interceptors: runnerInterceptors,
    extensionLifecycleEventSink: runnerExtensionLifecycleEvents);
runner.SessionName = session.Name;
runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(settings.SteeringMode);
runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(settings.FollowUpMode);
var autoCompaction = CodingAgentAutoCompactionOptions.FromEnvironment();
if (!string.IsNullOrWhiteSpace(settings.DefaultThinkingLevel))
{
    runner.ThinkingLevel = CodingAgentThinkingLevels.ClampForModel(
        runner.Model,
        CodingAgentThinkingLevels.ParseOrNull(settings.DefaultThinkingLevel));
}
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (printMode)
{
    var printRunner = new CodingAgentPrintMode(runner, Console.Out, Console.Error);
    if (initialPrompt is null)
    {
        Console.Error.WriteLine("error: --print requires a prompt, stdin, or @file argument");
        return 1;
    }

    return await printRunner.RunAsync(initialPrompt, cts.Token).ConfigureAwait(false);
}

if (rpcMode)
{
    var rpcHost = new CodingAgentRpcHost(
        runner,
        Console.In,
        Console.Out,
        sessionStore: sessionStore,
        settingsStore: settingsStore,
        treeSessionController: treeSessionController,
        autoCompaction: autoCompaction,
        retryOptions: CodingAgentRetryOptions.FromSettingsOrEnvironment(settings),
        promptTemplateStore: promptTemplateStore,
        skillStore: skillStore,
        extensionCommandStore: extensionCommandStore);
    return await rpcHost.RunAsync(cts.Token).ConfigureAwait(false);
}

var host = new CodingAgentHost(
    ui,
    runner,
    sessionStore,
    settingsStore,
    treeSessionController: treeSessionController,
    promptTemplateStore: promptTemplateStore,
    skillStore: skillStore,
    contextFileStore: contextFileStore,
    themeStore: themeStore,
    extensionCommandStore: extensionCommandStore,
    packageManager: packageManager,
    packageResourceState: packageResourceState,
    changelogStore: changelogStore,
    autoCompaction: autoCompaction,
    autoCompactionEnabled: settings.AutoCompactionEnabled,
    retryOptions: CodingAgentRetryOptions.FromSettingsOrEnvironment(settings),
    turnInputSource: editor is null
        ? null
        : compositionSession is null
            ? new SystemConsoleCodingAgentTurnInputSource()
            : new CompositionCodingAgentTurnInputSource(keyReader, compositionSession, editor.KeyBindings),
    historySnapshotProvider: editor is null ? null : limit => editor.History.Snapshot(limit),
    treeNavigator: editor is null ? null : CreateTreeNavigator(keyReader, settingsStore, treeSessionController, compositionSession),
    themeSelector: editor is null
        ? null
        : compositionSession is null
            ? CodingAgentThemeSelector.CreateConsoleSelector(keyReader)
            : CodingAgentThemeSelector.CreateCompositionSelector(compositionSession),
    settingsSelector: editor is null
        ? null
        : compositionSession is null
            ? CodingAgentSettingsSelector.CreateConsoleSelector(keyReader)
            : CodingAgentSettingsSelector.CreateCompositionSelector(compositionSession),
    scopedModelsSelector: editor is null
        ? null
        : compositionSession is null
            ? CodingAgentScopedModelsSelector.CreateConsoleSelector(keyReader)
            : CodingAgentScopedModelsSelector.CreateCompositionSelector(compositionSession),
    authSelector: editor is null
        ? null
        : compositionSession is null
            ? CodingAgentAuthSelector.CreateConsoleSelector(keyReader)
            : CodingAgentAuthSelector.CreateCompositionSelector(compositionSession),
    thinkingSelector: editor is null
        ? null
        : compositionSession is null
            ? CodingAgentThinkingSelector.CreateConsoleSelector(keyReader)
            : CodingAgentThinkingSelector.CreateCompositionSelector(compositionSession),
    modelSelector: editor is null
        ? null
        : compositionSession is null
            ? CodingAgentModelSelector.CreateConsoleSelector(keyReader)
            : CodingAgentModelSelector.CreateCompositionSelector(compositionSession),
    resumeSelector: editor is null
        ? null
        : compositionSession is null
            ? CodingAgentResumeSelector.CreateConsoleSelector(keyReader)
            : CodingAgentResumeSelector.CreateCompositionSelector(compositionSession),
    metadataViewer: compositionSession is null
        ? null
        : (snapshot, cancellationToken) =>
            CodingAgentCompositionMetadataViewer.RunAsync(snapshot, compositionSession, cancellationToken),
    oauthLoginCallbacksFactory: () => new InteractiveOAuthLoginCallbacks(ui),
    keyBindings: editor?.KeyBindings,
    extensionResourceState: extensionResourceState,
    compositionSession: compositionSession,
    startupNoticeService: new CodingAgentStartupNoticeService(settingsStore, changelogStore),
    reloadKeyBindings: editor is null
        ? null
        : () =>
        {
            var bindings = KeyBindingFileStore.LoadOrDefault(ResolveKeyBindingsPath());
            editor.SetKeyBindings(bindings);
            editor.SetAutocompleteProvider(CodingAgentAutocompleteProviderFactory.Create(
                promptTemplateStore,
                skillStore,
                extensionCommandStore));
            return editor.KeyBindings;
        },
    initialPrompt: initialPrompt,
    initialMessages: cli.Messages.Count > 1 ? cli.Messages.Skip(1).ToArray() : null);

return await host.RunAsync(cts.Token).ConfigureAwait(false);
