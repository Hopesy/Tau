using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

if (await CodingAgentAuthCli.TryHandleAsync(args, Console.In, Console.Out, Console.Error).ConfigureAwait(false) is { } authCommandExitCode)
{
    return authCommandExitCode;
}

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

// Mirrors upstream main.ts: `--offline` forces PI_OFFLINE=1 so all downstream startup network
// guards (package manager update, startup changelog telemetry) observe offline mode.
if (cli.Offline)
{
    Environment.SetEnvironmentVariable("PI_OFFLINE", "1");
}

if (cli.Diagnostics.Count > 0)
{
    var hasError = false;
    foreach (var diagnostic in cli.Diagnostics)
    {
        if (diagnostic.Type.Equals("error", StringComparison.Ordinal))
        {
            hasError = true;
            Console.Error.WriteLine($"Error: {diagnostic.Message}");
        }
        else
        {
            Console.Error.WriteLine($"Warning: {diagnostic.Message}");
        }
    }

    if (hasError)
    {
        return 1;
    }
}

if (cli.Version)
{
    Console.Out.WriteLine(CodingAgentCliHelp.ResolveVersion());
    return 0;
}

var migrationResult = CodingAgentMigrations.Run();
CodingAgentMigrations.PrintDeprecationWarnings(migrationResult.DeprecationWarnings, Console.Error);

if (!string.IsNullOrWhiteSpace(cli.Export))
{
    return ExportSessionFile(cli.Export, cli.Messages.Count > 0 ? cli.Messages[0] : null);
}

if (!string.IsNullOrWhiteSpace(cli.Fork))
{
    var conflictingSessionFlags = new List<string>();
    if (!string.IsNullOrWhiteSpace(cli.Session))
    {
        conflictingSessionFlags.Add("--session");
    }

    if (cli.Continue)
    {
        conflictingSessionFlags.Add("--continue");
    }

    if (cli.Resume)
    {
        conflictingSessionFlags.Add("--resume");
    }

    if (cli.NoSession)
    {
        conflictingSessionFlags.Add("--no-session");
    }

    if (conflictingSessionFlags.Count > 0)
    {
        Console.Error.WriteLine($"error: --fork cannot be combined with {string.Join(", ", conflictingSessionFlags)}");
        return 1;
    }
}

var jsonMode = cli.JsonMode;
// Mirrors upstream resolveAppMode: `--mode json` is a non-interactive single-shot mode that emits
// the agent event stream as JSON, so it implies print mode.
var printMode = cli.PrintMode || jsonMode;
var rpcMode = cli.RpcMode;
var noContextFiles = cli.NoContextFiles;
var noThemes = cli.NoThemes;
var noExtensions = cli.NoExtensions;
var noSkills = cli.NoSkills;
var noPromptTemplates = cli.NoPromptTemplates;
var explicitThemePaths = cli.ThemePaths;
var explicitExtensionPaths = cli.ExtensionPaths;
var explicitSkillPaths = cli.SkillPaths;
var explicitPromptTemplatePaths = cli.PromptTemplatePaths;
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
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

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

// Mirrors upstream resolvePromptInput (core/resource-loader.ts): if the value names an existing
// file, read its contents; otherwise treat it as literal prompt text.
static string? ResolvePromptInput(string? input)
{
    if (string.IsNullOrWhiteSpace(input))
    {
        return null;
    }

    try
    {
        if (File.Exists(input))
        {
            return File.ReadAllText(input);
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"warning: could not read prompt file {input}: {ex.Message}");
        return input;
    }

    return input;
}

// Mirrors upstream append-system-prompt resolution: each --append-system-prompt value is resolved
// as a file path or literal, then non-empty results are joined with a blank line.
static string? CombineAppendSystemPrompt(IReadOnlyList<string> values)
{
    if (values.Count == 0)
    {
        return null;
    }

    var resolved = values
        .Select(ResolvePromptInput)
        .Where(static text => !string.IsNullOrWhiteSpace(text))
        .Select(static text => text!)
        .ToArray();

    return resolved.Length == 0 ? null : string.Join("\n\n", resolved);
}

// Mirrors upstream --tools / --no-tools selection (main.ts): a null result keeps Tau's full default
// built-in tool set; an explicit (possibly empty) list enables only the named built-ins. Extension
// tools always load regardless of this selection.
static IReadOnlyList<string>? ResolveSelectedBuiltInToolNames(CodingAgentCliArguments cli)
{
    if (cli.NoTools)
    {
        return cli.Tools ?? [];
    }

    return cli.Tools;
}

// Mirrors upstream --export (core/export-html/index.ts exportFromFile): read a JSONL session file,
// render it to standalone HTML and exit. Output path defaults to pi-session-<input>.html when omitted.
static int ExportSessionFile(string inputPath, string? outputPath)
{
    var result = CodingAgentSessionFileExporter.Export(inputPath, outputPath);
    if (result.Success)
    {
        Console.Out.WriteLine($"Exported to: {result.OutputPath}");
        return 0;
    }

    Console.Error.WriteLine($"Error: {result.ErrorMessage}");
    return 1;
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

static Model? TryResolveStartupModel(ModelCatalog modelCatalog, string? providerId, string? modelId)
{
    try
    {
        var selection = modelCatalog.ResolveSelection(
            providerId,
            modelId,
            defaultProvider: Environment.GetEnvironmentVariable("TAU_PROVIDER"));
        return modelCatalog.GetModel(selection.Provider, selection.ModelId);
    }
    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
    {
        return null;
    }
}

static CodingAgentScopedModelEntry? FindScopedModelEntry(
    IReadOnlyList<CodingAgentScopedModelEntry> entries,
    Model model)
{
    foreach (var entry in entries)
    {
        if (entry.Model.Provider.Equals(model.Provider, StringComparison.OrdinalIgnoreCase) &&
            entry.Model.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase))
        {
            return entry;
        }
    }

    return null;
}

static bool HasExplicitStartupModelSelection(CodingAgentCliArguments cli) =>
    !string.IsNullOrWhiteSpace(cli.Provider) ||
    !string.IsNullOrWhiteSpace(cli.Model) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAU_PROVIDER")) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAU_MODEL"));

static string FormatModelScopeNotice(IReadOnlyList<CodingAgentScopedModelEntry> entries) =>
    "Model scope: " + string.Join(", ", entries.Select(static entry => entry.Pattern)) + " (Ctrl+P to cycle)";

var modelCatalog = new ModelCatalog();
var registeredModels = CodingAgentModelAvailability.GetRegisteredModels(modelCatalog);
if (cli.ListModels is not null)
{
    Console.Out.WriteLine(CodingAgentModelListFormatter.Format(registeredModels, cli.ListModels.SearchPattern));
    return 0;
}

if (!CodingAgentModelAvailability.TryResolveScopedModelEntries(
        cli.Models,
        registeredModels,
        out var cliScopedModels,
        out var modelScopeError))
{
    Console.Error.WriteLine($"error: --models {modelScopeError}");
    return 1;
}
var scopedModelsOverride = cliScopedModels.Count == 0
    ? cli.Models
    : cliScopedModels.Select(static entry => entry.Pattern).ToArray();

var startupResume = await CodingAgentStartupResumeResolver.ResolveAsync(
        cli.Resume && !cli.NoSession,
        cli.Session,
        printMode,
        rpcMode,
        cli.SessionDir,
        CodingAgentTreeSessionStore.GetDefaultPath(),
        editor is null
            ? null
            : compositionSession is null
                ? CodingAgentResumeSelector.CreateConsoleSelector(keyReader)
                : CodingAgentResumeSelector.CreateCompositionSelector(compositionSession),
        cts.Token)
    .ConfigureAwait(false);
if (startupResume.ExitCode is { } startupResumeExitCode)
{
    if (!string.IsNullOrWhiteSpace(startupResume.Message))
    {
        if (startupResume.IsError)
        {
            Console.Error.WriteLine(startupResume.Message);
        }
        else
        {
            Console.Out.WriteLine(startupResume.Message);
        }
    }

    return startupResumeExitCode;
}

CodingAgentSessionTarget sessionTarget;
try
{
    var explicitSessionReference = cli.Session ?? startupResume.SelectedPath;
    var continueRecent = string.IsNullOrWhiteSpace(explicitSessionReference) && cli.Continue;
    sessionTarget = CodingAgentSessionTarget.Resolve(
        explicitSessionReference,
        continueRecent,
        cli.SessionDir,
        cli.Fork,
        cli.NoSession);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
var sessionStore = sessionTarget.SessionStore;
var treeSessionController = sessionTarget.TreeSessionController;
var settingsStore = new CodingAgentSettingsStore();
var packageResourceState = new CodingAgentPackageResourceState(packageManager.ResolveResources());
var extensionCommandStore = new CodingAgentExtensionCommandStore(
    explicitPaths: explicitExtensionPaths.Count == 0 ? null : explicitExtensionPaths,
    additionalPathsProvider: () => packageResourceState.ExtensionPaths,
    includeDefaults: !noExtensions);
if (cli.Help)
{
    Console.Out.WriteLine(CodingAgentCliHelp.BuildHelpText(
        CodingAgentCliHelp.ResolveCommandName(),
        extensionCommandStore.LoadStatus().Flags));
    return 0;
}

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
    explicitPaths: explicitPromptTemplatePaths.Count == 0 ? null : explicitPromptTemplatePaths,
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.PromptPaths, extensionResourceState.PromptPaths),
    includeDefaults: !noPromptTemplates);
var skillStore = new CodingAgentSkillStore(
    explicitPaths: explicitSkillPaths.Count == 0 ? null : explicitSkillPaths,
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.SkillPaths, extensionResourceState.SkillPaths),
    includeDefaults: !noSkills);
var contextFileStore = new CodingAgentContextFileStore(includeDefaults: !noContextFiles);
var themeStore = new CodingAgentThemeStore(
    explicitPaths: explicitThemePaths.Count == 0 ? null : explicitThemePaths,
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.ThemePaths, extensionResourceState.ThemePaths),
    includeDefaults: !noThemes);
editor?.SetAutocompleteProvider(CodingAgentAutocompleteProviderFactory.Create(
    promptTemplateStore,
    skillStore,
    extensionCommandStore));
var session = sessionTarget.LoadInitialSnapshot();
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
var startupThinkingLevel = cli.Thinking ?? settings.DefaultThinkingLevel;
if (cliScopedModels.Count > 0)
{
    var startupModel = TryResolveStartupModel(modelCatalog, providerId, modelId);
    var scopedEntry = startupModel is null ? null : FindScopedModelEntry(cliScopedModels, startupModel);
    if (scopedEntry is null && !HasExplicitStartupModelSelection(cli))
    {
        var first = cliScopedModels[0];
        providerId = first.Model.Provider;
        modelId = first.Model.Id;
        scopedEntry = first;
    }

    if (cli.Thinking is null && scopedEntry is { ThinkingLevel: not null } thinkingEntry)
    {
        startupThinkingLevel = thinkingEntry.ThinkingLevel;
    }
}

var selectedBuiltInToolNames = ResolveSelectedBuiltInToolNames(cli);
var runnerTools = RuntimeCodingAgentRunner.CreateDefaultTools(
    settings.ImagesAutoResize ?? true,
    extensionCommandStore.LoadTools(),
    selectedBuiltInToolNames);
var runnerInterceptors = extensionCommandStore.LoadToolInterceptors();
var runnerExtensionLifecycleEvents = extensionCommandStore.LoadLifecycleEventSink();
var resolvedSystemPrompt = ResolvePromptInput(cli.SystemPrompt);
var resolvedAppendSystemPrompt = CombineAppendSystemPrompt(cli.AppendSystemPrompt);
var runner = RuntimeCodingAgentRunner.Create(
    providerId,
    modelId,
    session.Messages,
    toolsOverride: runnerTools,
    systemPromptOverride: resolvedSystemPrompt,
    skills: skillStore.Load(),
    contextFiles: contextFileStore.Load(),
    logSink: logSink,
    autoResizeImages: settings.ImagesAutoResize ?? true,
    interceptors: runnerInterceptors,
    extensionLifecycleEventSink: runnerExtensionLifecycleEvents,
    appendSystemPrompt: resolvedAppendSystemPrompt,
    apiKey: cli.ApiKey,
    modelCatalogOverride: modelCatalog);
runner.SessionName = session.Name;
runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(settings.SteeringMode);
runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(settings.FollowUpMode);
var autoCompaction = CodingAgentAutoCompactionOptions.FromEnvironment();
// An explicit --thinking flag overrides the persisted defaultThinkingLevel, mirroring upstream
// main.ts where parsed.thinking takes precedence over saved/scoped thinking levels.
if (!string.IsNullOrWhiteSpace(startupThinkingLevel))
{
    runner.ThinkingLevel = CodingAgentThinkingLevels.ClampForModel(
        runner.Model,
        CodingAgentThinkingLevels.ParseOrNull(startupThinkingLevel));
}

if (printMode)
{
    var printRunner = new CodingAgentPrintMode(runner, Console.Out, Console.Error, jsonMode: jsonMode);
    if (initialPrompt is null)
    {
        Console.Error.WriteLine("error: --print requires a prompt, stdin, or @file argument");
        return 1;
    }

    // Mirrors upstream print-mode.ts: json mode emits the session header as the first JSONL line
    // before any agent event, when a tree session is available.
    if (jsonMode && treeSessionController is not null)
    {
        try
        {
            Console.Out.WriteLine(CodingAgentRpcHost.SerializeHeaderLine(treeSessionController.GetSessionHeader()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // A header read failure must not block the json event stream.
        }
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
            extensionCommandStore: extensionCommandStore,
            scopedModelsOverride: scopedModelsOverride);
    return await rpcHost.RunAsync(cts.Token).ConfigureAwait(false);
}

var scopedModelsForNotice = CodingAgentModelAvailability.GetModelCycleCandidates(
    scopedModelsOverride ?? settings.EnabledModels,
    registeredModels,
    registeredModels,
    out var hasScopedModelsForNotice);
if (!printMode && !rpcMode && hasScopedModelsForNotice && (cli.Verbose || settings.QuietStartup != true))
{
    Console.Out.WriteLine(FormatModelScopeNotice(scopedModelsForNotice));
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
    treeNavigator: editor is null || treeSessionController is null
        ? null
        : CreateTreeNavigator(keyReader, settingsStore, treeSessionController, compositionSession),
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
    scopedModelsOverride: scopedModelsOverride,
    versionUpdateChecker: cancellationToken => CodingAgentVersionCheck.CheckForNewVersionAsync(
        CodingAgentCliHelp.ResolveVersion(),
        cancellationToken: cancellationToken),
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
