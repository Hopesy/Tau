using Tau.Ai.Observability;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

var printPrompt = TryExtractPrintPrompt(args);
var rpcMode = HasRpcMode(args);
var noContextFiles = HasNoContextFiles(args);
var noThemes = HasNoThemes(args);
var explicitThemePaths = GetThemePaths(args);
var keyReader = new SystemConsoleKeyReader();
var useCompositionUi = ShouldUseCompositionUi(printPrompt, rpcMode);
var compositionSession = useCompositionUi
    ? new TuiCompositionSession(TuiAnsiRenderSurface.ForConsole(), keyReader)
    : null;
ITerminal terminal = compositionSession is null ? new SystemConsoleTerminal() : new TuiPassiveTerminal();
var editor = printPrompt is null && !rpcMode ? CreateInteractiveEditorIfAttached(keyReader, compositionSession, useCompositionUi) : null;
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

static bool HasRpcMode(string[] cliArgs)
{
    for (var i = 0; i < cliArgs.Length; i++)
    {
        var arg = cliArgs[i];
        if (arg.Equals("--mode=rpc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (arg.Equals("--mode", StringComparison.OrdinalIgnoreCase) &&
            i + 1 < cliArgs.Length &&
            cliArgs[i + 1].Equals("rpc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static bool HasNoContextFiles(string[] cliArgs)
{
    foreach (var arg in cliArgs)
    {
        if (arg.Equals("--no-context-files", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-nc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static bool HasNoThemes(string[] cliArgs)
{
    foreach (var arg in cliArgs)
    {
        if (arg.Equals("--no-themes", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-nt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static IReadOnlyList<string> GetThemePaths(string[] cliArgs)
{
    var paths = new List<string>();
    for (var i = 0; i < cliArgs.Length; i++)
    {
        var arg = cliArgs[i];
        if (arg.Equals("--theme", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= cliArgs.Length)
            {
                Console.Error.WriteLine("error: --theme requires a path argument");
                Environment.Exit(1);
            }

            paths.Add(cliArgs[++i]);
            continue;
        }

        if (arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
        {
            paths.Add(arg["--theme=".Length..]);
        }
    }

    return paths;
}

static string? TryExtractPrintPrompt(string[] cliArgs)
{
    for (var i = 0; i < cliArgs.Length; i++)
    {
        var arg = cliArgs[i];
        if (arg is "--print" or "-p")
        {
            if (i + 1 >= cliArgs.Length)
            {
                Console.Error.WriteLine("error: --print requires a prompt argument");
                Environment.Exit(1);
            }
            return cliArgs[i + 1];
        }
        if (arg.StartsWith("--print=", StringComparison.Ordinal))
        {
            return arg["--print=".Length..];
        }
    }
    return null;
}
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

static bool ShouldUseCompositionUi(string? printPrompt, bool rpcMode) =>
    printPrompt is null &&
    !rpcMode &&
    !Console.IsInputRedirected &&
    !Console.IsOutputRedirected &&
    !string.Equals(Environment.GetEnvironmentVariable("TAU_CODING_AGENT_DISABLE_INPUT_EDITOR"), "1", StringComparison.Ordinal);

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
var extensionCommandStore = new CodingAgentExtensionCommandStore();
var extensionResourceState = new CodingAgentExtensionResourceState(extensionCommandStore.LoadResources());
var promptTemplateStore = new CodingAgentPromptTemplateStore(additionalPathsProvider: () => extensionResourceState.PromptPaths);
var skillStore = new CodingAgentSkillStore(additionalPathsProvider: () => extensionResourceState.SkillPaths);
var contextFileStore = new CodingAgentContextFileStore(includeDefaults: !noContextFiles);
var themeStore = new CodingAgentThemeStore(
    explicitPaths: explicitThemePaths.Count == 0 ? null : explicitThemePaths,
    additionalPathsProvider: () => extensionResourceState.ThemePaths,
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
var providerId = Environment.GetEnvironmentVariable("TAU_PROVIDER") ?? session.Provider ?? settings.DefaultProvider;
var modelId = Environment.GetEnvironmentVariable("TAU_MODEL")
              ?? (string.Equals(providerId, session.Provider, StringComparison.OrdinalIgnoreCase) ? session.Model : null)
              ?? (string.Equals(providerId, settings.DefaultProvider, StringComparison.OrdinalIgnoreCase) ? settings.DefaultModel : null);
var runner = RuntimeCodingAgentRunner.Create(
    providerId,
    modelId,
    session.Messages,
    skills: skillStore.Load(),
    contextFiles: contextFileStore.Load(),
    logSink: logSink);
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

if (printPrompt is not null)
{
    var printMode = new CodingAgentPrintMode(runner, Console.Out, Console.Error);
    return await printMode.RunAsync(printPrompt, cts.Token).ConfigureAwait(false);
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
        });

return await host.RunAsync(cts.Token).ConfigureAwait(false);
