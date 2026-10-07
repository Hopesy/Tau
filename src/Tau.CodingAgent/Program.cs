using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

// 1. 【CodingAgent】【MCP 命令】配置和登录管理在模型会话启动前独立执行
if (args.Length > 0 && args[0] == "mcp")
{
    using var mcpCancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelMcp = (_, interrupt) => { interrupt.Cancel = true; mcpCancellation.Cancel(); };
    Console.CancelKeyPress += cancelMcp;
    try { return await CodingAgentMcpCli.TryHandleAsync(args, Console.Out, Console.Error, token: mcpCancellation.Token).ConfigureAwait(false) ?? 1; }
    finally { Console.CancelKeyPress -= cancelMcp; }
}

// 2. 【CodingAgent】【认证命令取消】独立 CLI 也响应 Ctrl+C，不等待认证网络任务自然结束
if (args.Length > 0 && (args[0].Equals("auth", StringComparison.OrdinalIgnoreCase) || args[0].Equals("login", StringComparison.OrdinalIgnoreCase)))
{
    using var authCancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelAuth = (_, interrupt) => { interrupt.Cancel = true; authCancellation.Cancel(); };
    Console.CancelKeyPress += cancelAuth;
    try { return await CodingAgentAuthCli.TryHandleAsync(args, Console.In, Console.Out, Console.Error, cancellationToken: authCancellation.Token).ConfigureAwait(false) ?? 1; }
    finally { Console.CancelKeyPress -= cancelAuth; }
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
    ? new TuiCompositionSession(
        TuiAnsiRenderSurface.ForConsole(),
        keyReader,
        displayOptions: TuiMessageDisplayOptions.Agent,
        statusTheme: TuiStatusBarTheme.Agent)
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

    var bindings = CodingAgentKeybindings.LoadEditorOrDefault(ResolveKeyBindingsPath());

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

/// <summary>【CodingAgent】【快捷键路径】优先使用显式路径及原生动作配置，兼容已有 Tau 快捷键文件。</summary>
/// <returns>当前配置路径，无法确定用户目录时为空。</returns>
static string? ResolveKeyBindingsPath()
{
    var explicitPath = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_KEYBINDINGS_FILE");
    if (!string.IsNullOrWhiteSpace(explicitPath))
    {
        return explicitPath;
    }

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (string.IsNullOrWhiteSpace(home)) return null;
    var nativePath = Path.Combine(home, ".tau", "keybindings.json");
    return File.Exists(nativePath) ? nativePath : Path.Combine(home, ".tau", "coding-agent-keybindings.json");
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
var settingsStore = CodingAgentSettingsStore.CreateLayered(packageManager.UserSettingsPath, packageManager.ProjectSettingsPath, projectTrusted: false);
packageManager.IsProjectTrusted = false;
var packageResourceState = new CodingAgentPackageResourceState(packageManager.ResolveResources());
using var extensionCommandStore = new CodingAgentExtensionCommandStore(
    explicitPaths: explicitExtensionPaths.Count == 0 ? null : explicitExtensionPaths,
    additionalPathsProvider: () => packageResourceState.ExtensionPaths,
    includeDefaults: !noExtensions,
    sourceInfosProvider: () => packageResourceState.SourceInfos)
{
    IsProjectTrusted = false,
    AutoResourceFilterProvider = () => packageManager.CreateAutomaticResourceFilter("extensions"),
    RefreshResourcePaths = () => packageResourceState.Update(packageManager.ResolveResources())
};
extensionCommandStore.ConfigureModelCatalog(modelCatalog, new Tau.Ai.Registry.FileModelsStore(Path.Combine(packageManager.UserInstallDirectory, "models-store.json")));
extensionCommandStore.SetExtensionUiBridge(null, rpcMode ? "rpc" : printMode ? "print" : "tui");
try
{
await CodingAgentProjectTrustBootstrap.ResolveAsync(Environment.CurrentDirectory, packageManager.UserInstallDirectory,
    settingsStore, packageManager, packageResourceState, extensionCommandStore, cli.ProjectTrustOverride,
    editor is null || cli.Help ? null : async (title, options, token) =>
    {
        Console.Out.WriteLine(title);
        var selector = new TuiSelectList(options.Select(option => new TuiSelectItem(option, option)), maxVisible: 6,
            layout: new TuiSelectListLayout(MinPrimaryColumnWidth: 20, MaxPrimaryColumnWidth: 100));
        var result = compositionSession is null
            ? await new TuiSelectorSession(selector, keyReader, TuiAnsiRenderSurface.ForConsole()).RunAsync(token).ConfigureAwait(false)
            : await TuiCompositionOverlaySessions.RunAsync(selector, compositionSession, token).ConfigureAwait(false);
        return result.HasSelection ? result.SelectedItem?.Value : null;
    }, error => Console.Error.WriteLine(error),
    input: (title, placeholder, token) => ui.ReadInputAsync(title + " " + placeholder + "> ", ConsoleColor.Yellow, token),
    notify: message => Console.Out.WriteLine(message), mode: rpcMode ? "rpc" : printMode ? "print" : "tui").ConfigureAwait(false);
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"error: {error.Message}");
    return 1;
}
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
await extensionCommandStore.InitializeProviderModelsAsync(CancellationToken.None).ConfigureAwait(false);
var promptTemplateStore = new CodingAgentPromptTemplateStore(
    explicitPaths: explicitPromptTemplatePaths.Count == 0 ? null : explicitPromptTemplatePaths,
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.PromptPaths, extensionResourceState.PromptPaths),
    includeDefaults: !noPromptTemplates) { IsProjectTrusted = settingsStore.IsProjectTrusted };
var skillStore = new CodingAgentSkillStore(
    explicitPaths: explicitSkillPaths.Count == 0 ? null : explicitSkillPaths,
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.SkillPaths, extensionResourceState.SkillPaths),
    includeDefaults: !noSkills) { IsProjectTrusted = settingsStore.IsProjectTrusted };
var contextFileStore = new CodingAgentContextFileStore(includeDefaults: !noContextFiles);
var themeStore = new CodingAgentThemeStore(
    explicitPaths: explicitThemePaths.Count == 0 ? null : explicitThemePaths,
    additionalPathsProvider: () => CombineResourcePaths(packageResourceState.ThemePaths, extensionResourceState.ThemePaths),
    includeDefaults: !noThemes) { IsProjectTrusted = settingsStore.IsProjectTrusted };
promptTemplateStore.SourceInfosProvider = () => CodingAgentSourceInfo.Combine(packageResourceState.SourceInfos, extensionResourceState.SourceInfos);
skillStore.SourceInfosProvider = () => CodingAgentSourceInfo.Combine(packageResourceState.SourceInfos, extensionResourceState.SourceInfos);
themeStore.SourceInfosProvider = () => CodingAgentSourceInfo.Combine(packageResourceState.SourceInfos, extensionResourceState.SourceInfos);
promptTemplateStore.AutoResourceFilterProvider = () => packageManager.CreateAutomaticResourceFilter("prompts");
skillStore.AutoResourceFilterProvider = () => packageManager.CreateAutomaticResourceFilter("skills");
themeStore.AutoResourceFilterProvider = () => packageManager.CreateAutomaticResourceFilter("themes");
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

var sessionSelection = CodingAgentBranchModelSelection.Resolve(session, modelCatalog);
var providerId = cli.Provider ?? Environment.GetEnvironmentVariable("TAU_PROVIDER") ?? sessionSelection.Provider ?? settings.DefaultProvider;
var modelId = cli.Model ?? Environment.GetEnvironmentVariable("TAU_MODEL")
              ?? (string.Equals(providerId, sessionSelection.Provider, StringComparison.OrdinalIgnoreCase) ? sessionSelection.Model : null)
              ?? (string.Equals(providerId, settings.DefaultProvider, StringComparison.OrdinalIgnoreCase) ? settings.DefaultModel : null);
var startupThinkingLevel = cli.Thinking;
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

var allRunnerTools = RuntimeCodingAgentRunner.CreateDefaultTools(
    settings.ImagesAutoResize ?? true,
    extensionCommandStore.LoadTools());
var toolSelection = CodingAgentToolSelection.Create(allRunnerTools, cli.Tools, cli.ExcludeTools, settings.DefaultTools,
    cli.NoTools ? CodingAgentSdkNoToolsMode.All : cli.NoBuiltInTools ? CodingAgentSdkNoToolsMode.BuiltIn : CodingAgentSdkNoToolsMode.None);
var runnerInterceptors = extensionCommandStore.LoadToolInterceptors();
var runnerExtensionLifecycleEvents = extensionCommandStore.LoadLifecycleEventSink();
var systemPromptFiles = new CodingAgentSystemPromptFiles(Environment.CurrentDirectory, packageManager.UserInstallDirectory,
    () => settingsStore.IsProjectTrusted, cli.SystemPrompt, cli.AppendSystemPrompt.Count == 0 ? null : cli.AppendSystemPrompt);
var loadedSystemPromptFiles = systemPromptFiles.Load();
var resolvedSystemPrompt = loadedSystemPromptFiles.SystemPrompt;
var resolvedAppendSystemPrompt = loadedSystemPromptFiles.AppendSystemPrompt;
var runner = RuntimeCodingAgentRunner.Create(
    providerId,
    modelId,
    session.Messages,
    toolsOverride: toolSelection.Registered,
    systemPromptOverride: resolvedSystemPrompt,
    skills: skillStore.Load(),
    contextFiles: contextFileStore.Load(),
    logSink: logSink,
    autoResizeImages: settings.ImagesAutoResize ?? true,
    interceptors: runnerInterceptors,
    extensionLifecycleEventSink: runnerExtensionLifecycleEvents,
    appendSystemPrompt: resolvedAppendSystemPrompt,
    apiKey: cli.ApiKey,
    modelCatalogOverride: modelCatalog,
    initialActiveToolNames: cli.Tools is null && !cli.NoTools && !cli.NoBuiltInTools && session.Messages.Any(message => message is SystemMessage)
        ? null : toolSelection.Active);
runner.SessionName = session.Name;
runner.ConfigureSessionSettings(settingsStore);
runner.ConfigureToolSelection(toolSelection);
runner.ConfigureScopedModelPatterns(scopedModelsOverride);
runner.ConfigureInputResources(skillStore, promptTemplateStore);
runner.ConfigureResourceReload(() => runner.RefreshSystemPromptResources(skillStore.Load(), contextFileStore.Load(), systemPromptFiles.Load()));
extensionCommandStore.ResourcesChanged += resources =>
{
    extensionResourceState.Update(resources);
    runner.RefreshSystemPromptResources(skillStore.Load(), contextFileStore.Load(), systemPromptFiles.Load());
};
runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(settings.SteeringMode);
runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(settings.FollowUpMode);
runner.InstallTelemetryEnabled = CodingAgentTelemetry.IsInstallTelemetryEnabled(
    settings,
    Environment.GetEnvironmentVariable("PI_TELEMETRY"));
var autoCompaction = CodingAgentAutoCompactionOptions.FromEnvironment();
// 1. 【CodingAgent】【思考恢复】显式及范围参数优先，随后恢复会话，缺少记录才使用设置和默认值
runner.ThinkingLevel = CodingAgentThinkingLevels.ResolveStartup(
    runner.Model, startupThinkingLevel, session.ThinkingLevel, settings.DefaultThinkingLevel);
extensionCommandStore.BindSession(runner, treeSessionController, sessionStore);
// 1. 【CodingAgent】【MCP 会话】CLI 默认加载服务器配置，连接失败由服务状态记录，退出时统一释放
await using var mcpService = new Tau.CodingAgent.Runtime.Mcp.CodingAgentMcpService(
    packageManager.UserInstallDirectory, Environment.CurrentDirectory, () => settingsStore.IsProjectTrusted, extensionCommandStore.McpServers,
    new() { ProviderToken = runner.ResolveMcpProviderTokenAsync });
await mcpService.ReloadAsync(cts.Token).ConfigureAwait(false);
runner.AttachMcpService(mcpService);
runner.EnableToolSearch();
runner.EnableCodeMode(extensionCommandStore);
editor?.SetAutocompleteProvider(CodingAgentAutocompleteProviderFactory.Create(
    promptTemplateStore, skillStore, extensionCommandStore, mcpService: mcpService));

if (printMode)
{
    extensionCommandStore.SetExtensionUiBridge(null, jsonMode ? "json" : "print");
    foreach (var error in await extensionCommandStore.EnsureSessionStartedAsync(cts.Token).ConfigureAwait(false))
        Console.Error.WriteLine($"【CodingAgent】【扩展启动】{error.FilePath}: {error.Error}");
    var printRunner = new CodingAgentPrintMode(
        runner, Console.Out, Console.Error, jsonMode: jsonMode,
        sessionStore: sessionStore, treeSessionController: treeSessionController);
    // 1. 【CodingAgent】【提示序列】初始提示已消费第一个位置参数，其余参数逐轮执行
    var remainingMessages = cli.Messages.Skip(1).ToArray();
    if (initialPrompt is null && remainingMessages.Length == 0)
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

    return await printRunner.RunAsync(initialPrompt, remainingMessages, cts.Token).ConfigureAwait(false);
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
    hideThinkingBlock: settings.HideThinkingBlock ?? false,
    turnInputSource: editor is null
        ? null
        : compositionSession is null
            ? new SystemConsoleCodingAgentTurnInputSource()
            : new CompositionCodingAgentTurnInputSource(keyReader, compositionSession, keyBindingsProvider: () => editor.KeyBindings, sharedEditor: editor),
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
            ? CodingAgentAuthSelector.CreateConsoleSelector(keyReader, themeProvider: () => themeStore.Find(settingsStore.Load().Theme))
            : CodingAgentAuthSelector.CreateCompositionSelector(compositionSession, () => themeStore.Find(settingsStore.Load().Theme)),
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
    oauthLoginCallbacksFactory: () => new InteractiveOAuthLoginCallbacks(ui, editor is null ? null : compositionSession is null
        ? CodingAgentOAuthPromptSelector.CreateConsoleSelector(keyReader, () => themeStore.Find(settingsStore.Load().Theme), () => CodingAgentKeybindings.LoadOrDefault(ResolveKeyBindingsPath()))
        : CodingAgentOAuthPromptSelector.CreateCompositionSelector(compositionSession, () => themeStore.Find(settingsStore.Load().Theme), () => CodingAgentKeybindings.LoadOrDefault(ResolveKeyBindingsPath()))),
    mcpMenuSelector: editor is null ? null : compositionSession is null
        ? CodingAgentMcpMenuSelector.CreateConsoleSelector(keyReader)
        : CodingAgentMcpMenuSelector.CreateCompositionSelector(compositionSession),
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
            var bindings = CodingAgentKeybindings.LoadEditorOrDefault(ResolveKeyBindingsPath());
            editor.SetKeyBindings(bindings);
            editor.SetAutocompleteProvider(CodingAgentAutocompleteProviderFactory.Create(
                promptTemplateStore,
                skillStore,
                extensionCommandStore, mcpService: mcpService));
            return editor.KeyBindings;
        },
    initialPrompt: initialPrompt,
    initialMessages: cli.Messages.Count > 1 ? cli.Messages.Skip(1).ToArray() : null);

return await host.RunAsync(cts.Token).ConfigureAwait(false);
