using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentCommandRouter
{
    private readonly ICodingAgentRunner _runner;
    private readonly CodingAgentSettingsStore? _settingsStore;
    private readonly ICodingAgentClipboard _clipboard;
    private readonly ICodingAgentShareClient _shareClient;
    private readonly CodingAgentTreeSessionController? _treeSessionController;
    private readonly CodingAgentPromptTemplateStore? _promptTemplateStore;
    private readonly CodingAgentSkillStore? _skillStore;
    private readonly CodingAgentContextFileStore? _contextFileStore;
    private readonly CodingAgentThemeStore? _themeStore;
    private readonly CodingAgentExtensionCommandStore? _extensionCommandStore;
    private readonly CodingAgentChangelogStore _changelogStore;
    private readonly CodingAgentAutoCompactionOptions _autoCompaction;
    private readonly Action<CodingAgentRetryOptions>? _retryOptionsChanged;
    private readonly Action<bool?>? _autoCompactionChanged;
    private readonly Func<int, IReadOnlyList<string>>? _historySnapshotProvider;
    private readonly Action? _clearScreenAction;
    private readonly Action<string?>? _inputDraftSetter;
    private readonly Func<CodingAgentTreeNavigationPromptState, CancellationToken, Task<CodingAgentTreeNavigationDecision>>? _treeNavigationPrompt;
    private readonly CodingAgentSessionSwitchCoordinator _sessionSwitchCoordinator;
    private readonly Func<CodingAgentTreeLabelPromptState, CancellationToken, Task<CodingAgentTreeLabelPromptResult>>? _treeLabelPrompt;
    private readonly Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>>? _treeNavigator;
    private readonly Func<CodingAgentThemeStatus, string?, CancellationToken, Task<string?>>? _themeSelector;
    private readonly Func<CodingAgentSettingsSelectorState, CancellationToken, Task<string?>>? _settingsSelector;
    private readonly Func<CodingAgentScopedModelsSelectorState, CancellationToken, Task<CodingAgentScopedModelsSelection>>? _scopedModelsSelector;
    private readonly Func<CodingAgentAuthSelectorState, CancellationToken, Task<string?>>? _authSelector;
    private readonly Func<CodingAgentThinkingSelectorState, CancellationToken, Task<string?>>? _thinkingSelector;
    private readonly Func<CodingAgentModelSelectorState, CancellationToken, Task<string?>>? _modelSelector;
    private readonly Func<CodingAgentResumeSelectorState, CancellationToken, Task<CodingAgentResumeSelectionResult>>? _resumeSelector;
    private readonly Func<CodingAgentTreeMetadataSnapshot, CancellationToken, Task>? _metadataViewer;
    private readonly Func<Tau.Ai.Auth.OAuth.IOAuthLoginCallbacks> _oauthLoginCallbacksFactory;
    private readonly CodingAgentExtensionResourceState? _extensionResourceState;
    private readonly Func<IKeyBindingMap?>? _reloadKeyBindings;
    private readonly string? _sessionFile;
    private CodingAgentRetryOptions _retryOptions;
    private IKeyBindingMap? _keyBindings;

    public CodingAgentCommandRouter(
        ICodingAgentRunner runner,
        CodingAgentSettingsStore? settingsStore = null,
        string? sessionFile = null,
        ICodingAgentClipboard? clipboard = null,
        ICodingAgentShareClient? shareClient = null,
        CodingAgentTreeSessionController? treeSessionController = null,
        CodingAgentPromptTemplateStore? promptTemplateStore = null,
        CodingAgentSkillStore? skillStore = null,
        CodingAgentContextFileStore? contextFileStore = null,
        CodingAgentThemeStore? themeStore = null,
        CodingAgentExtensionCommandStore? extensionCommandStore = null,
        CodingAgentChangelogStore? changelogStore = null,
        CodingAgentAutoCompactionOptions? autoCompaction = null,
        CodingAgentRetryOptions? retryOptions = null,
        Action<CodingAgentRetryOptions>? retryOptionsChanged = null,
        Action<bool?>? autoCompactionChanged = null,
        Func<int, IReadOnlyList<string>>? historySnapshotProvider = null,
        Action? clearScreenAction = null,
        Action<string?>? inputDraftSetter = null,
        Func<CodingAgentTreeNavigationPromptState, CancellationToken, Task<CodingAgentTreeNavigationDecision>>? treeNavigationPrompt = null,
        Func<CodingAgentSessionSwitchPromptState, CancellationToken, Task<CodingAgentTreeNavigationDecision>>? sessionSwitchPrompt = null,
        Func<CodingAgentSessionSwitchHookState, CancellationToken, Task<CodingAgentSessionSwitchHookResult?>>? sessionSwitchHook = null,
        Func<CodingAgentTreeLabelPromptState, CancellationToken, Task<CodingAgentTreeLabelPromptResult>>? treeLabelPrompt = null,
        Func<IReadOnlyList<CodingAgentTreeViewItem>, string?, CancellationToken, Task<CodingAgentTreeInteractiveNavigator.Result>>? treeNavigator = null,
        Func<CodingAgentThemeStatus, string?, CancellationToken, Task<string?>>? themeSelector = null,
        Func<CodingAgentSettingsSelectorState, CancellationToken, Task<string?>>? settingsSelector = null,
        Func<CodingAgentScopedModelsSelectorState, CancellationToken, Task<CodingAgentScopedModelsSelection>>? scopedModelsSelector = null,
        Func<CodingAgentAuthSelectorState, CancellationToken, Task<string?>>? authSelector = null,
        Func<CodingAgentThinkingSelectorState, CancellationToken, Task<string?>>? thinkingSelector = null,
        Func<CodingAgentModelSelectorState, CancellationToken, Task<string?>>? modelSelector = null,
        Func<CodingAgentResumeSelectorState, CancellationToken, Task<CodingAgentResumeSelectionResult>>? resumeSelector = null,
        Func<CodingAgentTreeMetadataSnapshot, CancellationToken, Task>? metadataViewer = null,
        Func<Tau.Ai.Auth.OAuth.IOAuthLoginCallbacks>? oauthLoginCallbacksFactory = null,
        IKeyBindingMap? keyBindings = null,
        CodingAgentExtensionResourceState? extensionResourceState = null,
        Func<IKeyBindingMap?>? reloadKeyBindings = null)
    {
        _runner = runner;
        _settingsStore = settingsStore;
        _clipboard = clipboard ?? new SystemCodingAgentClipboard();
        _shareClient = shareClient ?? new GitHubCliCodingAgentShareClient();
        _treeSessionController = treeSessionController;
        _promptTemplateStore = promptTemplateStore;
        _skillStore = skillStore;
        _contextFileStore = contextFileStore;
        _themeStore = themeStore;
        _extensionCommandStore = extensionCommandStore;
        _changelogStore = changelogStore ?? new CodingAgentChangelogStore();
        _autoCompaction = autoCompaction ?? CodingAgentAutoCompactionOptions.Disabled;
        _retryOptions = retryOptions ?? CodingAgentRetryOptions.Disabled;
        _retryOptionsChanged = retryOptionsChanged;
        _autoCompactionChanged = autoCompactionChanged;
        _historySnapshotProvider = historySnapshotProvider;
        _clearScreenAction = clearScreenAction;
        _inputDraftSetter = inputDraftSetter;
        _treeNavigationPrompt = treeNavigationPrompt;
        _sessionSwitchCoordinator = new CodingAgentSessionSwitchCoordinator(
            runner,
            treeSessionController,
            sessionSwitchPrompt,
            sessionSwitchHook);
        _treeLabelPrompt = treeLabelPrompt;
        _treeNavigator = treeNavigator;
        _themeSelector = themeSelector;
        _settingsSelector = settingsSelector;
        _scopedModelsSelector = scopedModelsSelector;
        _authSelector = authSelector;
        _thinkingSelector = thinkingSelector;
        _modelSelector = modelSelector;
        _resumeSelector = resumeSelector;
        _metadataViewer = metadataViewer;
        _oauthLoginCallbacksFactory = oauthLoginCallbacksFactory ?? (() => new ConsoleOAuthLoginCallbacks());
        _keyBindings = keyBindings;
        _extensionResourceState = extensionResourceState;
        _reloadKeyBindings = reloadKeyBindings;
        _sessionFile = sessionFile;
    }

    public async Task<CodingAgentCommandResult> TryHandleAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return CodingAgentCommandResult.NotCommand;
        }

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return CodingAgentCommandResult.NotCommand;
        }

        var command = parts[0].ToLowerInvariant();
        try
        {
            return command switch
            {
                "/help" => HandleHelpCommand(parts),
                "/reload" => HandleReloadCommand(parts),
                "/hotkeys" => HandleHotkeysCommand(parts),
                "/settings" => await HandleSettingsCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/theme" => await HandleThemeCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/name" => HandleNameCommand(input, parts),
                "/copy" => await HandleCopyCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/files" => HandleFilesCommand(parts),
                "/export" => HandleExportCommand(input, parts),
                "/share" => await HandleShareCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/import" => await HandleImportCommandAsync(input, parts, cancellationToken).ConfigureAwait(false),
                "/new" => await HandleNewCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/quit" => HandleQuitCommand(parts),
                "/session" => HandleSessionCommand(parts),
                "/metadata" => await HandleMetadataCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/tree" => await HandleTreeCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/label" => HandleLabelCommand(input, parts),
                "/fork" => await HandleForkCommandAsync(input, parts, cancellationToken).ConfigureAwait(false),
                "/clone" => HandleCloneCommand(parts),
                "/resume" => await HandleResumeCommandAsync(input, parts, cancellationToken).ConfigureAwait(false),
                "/model" => await HandleModelCommandAsync(input, parts, cancellationToken).ConfigureAwait(false),
                "/provider" => HandleProviderCommand(parts),
                "/models" => HandleModelsCommand(parts),
                "/providers" => HandleProvidersCommand(parts),
                "/scoped-models" => await HandleScopedModelsCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/prompts" => HandlePromptsCommand(parts),
                "/skills" => HandleSkillsCommand(parts),
                "/extensions" => HandleExtensionsCommand(parts),
                "/auth" => await HandleAuthCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/login" => await HandleLoginCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/logout" => await HandleLogoutCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/changelog" => HandleChangelogCommand(parts),
                "/retry" => HandleRetryCommand(parts),
                "/thinking" => await HandleThinkingCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/history" => HandleHistoryCommand(parts),
                "/find" => HandleFindCommand(input, parts),
                "/clear" => HandleClearCommand(parts),
                "/compact" => await HandleCompactCommandAsync(input, parts, cancellationToken).ConfigureAwait(false),
                _ => CodingAgentCommandResult.Error($"unknown command '{parts[0]}'")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            return CodingAgentCommandResult.Error(ex.Message);
        }
    }

    private static CodingAgentCommandResult HandleHelpCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/help"));
        }

        return CodingAgentCommandResult.Status(CodingAgentCommandCatalog.HelpLine);
    }

    private CodingAgentCommandResult HandleReloadCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/reload"));
        }

        var lines = new List<string>();
        var settings = _settingsStore?.Load();
        if (settings is null)
        {
            lines.Add("settings: unavailable");
        }
        else
        {
            _retryOptions = CodingAgentRetryOptions.FromSettingsOrEnvironment(settings);
            _retryOptionsChanged?.Invoke(_retryOptions);
            _autoCompactionChanged?.Invoke(settings.AutoCompactionEnabled);
            _runner.ThinkingLevel = CodingAgentThinkingLevels.ClampForModel(
                _runner.Model,
                ParseThinkingLevelOrNull(settings.DefaultThinkingLevel));
            _runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(settings.SteeringMode);
            _runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(settings.FollowUpMode);
            lines.Add(
                $"settings: loaded, retry {FormatRetryPolicy(_retryOptions)}, thinking {FormatThinkingLevel(_runner.ThinkingLevel)}, steering {CodingAgentQueueModes.FromAgentQueueMode(_runner.SteeringMode)}, follow-up {CodingAgentQueueModes.FromAgentQueueMode(_runner.FollowUpMode)}");
        }

        var extensionStatus = _extensionCommandStore?.LoadStatus();
        if (extensionStatus is null)
        {
            lines.Add("extensions: unavailable");
        }
        else
        {
            _extensionResourceState?.Update(extensionStatus.Resources);
            lines.Add(
                $"extensions: {extensionStatus.Commands.Count} commands, {extensionStatus.Files.Count} files, {extensionStatus.Diagnostics.Count} issues");
        }

        var prompts = _promptTemplateStore?.Load();
        lines.Add(prompts is null ? "prompts: unavailable" : $"prompts: {prompts.Count}");

        var skills = _skillStore?.Load();
        var contextFiles = _contextFileStore?.Load();
        var promptRefreshed = skills is null && contextFiles is null
            ? false
            : _runner.RefreshSystemPromptResources(skills ?? [], contextFiles ?? []);
        if (skills is null)
        {
            lines.Add("skills: unavailable");
        }
        else
        {
            lines.Add($"skills: {skills.Count}, runner prompt {(promptRefreshed ? "refreshed" : "unchanged")}");
        }

        lines.Add(contextFiles is null
            ? "context files: unavailable"
            : $"context files: {contextFiles.Count}, runner prompt {(promptRefreshed ? "refreshed" : "unchanged")}");

        if (_reloadKeyBindings is null)
        {
            lines.Add("keybindings: unavailable");
        }
        else
        {
            _keyBindings = _reloadKeyBindings();
            lines.Add(_keyBindings is null ? "keybindings: unavailable" : $"keybindings: {_keyBindings.Bindings.Count}");
        }

        var themeStatus = _themeStore?.LoadStatus();
        lines.Add(themeStatus is null
            ? "themes: unavailable"
            : $"themes: {themeStatus.Themes.Count}, current {FormatCurrentTheme(settings?.Theme)}, issues {themeStatus.Diagnostics.Count}");
        return CodingAgentCommandResult.Status($"reload complete:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    private CodingAgentCommandResult HandleHotkeysCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/hotkeys"));
        }

        if (_keyBindings is null)
        {
            return CodingAgentCommandResult.Error("Hotkeys are not available in this session.");
        }

        return CodingAgentCommandResult.Status(CodingAgentHotkeysFormatter.Format(_keyBindings));
    }

    private async Task<CodingAgentCommandResult> HandleSettingsCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count > 2 ||
            (parts.Count == 2 &&
             !IsCurrentKeyword(parts[1]) &&
             !parts[1].Equals("path", StringComparison.OrdinalIgnoreCase) &&
             !parts[1].Equals("select", StringComparison.OrdinalIgnoreCase)))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/settings"));
        }

        if (_settingsStore is null)
        {
            return CodingAgentCommandResult.Error("settings are not available in this session");
        }

        if (parts.Count == 2 && parts[1].Equals("path", StringComparison.OrdinalIgnoreCase))
        {
            return CodingAgentCommandResult.Status($"settings: {_settingsStore.Path}");
        }

        var settings = _settingsStore.Load();
        var shouldOpenSelector = parts.Count == 1 || parts[1].Equals("select", StringComparison.OrdinalIgnoreCase);
        if (shouldOpenSelector)
        {
            if (_settingsSelector is null)
            {
                return parts.Count == 1
                    ? CodingAgentCommandResult.Status(FormatSettings(settings))
                    : CodingAgentCommandResult.Error("settings selector is not available in this session");
            }

            var selected = await _settingsSelector(
                    CreateSettingsSelectorState(settings),
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return CodingAgentCommandResult.Status("settings selection cancelled");
            }

            return await ApplySettingsSelectionAsync(settings, selected.Trim(), cancellationToken).ConfigureAwait(false);
        }

        return CodingAgentCommandResult.Status(FormatSettings(settings));
    }

    private CodingAgentSettingsSelectorState CreateSettingsSelectorState(CodingAgentSettingsSnapshot settings) =>
        new(
            _settingsStore?.Path ?? "unavailable",
            settings,
            _runner.Model,
            _runner.ThinkingLevel,
            ResolveAutoCompactionEnabled(settings.AutoCompactionEnabled),
            FormatCurrentTheme(settings.Theme));

    private async Task<CodingAgentCommandResult> ApplySettingsSelectionAsync(
        CodingAgentSettingsSnapshot snapshot,
        string selection,
        CancellationToken cancellationToken)
    {
        var current = _settingsStore?.Load() ?? snapshot;
        if (CodingAgentSettingsSelector.TryParseSelection(selection, out var settingId, out var settingValue))
        {
            return await ApplySettingsValueAsync(current, settingId, settingValue, cancellationToken)
                .ConfigureAwait(false);
        }

        var normalized = selection.Trim().ToLowerInvariant();

        return normalized switch
        {
            CodingAgentSettingsSelector.AutoCompactionAction => SaveAutoCompactionSelection(current),
            CodingAgentSettingsSelector.SteeringModeAction => SaveSteeringModeSelection(current),
            CodingAgentSettingsSelector.FollowUpModeAction => SaveFollowUpModeSelection(current),
            CodingAgentSettingsSelector.TreeFilterModeAction => SaveTreeFilterModeSelection(current),
            CodingAgentSettingsSelector.ThinkingLevelAction => SaveThinkingLevelSelection(current),
            CodingAgentSettingsSelector.ScopedModelsAction => await SelectScopedModelsFromSettingsAsync(current, cancellationToken).ConfigureAwait(false),
            CodingAgentSettingsSelector.ThemeAction => await SelectThemeAsync(current, cancellationToken).ConfigureAwait(false),
            _ => CodingAgentCommandResult.Error($"settings selector returned unsupported action '{selection}'")
        };
    }

    private async Task<CodingAgentCommandResult> ApplySettingsValueAsync(
        CodingAgentSettingsSnapshot current,
        string settingId,
        string settingValue,
        CancellationToken cancellationToken)
    {
        var normalizedId = settingId.Trim().ToLowerInvariant();
        return normalizedId switch
        {
            CodingAgentSettingsSelector.AutoCompactionAction =>
                SaveAutoCompactionValue(current, settingValue),
            CodingAgentSettingsSelector.TerminalShowImagesAction =>
                SaveBooleanSetting(current, settingValue, "show images", value => current with { TerminalShowImages = value }),
            CodingAgentSettingsSelector.ImagesAutoResizeAction =>
                SaveBooleanSetting(current, settingValue, "auto-resize images", value => current with { ImagesAutoResize = value }),
            CodingAgentSettingsSelector.ImagesBlockImagesAction =>
                SaveBooleanSetting(current, settingValue, "block images", value => current with { ImagesBlockImages = value }),
            CodingAgentSettingsSelector.ShowHardwareCursorAction =>
                SaveBooleanSetting(current, settingValue, "show hardware cursor", value => current with { ShowHardwareCursor = value }),
            CodingAgentSettingsSelector.EditorPaddingAction =>
                SaveBoundedIntSetting(current, settingValue, "editor padding", 0, 3, value => current with { EditorPaddingX = value }),
            CodingAgentSettingsSelector.AutocompleteMaxVisibleAction =>
                SaveBoundedIntSetting(current, settingValue, "autocomplete max items", 3, 20, value => current with { AutocompleteMaxVisible = value }),
            CodingAgentSettingsSelector.TerminalClearOnShrinkAction =>
                SaveBooleanSetting(current, settingValue, "clear on shrink", value => current with { TerminalClearOnShrink = value }),
            CodingAgentSettingsSelector.SteeringModeAction =>
                SaveSteeringModeValue(current, settingValue),
            CodingAgentSettingsSelector.FollowUpModeAction =>
                SaveFollowUpModeValue(current, settingValue),
            CodingAgentSettingsSelector.TreeFilterModeAction =>
                SaveTreeFilterModeValue(current, settingValue),
            CodingAgentSettingsSelector.ThinkingLevelAction =>
                SaveThinkingLevelValue(settingValue),
            CodingAgentSettingsSelector.QuietStartupAction =>
                SaveBooleanSetting(current, settingValue, "quiet startup", value => current with { QuietStartup = value }),
            CodingAgentSettingsSelector.CollapseChangelogAction =>
                SaveBooleanSetting(current, settingValue, "collapse changelog", value => current with { CollapseChangelog = value }),
            CodingAgentSettingsSelector.InstallTelemetryAction =>
                SaveBooleanSetting(current, settingValue, "install telemetry", value => current with { EnableInstallTelemetry = value }),
            CodingAgentSettingsSelector.ScopedModelsAction =>
                await SelectScopedModelsFromSettingsAsync(current, cancellationToken).ConfigureAwait(false),
            CodingAgentSettingsSelector.ThemeAction =>
                await SelectThemeAsync(current, cancellationToken).ConfigureAwait(false),
            _ => CodingAgentCommandResult.Error($"settings selector returned unsupported action '{settingId}'")
        };
    }

    private async Task<CodingAgentCommandResult> SelectScopedModelsFromSettingsAsync(
        CodingAgentSettingsSnapshot settings,
        CancellationToken cancellationToken)
    {
        var availableModels = GetAvailableModels();
        if (availableModels.Count == 0)
        {
            return CodingAgentCommandResult.Error("scoped models: no models available");
        }

        return await SelectScopedModelsAsync(settings, availableModels, cancellationToken).ConfigureAwait(false);
    }

    private CodingAgentCommandResult SaveAutoCompactionSelection(CodingAgentSettingsSnapshot current)
    {
        var next = !ResolveAutoCompactionEnabled(current.AutoCompactionEnabled);
        _settingsStore?.Save(current with { AutoCompactionEnabled = next });
        _autoCompactionChanged?.Invoke(next);
        return CodingAgentCommandResult.Status($"auto compaction: {FormatSettingsAutoCompaction(next)}");
    }

    private CodingAgentCommandResult SaveAutoCompactionValue(CodingAgentSettingsSnapshot current, string value)
    {
        if (!TryParseSettingsBoolean(value, out var enabled))
        {
            return CodingAgentCommandResult.Error($"settings selector returned unsupported boolean '{value}'");
        }

        _settingsStore?.Save(current with { AutoCompactionEnabled = enabled });
        _autoCompactionChanged?.Invoke(enabled);
        return CodingAgentCommandResult.Status($"auto compaction: {FormatSettingsAutoCompaction(enabled)}");
    }

    private CodingAgentCommandResult SaveSteeringModeSelection(CodingAgentSettingsSnapshot current)
    {
        var next = ToggleQueueMode(current.SteeringMode);
        _runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(next);
        _settingsStore?.Save(current with { SteeringMode = next });
        return CodingAgentCommandResult.Status($"steering mode: {next}");
    }

    private CodingAgentCommandResult SaveSteeringModeValue(CodingAgentSettingsSnapshot current, string value)
    {
        if (!CodingAgentQueueModes.TryNormalize(value, out var mode))
        {
            return CodingAgentCommandResult.Error($"settings selector returned unsupported steering mode '{value}'");
        }

        _runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(mode);
        _settingsStore?.Save(current with { SteeringMode = mode });
        return CodingAgentCommandResult.Status($"steering mode: {mode}");
    }

    private CodingAgentCommandResult SaveFollowUpModeSelection(CodingAgentSettingsSnapshot current)
    {
        var next = ToggleQueueMode(current.FollowUpMode);
        _runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(next);
        _settingsStore?.Save(current with { FollowUpMode = next });
        return CodingAgentCommandResult.Status($"follow-up mode: {next}");
    }

    private CodingAgentCommandResult SaveFollowUpModeValue(CodingAgentSettingsSnapshot current, string value)
    {
        if (!CodingAgentQueueModes.TryNormalize(value, out var mode))
        {
            return CodingAgentCommandResult.Error($"settings selector returned unsupported follow-up mode '{value}'");
        }

        _runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(mode);
        _settingsStore?.Save(current with { FollowUpMode = mode });
        return CodingAgentCommandResult.Status($"follow-up mode: {mode}");
    }

    private CodingAgentCommandResult SaveTreeFilterModeSelection(CodingAgentSettingsSnapshot current)
    {
        var next = CycleTreeFilterMode(current.TreeFilterMode);
        _settingsStore?.Save(current with { TreeFilterMode = IsDefaultTreeFilterMode(next) ? null : next });
        return CodingAgentCommandResult.Status($"tree filter: {next}");
    }

    private CodingAgentCommandResult SaveTreeFilterModeValue(CodingAgentSettingsSnapshot current, string value)
    {
        if (!TryParseTreeFilterMode(value, out var mode))
        {
            return CodingAgentCommandResult.Error($"settings selector returned unsupported tree filter '{value}'");
        }

        var next = FormatTreeFilterModeRaw(mode);
        _settingsStore?.Save(current with { TreeFilterMode = IsDefaultTreeFilterMode(next) ? null : next });
        return CodingAgentCommandResult.Status($"tree filter: {next}");
    }

    private CodingAgentCommandResult SaveThinkingLevelSelection(CodingAgentSettingsSnapshot current)
    {
        _runner.ThinkingLevel = CodingAgentThinkingLevels.CycleForModel(_runner.Model, _runner.ThinkingLevel);
        var next = FormatThinkingLevelRaw(_runner.ThinkingLevel);
        _settingsStore?.Save(current with { DefaultThinkingLevel = next });
        return CodingAgentCommandResult.Status($"thinking: {FormatThinkingLevel(_runner.ThinkingLevel)}");
    }

    private CodingAgentCommandResult SaveThinkingLevelValue(string value)
    {
        if (!CodingAgentThinkingLevels.TryParse(value, out var requested))
        {
            return CodingAgentCommandResult.Error($"settings selector returned unsupported thinking level '{value}'");
        }

        var effective = SetAndSaveThinkingLevel(requested);
        return CodingAgentCommandResult.Status($"thinking: {FormatThinkingLevel(effective)}");
    }

    private CodingAgentCommandResult SaveBooleanSetting(
        CodingAgentSettingsSnapshot current,
        string value,
        string label,
        Func<bool, CodingAgentSettingsSnapshot> update)
    {
        if (!TryParseSettingsBoolean(value, out var enabled))
        {
            return CodingAgentCommandResult.Error($"settings selector returned unsupported boolean '{value}'");
        }

        _settingsStore?.Save(update(enabled));
        return CodingAgentCommandResult.Status($"{label}: {FormatSettingsBoolean(enabled)}");
    }

    private CodingAgentCommandResult SaveBoundedIntSetting(
        CodingAgentSettingsSnapshot current,
        string value,
        string label,
        int min,
        int max,
        Func<int, CodingAgentSettingsSnapshot> update)
    {
        if (!int.TryParse(value, out var parsed) || parsed < min || parsed > max)
        {
            return CodingAgentCommandResult.Error($"settings selector returned unsupported {label} value '{value}'");
        }

        _settingsStore?.Save(update(parsed));
        return CodingAgentCommandResult.Status($"{label}: {parsed}");
    }

    private async Task<CodingAgentCommandResult> SelectThemeAsync(
        CodingAgentSettingsSnapshot settings,
        CancellationToken cancellationToken)
    {
        if (_themeStore is null)
        {
            return CodingAgentCommandResult.Error("theme discovery is not available in this session");
        }

        if (_themeSelector is null)
        {
            return CodingAgentCommandResult.Error("theme selector is not available in this session");
        }

        var status = _themeStore.LoadStatus();
        if (status.Themes.Count == 0)
        {
            return CodingAgentCommandResult.Error("no themes available");
        }

        var selected = await _themeSelector(
                status,
                FormatCurrentTheme(settings.Theme),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return CodingAgentCommandResult.Status("theme selection cancelled");
        }

        var theme = status.Themes.FirstOrDefault(candidate =>
            candidate.Name.Equals(selected.Trim(), StringComparison.OrdinalIgnoreCase));
        if (theme is null)
        {
            return CodingAgentCommandResult.Error($"theme '{selected.Trim()}' is not available");
        }

        _settingsStore?.Save(settings with { Theme = theme.Name });
        return CodingAgentCommandResult.Status($"theme: {theme.Name}");
    }

    private string FormatSettings(CodingAgentSettingsSnapshot settings)
    {
        var lines = new List<string>
        {
            $"settings: {_settingsStore?.Path ?? "unavailable"}",
            $"current model: {_runner.Model.Provider}/{_runner.Model.Id}",
            $"current thinking: {FormatThinkingLevel(_runner.ThinkingLevel)}",
            $"default model: {FormatSettingsDefaultModel(settings)}",
            $"tree filter: {FormatTreeFilterSetting(settings.TreeFilterMode)}",
            $"retry: {FormatRetryPolicy(CodingAgentRetryOptions.FromSettingsOrEnvironment(settings))}",
            $"default thinking: {FormatSettingsThinkingLevel(settings.DefaultThinkingLevel)}",
            $"steering mode: {CodingAgentQueueModes.NormalizeOrDefault(settings.SteeringMode)}",
            $"follow-up mode: {CodingAgentQueueModes.NormalizeOrDefault(settings.FollowUpMode)}",
            $"auto compaction: {FormatSettingsAutoCompaction(settings.AutoCompactionEnabled)}",
            $"theme: {FormatThemeSetting(settings.Theme)}",
            $"scoped models: {FormatSettingsScopedModels(settings.EnabledModels)}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<CodingAgentCommandResult> HandleThemeCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (_settingsStore is null)
        {
            return CodingAgentCommandResult.Error("theme settings are not available in this session");
        }

        var settings = _settingsStore.Load();
        if (parts.Count == 1 || (parts.Count == 2 && IsCurrentKeyword(parts[1])))
        {
            return CodingAgentCommandResult.Status(FormatThemeCurrent(settings.Theme));
        }

        if (parts.Count == 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return FormatThemeList(settings.Theme);
        }

        if (parts.Count == 2 && parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _settingsStore.Save(settings with { Theme = null });
            return CodingAgentCommandResult.Status($"theme: {CodingAgentThemeStore.DefaultThemeName}");
        }

        if (parts.Count == 2 && parts[1].Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            return await SelectThemeAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        if (parts.Count == 3 && parts[1].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            if (_themeStore is null)
            {
                return CodingAgentCommandResult.Error("theme discovery is not available in this session");
            }

            var requested = parts[2].Trim();
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/theme"));
            }

            var status = _themeStore.LoadStatus();
            var theme = status.Themes.FirstOrDefault(candidate =>
                candidate.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (theme is null)
            {
                return CodingAgentCommandResult.Error($"theme '{requested}' is not available");
            }

            _settingsStore.Save(settings with { Theme = theme.Name });
            return CodingAgentCommandResult.Status($"theme: {theme.Name}");
        }

        return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/theme"));
    }

    private CodingAgentCommandResult FormatThemeList(string? currentTheme)
    {
        if (_themeStore is null)
        {
            return CodingAgentCommandResult.Error("theme discovery is not available in this session");
        }

        var status = _themeStore.LoadStatus();
        var current = FormatCurrentTheme(currentTheme);
        var lines = new List<string> { $"themes: {status.Themes.Count}, current {current}" };
        foreach (var theme in status.Themes)
        {
            var marker = theme.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? "*" : "-";
            var location = theme.FilePath is null ? theme.Scope : $"{theme.Scope} {theme.FilePath}";
            lines.Add($"{marker} {theme.Name} ({location})");
        }

        if (status.Diagnostics.Count > 0)
        {
            lines.Add($"issues: {status.Diagnostics.Count}");
            foreach (var diagnostic in status.Diagnostics.Take(8))
            {
                lines.Add($"{diagnostic.Severity} {diagnostic.Path} ({diagnostic.Scope}) - {diagnostic.Message}");
            }

            if (status.Diagnostics.Count > 8)
            {
                lines.Add($"... {status.Diagnostics.Count - 8} more issues");
            }
        }

        return CodingAgentCommandResult.Status(string.Join(Environment.NewLine, lines));
    }

    private string FormatThemeCurrent(string? currentTheme)
    {
        var current = FormatCurrentTheme(currentTheme);
        if (_themeStore is null)
        {
            return $"theme: {current}";
        }

        var theme = _themeStore.Find(current);
        if (theme is null)
        {
            return $"theme: {current} (not found)";
        }

        var location = theme.FilePath is null ? theme.Scope : $"{theme.Scope} {theme.FilePath}";
        return $"theme: {theme.Name} ({location})";
    }

    private CodingAgentCommandResult HandleNameCommand(string input, IReadOnlyList<string> parts)
    {
        if (parts.Count == 1)
        {
            return CodingAgentCommandResult.Status($"session name: {FormatSessionName(_runner.SessionName)}");
        }

        var name = input[(input.IndexOf(' ') + 1)..].Trim();
        _runner.SessionName = name.Equals("clear", StringComparison.OrdinalIgnoreCase)
            ? null
            : name;
        return CodingAgentCommandResult.Status($"session name: {FormatSessionName(_runner.SessionName)}");
    }

    private async Task<CodingAgentCommandResult> HandleCopyCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/copy"));
        }

        var text = GetLastAssistantText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return CodingAgentCommandResult.Error("no assistant text to copy");
        }

        await _clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
        return CodingAgentCommandResult.Status("copied last assistant message to clipboard");
    }

    private CodingAgentCommandResult HandleFilesCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/files"));
        }

        var files = CodingAgentFileOperationTracker.Collect(_runner.Messages);
        if (files.Count == 0)
        {
            return CodingAgentCommandResult.Status("files: none");
        }

        var lines = files.Select(file => $"{file.Path} ({CodingAgentFileOperationTracker.FormatCounts(file)})");
        return CodingAgentCommandResult.Status($"files:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    private CodingAgentCommandResult HandleExportCommand(string input, IReadOnlyList<string> parts)
    {
        var exportPath = parts.Count == 1
            ? GetDefaultHtmlExportPath()
            : input[(input.IndexOf(' ') + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/export"));
        }

        if (CodingAgentHtmlSessionExporter.IsHtmlPath(exportPath))
        {
            var htmlPath = ExportHtmlTranscript(exportPath);
            return CodingAgentCommandResult.Status($"exported session transcript to {htmlPath}");
        }

        if (CodingAgentTreeSessionStore.IsJsonlPath(exportPath))
        {
            if (_treeSessionController is null)
            {
                return CodingAgentCommandResult.Error("JSONL export requires an active tree session");
            }

            _treeSessionController.SyncFromRunner(_runner);
            var treeExportPath = _treeSessionController.ExportCurrentBranch(exportPath);
            return CodingAgentCommandResult.Status($"exported session branch to {treeExportPath}");
        }

        var store = new CodingAgentSessionStore(exportPath);
        store.Save(_runner.Messages, _runner.Model, _runner.SessionName);
        return CodingAgentCommandResult.Status($"exported session to {store.Path}");
    }

    private async Task<CodingAgentCommandResult> HandleShareCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/share"));
        }

        if (_runner.Messages.Count == 0)
        {
            return CodingAgentCommandResult.Error("nothing to share yet");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"tau-session-share-{Guid.NewGuid():N}.html");
        try
        {
            var htmlPath = ExportHtmlTranscript(tempPath);
            var result = await _shareClient.ShareAsync(htmlPath, cancellationToken).ConfigureAwait(false);
            return CodingAgentCommandResult.Status($"Share URL: {result.ShareUrl}\nGist: {result.GistUrl}");
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private string ExportHtmlTranscript(string exportPath)
    {
        CodingAgentSessionSnapshot snapshot;
        CodingAgentTreeSessionSummary? treeSummary = null;
        string? sessionJsonl = null;
        if (_treeSessionController is not null)
        {
            _treeSessionController.SyncFromRunner(_runner);
            snapshot = _treeSessionController.LoadSnapshot().ToFlatSnapshot();
            treeSummary = _treeSessionController.GetSummary();
            sessionJsonl = _treeSessionController.ExportCurrentBranchText();
        }
        else
        {
            snapshot = new CodingAgentSessionSnapshot(
                _runner.Messages,
                _runner.Model.Provider,
                _runner.Model.Id,
                _runner.SessionName);
        }

        return CodingAgentHtmlSessionExporter.Export(
            exportPath,
            snapshot.Messages,
            snapshot.Provider ?? _runner.Model.Provider,
            snapshot.Model ?? _runner.Model.Id,
            snapshot.Name ?? _runner.SessionName,
            treeSummary,
            sessionJsonl,
            (_runner as ICodingAgentToolResultDetailsProvider)?.ToolResultDetailsByToolCallId);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<CodingAgentCommandResult> HandleImportCommandAsync(
        string input,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count < 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/import"));
        }

        var importPath = input[(input.IndexOf(' ') + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(importPath))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/import"));
        }

        if (CodingAgentTreeSessionStore.IsJsonlPath(importPath))
        {
            if (_treeSessionController is null)
            {
                return CodingAgentCommandResult.Error("JSONL import requires an active tree session");
            }

            var currentPath = System.IO.Path.GetFullPath(_treeSessionController.Path);
            var importTreePath = System.IO.Path.GetFullPath(importPath);
            if (!importTreePath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
            {
                var switchSummary = await _sessionSwitchCoordinator.TrySummarizeBeforeSwitchAsync(
                        CodingAgentTreeNavigationReason.ImportSession,
                        importTreePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (switchSummary.Cancelled)
                {
                    return CodingAgentCommandResult.Status("import cancelled");
                }

                var importedTreeSnapshot = _treeSessionController.Resume(importPath);
                _runner.RestoreSession(importedTreeSnapshot.ToFlatSnapshot());
                return CodingAgentCommandResult.Status(
                    $"resumed session from {importedTreeSnapshot.FilePath}: {importedTreeSnapshot.Messages.Count} messages, model {_runner.Model.Provider}/{_runner.Model.Id}, name {FormatSessionName(_runner.SessionName)}, leaf {FormatTreeId(importedTreeSnapshot.LeafId)}{FormatSessionSwitchSummarySuffix(switchSummary)}");
            }

            var treeSnapshot = _treeSessionController.Resume(importPath);
            _runner.RestoreSession(treeSnapshot.ToFlatSnapshot());
            return CodingAgentCommandResult.Status(
                $"resumed session from {treeSnapshot.FilePath}: {treeSnapshot.Messages.Count} messages, model {_runner.Model.Provider}/{_runner.Model.Id}, name {FormatSessionName(_runner.SessionName)}, leaf {FormatTreeId(treeSnapshot.LeafId)}");
        }

        var store = new CodingAgentSessionStore(importPath);
        var snapshot = store.LoadStrict();
        _runner.RestoreSession(snapshot);
        _treeSessionController?.ReplaceWithRunnerSession(_runner);
        return CodingAgentCommandResult.Status(
            $"imported session from {store.Path}: {snapshot.Messages.Count} messages, model {_runner.Model.Provider}/{_runner.Model.Id}, name {FormatSessionName(_runner.SessionName)}");
    }

    private async Task<CodingAgentCommandResult> HandleNewCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/new"));
        }

        var switchSummary = await _sessionSwitchCoordinator.TrySummarizeBeforeSwitchAsync(
                CodingAgentTreeNavigationReason.NewSession,
                targetSessionPath: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (switchSummary.Cancelled)
        {
            return CodingAgentCommandResult.Status("new session cancelled");
        }

        _runner.ResetSession();
        _treeSessionController?.StartNewFromRunner(_runner);
        return CodingAgentCommandResult.Status(
            $"started new session with model {_runner.Model.Provider}/{_runner.Model.Id}{FormatSessionSwitchSummarySuffix(switchSummary)}");
    }

    private static CodingAgentCommandResult HandleQuitCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/quit"));
        }

        return CodingAgentCommandResult.Exit("Goodbye!");
    }

    private CodingAgentCommandResult HandleSessionCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/session"));
        }

        var stats = _runner.GetSessionStats(_sessionFile);
        var file = string.IsNullOrWhiteSpace(stats.SessionFile) ? "none" : stats.SessionFile;
        var tokenBudget = FormatTokenBudget(stats);
        if (_treeSessionController is not null)
        {
            _treeSessionController.SyncFromRunner(_runner);
            var tree = _treeSessionController.GetSummary();
            return CodingAgentCommandResult.Status(
                $"session: name {FormatSessionName(stats.SessionName)}, model {stats.Provider}/{stats.Model}, messages {stats.TotalMessages} (user {stats.UserMessages}, assistant {stats.AssistantMessages}, tool {stats.ToolResultMessages}, toolCalls {stats.ToolCalls}), tokens {tokenBudget}, retry {FormatRetryPolicy(_retryOptions)}, file {file}, tree {tree.FilePath}, leaf {FormatTreeId(tree.LeafId)}, entries {tree.EntryCount}, messages {tree.TotalMessageCount}, branch entries {tree.BranchEntryCount}, branch messages {tree.BranchMessageCount}, branches {tree.BranchPointCount}, labels {tree.LabelCount}, cwd {tree.Cwd}{FormatParentSession(tree.ParentSession)}");
        }

        return CodingAgentCommandResult.Status(
            $"session: name {FormatSessionName(stats.SessionName)}, model {stats.Provider}/{stats.Model}, messages {stats.TotalMessages} (user {stats.UserMessages}, assistant {stats.AssistantMessages}, tool {stats.ToolResultMessages}, toolCalls {stats.ToolCalls}), tokens {tokenBudget}, retry {FormatRetryPolicy(_retryOptions)}, file {file}");
    }

    private async Task<CodingAgentCommandResult> HandleMetadataCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/metadata"));
        }

        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        _treeSessionController.SyncFromRunner(_runner);
        var entryId = parts.Count == 2 ? parts[1] : null;
        var snapshot = _treeSessionController.GetMetadataSnapshot(entryId);
        if (_metadataViewer is not null)
        {
            await _metadataViewer(snapshot, cancellationToken).ConfigureAwait(false);
            return new CodingAgentCommandResult(true, false, null);
        }

        return CodingAgentCommandResult.Status(_treeSessionController.FormatMetadata(entryId));
    }

    private async Task<CodingAgentCommandResult> HandleTreeCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        if (!TryParseTreeOptions(parts, GetDefaultTreeFilterMode(), out var options, out var interactive))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/tree"));
        }

        _treeSessionController.SyncFromRunner(_runner);

        if (interactive)
        {
            if (_treeNavigator is null)
            {
                return CodingAgentCommandResult.Error("interactive tree navigator is not available in this mode");
            }

            var items = _treeSessionController.EnumerateView(options with { MaxEntries = int.MaxValue });
            if (items.Count == 0)
            {
                return CodingAgentCommandResult.Status("tree has no entries matching filter");
            }

            string? preferredEntryId = null;
            while (true)
            {
                var result = await _treeNavigator(items, preferredEntryId, cancellationToken).ConfigureAwait(false);
                if (result.FoldedEntryIds is not null)
                {
                    _treeSessionController.AppendTreeFoldState(result.FoldedEntryIds);
                }

                preferredEntryId = result.LabelEditEntryId ?? result.SelectedEntryId ?? preferredEntryId;

                if (!string.IsNullOrWhiteSpace(result.LabelEditEntryId))
                {
                    if (_treeLabelPrompt is null)
                    {
                        return CodingAgentCommandResult.Error("interactive tree label editor is not available in this mode");
                    }

                    var entryId = result.LabelEditEntryId;
                    var promptResult = await _treeLabelPrompt(
                            new CodingAgentTreeLabelPromptState(entryId, _treeSessionController.GetLabel(entryId)),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!promptResult.Cancelled)
                    {
                        _treeSessionController.AppendLabelChange(entryId, NormalizeTreeLabel(promptResult.Label));
                    }

                    items = _treeSessionController.EnumerateView(options with { MaxEntries = int.MaxValue });
                    if (items.Count == 0)
                    {
                        return CodingAgentCommandResult.Status("tree has no entries matching filter");
                    }

                    continue;
                }

                if (result.SelectedEntryId is null)
                {
                    return CodingAgentCommandResult.Status("tree navigator cancelled");
                }

                var selectedItem = items.FirstOrDefault(item =>
                    item.EntryId.Equals(result.SelectedEntryId, StringComparison.OrdinalIgnoreCase));
                if (selectedItem is null)
                {
                    return CodingAgentCommandResult.Error($"selected tree entry '{result.SelectedEntryId}' is no longer available");
                }

                if (selectedItem.IsCurrentLeaf)
                {
                    return CodingAgentCommandResult.Status("Already at this point");
                }

                var navigationTargetId = ShouldUseUserEntryNavigation(selectedItem)
                    ? selectedItem.ParentEntryId
                    : selectedItem.EntryId;
                var navigationDecision = await ResolveTreeNavigationDecisionAsync(navigationTargetId, cancellationToken)
                    .ConfigureAwait(false);
                if (navigationDecision.Cancelled)
                {
                    preferredEntryId = selectedItem.EntryId;
                    items = _treeSessionController.EnumerateView(options with { MaxEntries = int.MaxValue });
                    if (items.Count == 0)
                    {
                        return CodingAgentCommandResult.Status("tree has no entries matching filter");
                    }

                    continue;
                }

                var originalSessionName = _runner.SessionName;
                var navigationLabel = NormalizeTreeLabel(navigationDecision.Label);
                CodingAgentTreeSessionSnapshot snapshot;
                CodingAgentBranchSummaryResult? summary = null;
                if (navigationDecision.Summarize)
                {
                    var summaryMessages = _treeSessionController.CollectBranchSummaryMessages(navigationTargetId);
                    if (summaryMessages.Count > 0)
                    {
                        summary = await _runner
                            .SummarizeBranchAsync(
                                summaryMessages,
                                navigationDecision.CustomInstructions,
                                navigationDecision.ReplaceInstructions,
                                cancellationToken)
                            .ConfigureAwait(false);
                        snapshot = _treeSessionController.BranchWithSummary(navigationTargetId, summary, navigationLabel);
                    }
                    else
                    {
                        snapshot = _treeSessionController.BranchTo(navigationTargetId, navigationLabel);
                    }
                }
                else
                {
                    snapshot = _treeSessionController.BranchTo(navigationTargetId, navigationLabel);
                }

                _runner.RestoreSession(snapshot.ToFlatSnapshot());
                if (snapshot.Name is null && !string.IsNullOrWhiteSpace(originalSessionName))
                {
                    _runner.SessionName = originalSessionName;
                }

                if (ShouldUseUserEntryNavigation(selectedItem))
                {
                    _inputDraftSetter?.Invoke(selectedItem.NavigationDraftText);
                    return CodingAgentCommandResult.Status(
                        $"tree rewound to {FormatTreeId(navigationTargetId)} from user entry {selectedItem.EntryId}: loaded draft, messages {snapshot.Messages.Count}{FormatTreeSummarySuffix(summary)}");
                }

                _inputDraftSetter?.Invoke(null);
                return CodingAgentCommandResult.Status(
                    $"navigated tree to {selectedItem.EntryId}: messages {snapshot.Messages.Count}{FormatTreeSummarySuffix(summary)}");
            }
        }

        return CodingAgentCommandResult.Status(_treeSessionController.FormatTree(options));
    }

    private CodingAgentCommandResult HandleLabelCommand(string input, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/label"));
        }

        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        _treeSessionController.SyncFromRunner(_runner);
        var entryId = parts[1];
        if (parts.Count == 2)
        {
            var current = _treeSessionController.GetLabel(entryId);
            return CodingAgentCommandResult.Status($"label {entryId}: {FormatSessionName(current)}");
        }

        var label = string.Join(" ", parts.Skip(2));
        var normalized = label.Equals("clear", StringComparison.OrdinalIgnoreCase) ? null : label;
        _treeSessionController.AppendLabelChange(entryId, normalized);
        return CodingAgentCommandResult.Status($"label {entryId}: {FormatSessionName(normalized)}");
    }

    private async Task<CodingAgentCommandResult> HandleForkCommandAsync(
        string input,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count < 2 || (parts.Count >= 3 && !IsBranchSummaryOption(parts[2])))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/fork"));
        }

        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        var entryId = parts[1];
        var summarize = parts.Count >= 3;
        _treeSessionController.SyncFromRunner(_runner);
        CodingAgentTreeSessionSnapshot snapshot;
        CodingAgentBranchSummaryResult? summary = null;
        if (summarize)
        {
            var messages = _treeSessionController.CollectBranchSummaryMessages(entryId);
            if (messages.Count > 0)
            {
                summary = await _runner
                    .SummarizeBranchAsync(
                        messages,
                        ExtractBranchSummaryInstructions(parts),
                        replaceInstructions: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                snapshot = _treeSessionController.BranchWithSummary(entryId, summary);
            }
            else
            {
                snapshot = _treeSessionController.Branch(entryId);
            }
        }
        else
        {
            snapshot = _treeSessionController.Branch(entryId);
        }

        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        var summarySuffix = summary is null
            ? summarize ? ", branch summary none" : string.Empty
            : $", branch summary {summary.EntryCount} entries, tokens ~{summary.TokensBefore}";
        return CodingAgentCommandResult.Status(
            $"forked session at {entryId}: leaf {FormatTreeId(snapshot.LeafId)}, messages {snapshot.Messages.Count}, model {_runner.Model.Provider}/{_runner.Model.Id}{summarySuffix}");
    }

    private CodingAgentCommandResult HandleCloneCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/clone"));
        }

        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        _treeSessionController.SyncFromRunner(_runner);
        var snapshot = _treeSessionController.CloneCurrentBranch();
        if (snapshot is null)
        {
            return CodingAgentCommandResult.Status("Nothing to clone yet");
        }

        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        return CodingAgentCommandResult.Status(
            $"cloned session to {snapshot.FilePath}: leaf {FormatTreeId(snapshot.LeafId)}, messages {snapshot.Messages.Count}, model {_runner.Model.Provider}/{_runner.Model.Id}");
    }

    private async Task<CodingAgentCommandResult> HandleResumeCommandAsync(
        string input,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/resume"));
        }

        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        _treeSessionController.SyncFromRunner(_runner);

        if (parts.Count == 1 && _resumeSelector is not null)
        {
            return await HandleInteractiveResumeSelectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var resumeTarget = parts.Count == 1
            ? "latest"
            : input[(input.IndexOf(' ') + 1)..].Trim();
        var path = resumeTarget.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? CodingAgentTreeSessionStore.FindMostRecentSession(_treeSessionController.Path)
            : resumeTarget;
        if (string.IsNullOrWhiteSpace(path))
        {
            return CodingAgentCommandResult.Error("no JSONL session found to resume");
        }

        var currentPath = System.IO.Path.GetFullPath(_treeSessionController.Path);
        var selectedPath = System.IO.Path.GetFullPath(path);
        if (selectedPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return CodingAgentCommandResult.Status("Already in this session");
        }

        var switchSummary = await _sessionSwitchCoordinator.TrySummarizeBeforeSwitchAsync(
                CodingAgentTreeNavigationReason.ResumeSession,
                selectedPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (switchSummary.Cancelled)
        {
            return CodingAgentCommandResult.Status("resume switch cancelled");
        }

        var snapshot = _treeSessionController.Resume(path);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        return CodingAgentCommandResult.Status(
            $"resumed session from {snapshot.FilePath}: {snapshot.Messages.Count} messages, model {_runner.Model.Provider}/{_runner.Model.Id}, name {FormatSessionName(_runner.SessionName)}, leaf {FormatTreeId(snapshot.LeafId)}{FormatSessionSwitchSummarySuffix(switchSummary)}");
    }

    private async Task<CodingAgentCommandResult> HandleInteractiveResumeSelectionAsync(CancellationToken cancellationToken)
    {
        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        if (_resumeSelector is null)
        {
            return CodingAgentCommandResult.Error("resume selector is not available in this session");
        }

        var state = new CodingAgentResumeSelectorState(
            _treeSessionController.Path,
            Environment.CurrentDirectory,
            CodingAgentTreeSessionStore.ListAvailableSessions(_treeSessionController.Path));
        if (state.Sessions.Count == 0)
        {
            return CodingAgentCommandResult.Error("no JSONL session found to resume");
        }

        var selection = await _resumeSelector(state, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(selection.RenamedCurrentSessionName))
        {
            _runner.SessionName = selection.RenamedCurrentSessionName;
        }

        var selectedPath = selection.SelectedPath;
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return CodingAgentCommandResult.Status("resume selection cancelled");
        }

        var currentPath = System.IO.Path.GetFullPath(_treeSessionController.Path);
        var normalizedSelectedPath = System.IO.Path.GetFullPath(selectedPath);
        if (normalizedSelectedPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return CodingAgentCommandResult.Status("Already in this session");
        }

        var switchSummary = await _sessionSwitchCoordinator.TrySummarizeBeforeSwitchAsync(
                CodingAgentTreeNavigationReason.ResumeSession,
                normalizedSelectedPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (switchSummary.Cancelled)
        {
            return CodingAgentCommandResult.Status("resume switch cancelled");
        }

        var snapshot = _treeSessionController.Resume(normalizedSelectedPath);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        return CodingAgentCommandResult.Status(
            $"resumed session from {snapshot.FilePath}: {snapshot.Messages.Count} messages, model {_runner.Model.Provider}/{_runner.Model.Id}, name {FormatSessionName(_runner.SessionName)}, leaf {FormatTreeId(snapshot.LeafId)}{FormatSessionSwitchSummarySuffix(switchSummary)}");
    }

    public CodingAgentCommandResult CycleModel(string direction = "forward")
    {
        var normalizedDirection = string.IsNullOrWhiteSpace(direction) ? "forward" : direction.Trim();
        var isBackward = normalizedDirection.Equals("backward", StringComparison.OrdinalIgnoreCase);
        if (!isBackward && !normalizedDirection.Equals("forward", StringComparison.OrdinalIgnoreCase))
        {
            return CodingAgentCommandResult.Error("model cycle direction must be 'forward' or 'backward'");
        }

        var candidates = GetModelCycleCandidates(out var isScoped);
        if (candidates.Count == 0)
        {
            return CodingAgentCommandResult.Error("No models with configured auth are available. Use /login or configure provider credentials.");
        }

        if (candidates.Count == 1)
        {
            return CodingAgentCommandResult.Status(isScoped ? "Only one model in scope" : "Only one model available");
        }

        var currentIndex = candidates.FindIndex(candidate => SameModel(candidate.Model, _runner.Model));
        var nextIndex = currentIndex < 0
            ? 0
            : isBackward
                ? (currentIndex - 1 + candidates.Count) % candidates.Count
                : (currentIndex + 1) % candidates.Count;
        var next = candidates[nextIndex];
        var selected = _runner.SelectModel(next.Model.Provider, next.Model.Id);
        SaveDefaultModel(selected);
        ApplyScopedThinkingOverride(next.ThinkingLevel);
        _treeSessionController?.SyncFromRunner(_runner);
        var scopeSuffix = FormatModelCycleScopeSuffix(isScoped, next.ThinkingLevel, _runner.ThinkingLevel);
        return CodingAgentCommandResult.Status($"model: {selected.Provider}/{selected.Id}{scopeSuffix}");
    }

    public async Task<CodingAgentCommandResult> SelectModelAsync(
        string? initialFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (_modelSelector is null)
        {
            return CodingAgentCommandResult.Error("model selector is not available in this session");
        }

        var registeredModels = GetAvailableModels();
        var availableModels = GetAuthConfiguredModels(registeredModels);
        if (availableModels.Count == 0)
        {
            return CodingAgentCommandResult.Error("model selector has no models with configured auth");
        }

        var scopedModels = GetModelCycleCandidates(registeredModels, availableModels, out var isScoped)
            .Select(entry => entry.Model)
            .ToArray();
        var selected = await _modelSelector(
                new CodingAgentModelSelectorState(
                    availableModels,
                    isScoped ? scopedModels : null,
                    _runner.Model,
                    initialFilter),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return CodingAgentCommandResult.Status("model selection cancelled");
        }

        if (!TryResolveScopedModelId(selected.Trim(), registeredModels, out var id, out var error))
        {
            return CodingAgentCommandResult.Error(error ?? $"model selector returned unsupported model '{selected.Trim()}'");
        }

        if (!availableModels.Any(model => FormatScopedModelId(model).Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            return CodingAgentCommandResult.Error($"model '{id}' does not have configured auth");
        }

        var model = SelectConfiguredModel(id);
        SaveDefaultModel(model);
        ClampCurrentThinkingLevel();
        _treeSessionController?.SyncFromRunner(_runner);
        return CodingAgentCommandResult.Status($"model: {model.Provider}/{model.Id}");
    }

    private async Task<CodingAgentCommandResult> HandleModelCommandAsync(
        string input,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count == 1)
        {
            return _modelSelector is null
                ? CodingAgentCommandResult.Status($"model: {_runner.Model.Provider}/{_runner.Model.Id}")
                : await SelectModelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (IsCurrentKeyword(parts[1]))
        {
            return CodingAgentCommandResult.Status($"model: {_runner.Model.Provider}/{_runner.Model.Id}");
        }

        if (parts[1].Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            var search = input.StartsWith("/model select ", StringComparison.OrdinalIgnoreCase)
                ? input["/model select ".Length..].Trim()
                : null;
            return await SelectModelAsync(
                    string.IsNullOrWhiteSpace(search) ? null : search,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string selectedId;
        if (parts.Count == 2)
        {
            if (!TryResolveScopedModelId(parts[1], GetAvailableModels(), out selectedId, out var error))
            {
                return CodingAgentCommandResult.Error(error ?? $"model '{parts[1]}' is not registered");
            }
        }
        else if (parts.Count == 3)
        {
            selectedId = $"{parts[1]}/{parts[2]}";
        }
        else
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/model"));
        }

        var selected = SelectConfiguredModel(selectedId);
        SaveDefaultModel(selected);
        ClampCurrentThinkingLevel();
        _treeSessionController?.SyncFromRunner(_runner);
        return CodingAgentCommandResult.Status($"model: {selected.Provider}/{selected.Id}");
    }

    private CodingAgentCommandResult HandleProviderCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count == 1 || IsCurrentKeyword(parts[1]))
        {
            return CodingAgentCommandResult.Status($"provider: {_runner.Model.Provider}");
        }

        if (parts.Count != 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/provider"));
        }

        var selected = SelectConfiguredProviderDefaultModel(parts[1]);
        SaveDefaultModel(selected);
        ClampCurrentThinkingLevel();
        _treeSessionController?.SyncFromRunner(_runner);
        return CodingAgentCommandResult.Status($"model: {selected.Provider}/{selected.Id}");
    }

    private CodingAgentCommandResult HandleModelsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/models"));
        }

        var provider = parts.Count == 2 ? parts[1] : _runner.Model.Provider;
        var models = _runner.GetModels(provider);
        if (models.Count == 0)
        {
            return CodingAgentCommandResult.Error($"provider '{provider}' has no registered models");
        }

        return CodingAgentCommandResult.Status($"models {provider}: {string.Join(", ", models.Select(model => model.Id))}");
    }

    private CodingAgentCommandResult HandleProvidersCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/providers"));
        }

        return CodingAgentCommandResult.Status($"providers: {string.Join(", ", _runner.GetProviders())}");
    }

    private async Task<CodingAgentCommandResult> HandleScopedModelsCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (_settingsStore is null)
        {
            return CodingAgentCommandResult.Error("scoped model settings are not available in this session");
        }

        var settings = _settingsStore.Load();
        var availableModels = GetAvailableModels();
        if (availableModels.Count == 0)
        {
            return CodingAgentCommandResult.Error("scoped models: no models available");
        }

        if (parts.Count == 1)
        {
            if (_scopedModelsSelector is null)
            {
                return CodingAgentCommandResult.Status(FormatScopedModels(settings.EnabledModels, availableModels));
            }

            return await SelectScopedModelsAsync(settings, availableModels, cancellationToken).ConfigureAwait(false);
        }

        if (parts.Count == 2 && IsCurrentKeyword(parts[1]))
        {
            return CodingAgentCommandResult.Status(FormatScopedModels(settings.EnabledModels, availableModels));
        }

        if (parts.Count == 2 && parts[1].Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            if (_scopedModelsSelector is null)
            {
                return CodingAgentCommandResult.Error("scoped model selector is not available in this session");
            }

            return await SelectScopedModelsAsync(settings, availableModels, cancellationToken).ConfigureAwait(false);
        }

        var action = parts[1].ToLowerInvariant();
        if (action is "clear" or "all")
        {
            if (parts.Count != 2)
            {
                return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/scoped-models"));
            }

            SaveScopedModels(settings, null);
            return CodingAgentCommandResult.Status(FormatScopedModels(null, availableModels));
        }

        if (action is not ("set" or "add" or "remove") || parts.Count < 3)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/scoped-models"));
        }

        var resolved = ResolveScopedModelEntries(parts.Skip(2), availableModels, out var error);
        if (error is not null)
        {
            return CodingAgentCommandResult.Error(error);
        }

        var next = action switch
        {
            "set" => resolved,
            "add" => AddScopedModelEntries(settings.EnabledModels, resolved, availableModels),
            "remove" => RemoveScopedModelEntries(settings.EnabledModels, resolved, availableModels),
            _ => resolved
        };

        if (next.Count == 0)
        {
            return CodingAgentCommandResult.Error("scoped model scope cannot be empty; use /scoped-models clear to enable all models");
        }

        var normalized = NormalizeScopedModelEntries(next, availableModels);
        var saved = ToSavedScopedModelPatterns(normalized, availableModels);
        SaveScopedModels(settings, saved);
        return CodingAgentCommandResult.Status(FormatScopedModels(saved, availableModels));
    }

    private async Task<CodingAgentCommandResult> SelectScopedModelsAsync(
        CodingAgentSettingsSnapshot settings,
        IReadOnlyList<Model> availableModels,
        CancellationToken cancellationToken)
    {
        if (_scopedModelsSelector is null)
        {
            return CodingAgentCommandResult.Error("scoped model selector is not available in this session");
        }

        var selection = await _scopedModelsSelector(
                new CodingAgentScopedModelsSelectorState(
                    _settingsStore?.Path ?? "unavailable",
                    availableModels,
                    settings.EnabledModels,
                    _runner.Model),
                cancellationToken)
            .ConfigureAwait(false);
        if (selection.IsCancelled)
        {
            return CodingAgentCommandResult.Status("scoped model selection cancelled");
        }

        IReadOnlyList<string>? saved = null;
        if (selection.EnabledModels is { Count: > 0 } selected)
        {
            var selectedEntries = ResolveScopedModelEntries(selected, availableModels, out var error);
            if (error is not null)
            {
                return CodingAgentCommandResult.Error(error);
            }

            var normalized = MergeSelectedScopedModelEntries(selectedEntries, settings.EnabledModels, availableModels);
            if (normalized.Count == 0)
            {
                return CodingAgentCommandResult.Error("scoped model selector returned no registered models");
            }

            saved = ToSavedScopedModelPatterns(normalized, availableModels);
        }

        SaveScopedModels(_settingsStore?.Load() ?? settings, saved);
        return CodingAgentCommandResult.Status(FormatScopedModels(saved, availableModels));
    }

    private IReadOnlyList<Model> GetAvailableModels()
    {
        return CodingAgentModelAvailability.GetRegisteredModels(_runner);
    }

    private IReadOnlyList<Model> GetAuthConfiguredModels(IReadOnlyList<Model>? registeredModels = null)
    {
        return CodingAgentModelAvailability.GetAuthConfiguredModels(_runner, registeredModels);
    }

    private List<CodingAgentScopedModelEntry> GetModelCycleCandidates(out bool isScoped)
    {
        var registeredModels = GetAvailableModels();
        var availableModels = GetAuthConfiguredModels(registeredModels);
        return GetModelCycleCandidates(registeredModels, availableModels, out isScoped);
    }

    private List<CodingAgentScopedModelEntry> GetModelCycleCandidates(
        IReadOnlyList<Model> registeredModels,
        IReadOnlyList<Model> availableModels,
        out bool isScoped)
    {
        var enabledModels = _settingsStore?.Load().EnabledModels;
        if (enabledModels is null || enabledModels.Count == 0)
        {
            isScoped = false;
            return availableModels
                .Select(static model => new CodingAgentScopedModelEntry(model, null))
                .ToList();
        }

        var candidates = new List<CodingAgentScopedModelEntry>();
        foreach (var enabledModel in enabledModels)
        {
            if (!CodingAgentScopedModelPatterns.TryResolve(enabledModel, registeredModels, out var entry, out _))
            {
                continue;
            }

            var model = availableModels.FirstOrDefault(candidate =>
                SameModel(candidate, entry.Model));
            if (model is not null && !candidates.Any(candidate => SameModel(candidate.Model, model)))
            {
                candidates.Add(new CodingAgentScopedModelEntry(model, entry.ThinkingLevel));
            }
        }

        if (candidates.Count == 0)
        {
            isScoped = false;
            return availableModels
                .Select(static model => new CodingAgentScopedModelEntry(model, null))
                .ToList();
        }

        isScoped = true;
        return candidates;
    }

    private Model SelectConfiguredModel(string modelReference)
    {
        var registeredModels = GetAvailableModels();
        if (!TryResolveScopedModelId(modelReference, registeredModels, out var id, out var error))
        {
            throw new KeyNotFoundException(error ?? $"model '{modelReference}' is not registered");
        }

        var availableModels = GetAuthConfiguredModels(registeredModels);
        if (!availableModels.Any(model => FormatScopedModelId(model).Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"model '{id}' does not have configured auth");
        }

        var parts = id.Split('/', 2, StringSplitOptions.TrimEntries);
        return _runner.SelectModel(parts[0], parts[1]);
    }

    private Model SelectConfiguredProviderDefaultModel(string provider)
    {
        var registeredModels = GetAvailableModels();
        var model = registeredModels.FirstOrDefault(candidate =>
            candidate.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            throw new KeyNotFoundException($"Provider '{provider}' is not registered.");
        }

        if (!GetAuthConfiguredModels(registeredModels).Any(candidate => SameModel(candidate, model)))
        {
            throw new InvalidOperationException($"provider '{model.Provider}' does not have configured auth");
        }

        return _runner.SelectModel(model.Provider, model.Id);
    }

    private static IReadOnlyList<CodingAgentScopedModelEntry> ResolveScopedModelEntries(
        IEnumerable<string> references,
        IReadOnlyList<Model> availableModels,
        out string? error)
    {
        error = null;
        var entries = new List<CodingAgentScopedModelEntry>();
        foreach (var reference in references)
        {
            var trimmed = reference.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!CodingAgentScopedModelPatterns.TryResolve(trimmed, availableModels, out var entry, out error))
            {
                return [];
            }

            if (!entries.Any(existing => SameModel(existing.Model, entry.Model)))
            {
                entries.Add(entry);
            }
        }

        if (entries.Count == 0)
        {
            error = CodingAgentCommandCatalog.Usage("/scoped-models");
        }

        return entries;
    }

    private static bool TryResolveScopedModelId(
        string reference,
        IReadOnlyList<Model> availableModels,
        out string id,
        out string? error)
    {
        id = string.Empty;
        error = null;
        if (reference.Contains('/', StringComparison.Ordinal))
        {
            var parts = reference.Split('/', 2, StringSplitOptions.TrimEntries);
            var match = availableModels.SingleOrDefault(model =>
                model.Provider.Equals(parts[0], StringComparison.OrdinalIgnoreCase) &&
                model.Id.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"model '{reference}' is not registered";
                return false;
            }

            id = FormatScopedModelId(match);
            return true;
        }

        var matches = availableModels
            .Where(model => model.Id.Equals(reference, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            error = $"model '{reference}' is not registered";
            return false;
        }

        if (matches.Length > 1)
        {
            error = $"model '{reference}' is ambiguous; use provider/model";
            return false;
        }

        id = FormatScopedModelId(matches[0]);
        return true;
    }

    private static IReadOnlyList<CodingAgentScopedModelEntry> AddScopedModelEntries(
        IReadOnlyList<string>? current,
        IReadOnlyList<CodingAgentScopedModelEntry> additions,
        IReadOnlyList<Model> availableModels)
    {
        var next = GetCurrentScopedModelEntries(current, availableModels).ToList();
        foreach (var addition in additions)
        {
            var existingIndex = next.FindIndex(entry => SameModel(entry.Model, addition.Model));
            if (existingIndex < 0)
            {
                next.Add(addition);
                continue;
            }

            if (addition.ThinkingLevel is not null)
            {
                next[existingIndex] = addition;
            }
        }

        return next;
    }

    private static IReadOnlyList<CodingAgentScopedModelEntry> RemoveScopedModelEntries(
        IReadOnlyList<string>? current,
        IReadOnlyList<CodingAgentScopedModelEntry> removals,
        IReadOnlyList<Model> availableModels)
    {
        return GetCurrentScopedModelEntries(current, availableModels)
            .Where(entry => !removals.Any(removal => SameModel(removal.Model, entry.Model)))
            .ToArray();
    }

    private static IReadOnlyList<CodingAgentScopedModelEntry> NormalizeScopedModelEntries(
        IReadOnlyList<CodingAgentScopedModelEntry> entries,
        IReadOnlyList<Model> availableModels)
    {
        var normalized = new List<CodingAgentScopedModelEntry>();
        foreach (var entry in entries)
        {
            var model = availableModels.FirstOrDefault(candidate => SameModel(candidate, entry.Model));
            if (model is null || normalized.Any(existing => SameModel(existing.Model, model)))
            {
                continue;
            }

            normalized.Add(new CodingAgentScopedModelEntry(model, entry.ThinkingLevel));
        }

        return normalized;
    }

    private static IReadOnlyList<CodingAgentScopedModelEntry> MergeSelectedScopedModelEntries(
        IReadOnlyList<CodingAgentScopedModelEntry> selected,
        IReadOnlyList<string>? current,
        IReadOnlyList<Model> availableModels)
    {
        var existing = GetCurrentScopedModelEntries(current, availableModels);
        return selected
            .Select(entry =>
            {
                var existingEntry = existing.FirstOrDefault(candidate => SameModel(candidate.Model, entry.Model));
                return new CodingAgentScopedModelEntry(
                    entry.Model,
                    entry.ThinkingLevel ?? existingEntry.ThinkingLevel);
            })
            .ToArray();
    }

    private static IReadOnlyList<CodingAgentScopedModelEntry> GetCurrentScopedModelEntries(
        IReadOnlyList<string>? current,
        IReadOnlyList<Model> availableModels)
    {
        if (current is null || current.Count == 0)
        {
            return availableModels
                .Select(static model => new CodingAgentScopedModelEntry(model, null))
                .ToArray();
        }

        var entries = new List<CodingAgentScopedModelEntry>();
        foreach (var pattern in current)
        {
            if (!CodingAgentScopedModelPatterns.TryResolve(pattern, availableModels, out var entry, out _))
            {
                continue;
            }

            if (!entries.Any(existing => SameModel(existing.Model, entry.Model)))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static IReadOnlyList<string>? ToSavedScopedModelPatterns(
        IReadOnlyList<CodingAgentScopedModelEntry> entries,
        IReadOnlyList<Model> availableModels)
    {
        var normalized = NormalizeScopedModelEntries(entries, availableModels);
        if (normalized.Count == 0)
        {
            return [];
        }

        if (normalized.Count == availableModels.Count &&
            normalized.All(static entry => entry.ThinkingLevel is null))
        {
            return null;
        }

        return normalized.Select(static entry => entry.Pattern).ToArray();
    }

    private string FormatScopedModels(IReadOnlyList<string>? enabledModels, IReadOnlyList<Model> availableModels)
    {
        var allIds = availableModels.Select(FormatScopedModelId).ToArray();
        var enabledEntries = enabledModels is null || enabledModels.Count == 0
            ? null
            : GetCurrentScopedModelEntries(enabledModels, availableModels).ToArray();
        var enabledSet = enabledEntries is null
            ? allIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : enabledEntries.Select(static entry => entry.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var thinkingByModelId = enabledEntries?
            .Where(static entry => entry.ThinkingLevel is not null)
            .ToDictionary(static entry => entry.ModelId, static entry => entry.ThinkingLevel!, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var enabledCount = enabledEntries?.Length ?? allIds.Length;
        var header = enabledEntries is null
            ? $"scoped models: all enabled ({allIds.Length}/{allIds.Length})"
            : $"scoped models: {enabledCount}/{allIds.Length} enabled";
        var lines = new List<string>
        {
            header,
            $"settings: {_settingsStore?.Path ?? "unavailable"}"
        };

        if (enabledEntries is not null)
        {
            lines.Add($"order: {string.Join(", ", enabledEntries.Select(static entry => entry.Pattern))}");
        }

        lines.Add("models:");
        foreach (var model in availableModels)
        {
            var id = FormatScopedModelId(model);
            var state = enabledSet.Contains(id) ? "enabled" : "disabled";
            if (state == "enabled" && thinkingByModelId.TryGetValue(id, out var thinkingLevel))
            {
                state += $", thinking {thinkingLevel}";
            }

            lines.Add($"  {id} ({state})");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatScopedModelId(Model model) => $"{model.Provider}/{model.Id}";

    private static bool SameModel(Model left, Model right) =>
        left.Provider.Equals(right.Provider, StringComparison.OrdinalIgnoreCase) &&
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase);

    private CodingAgentCommandResult HandlePromptsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/prompts"));
        }

        var templates = _promptTemplateStore?.Load() ?? [];
        if (templates.Count == 0)
        {
            return CodingAgentCommandResult.Status("prompts: none");
        }

        var promptList = templates.Select(template =>
        {
            var hint = string.IsNullOrWhiteSpace(template.ArgumentHint) ? string.Empty : $" {template.ArgumentHint}";
            var description = string.IsNullOrWhiteSpace(template.Description) ? string.Empty : $" - {template.Description}";
            return $"/{template.Name}{hint}{description}";
        });
        return CodingAgentCommandResult.Status($"prompts: {string.Join("; ", promptList)}");
    }

    private CodingAgentCommandResult HandleSkillsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/skills"));
        }

        var skills = _skillStore?.Load() ?? [];
        if (skills.Count == 0)
        {
            return CodingAgentCommandResult.Status("skills: none");
        }

        var skillList = skills.Select(skill =>
        {
            var description = string.IsNullOrWhiteSpace(skill.Description) ? string.Empty : $" - {skill.Description}";
            return $"/skill:{skill.Name}{description}";
        });
        return CodingAgentCommandResult.Status($"skills: {string.Join("; ", skillList)}");
    }

    private CodingAgentCommandResult HandleExtensionsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/extensions"));
        }

        var status = _extensionCommandStore?.LoadStatus();
        var commands = status?.Commands ?? [];
        var lines = new List<string>();
        if (commands.Count == 0)
        {
            lines.Add("extensions: none");
        }
        else
        {
            var commandList = commands.Select(command =>
            {
                var hint = string.IsNullOrWhiteSpace(command.ArgumentHint) ? string.Empty : $" {command.ArgumentHint}";
                var description = string.IsNullOrWhiteSpace(command.Description) ? string.Empty : $" - {command.Description}";
                var mode = command.SendToRunner ? ", runner" : string.Empty;
                return $"/{command.InvocationName}{hint}{description} ({command.Scope}{mode})";
            });
            lines.Add($"extensions: {string.Join("; ", commandList)}");
        }

        if (status is not null)
        {
            AppendExtensionStatusDetails(lines, status);
        }

        return CodingAgentCommandResult.Status(string.Join('\n', lines));
    }

    private static void AppendExtensionStatusDetails(ICollection<string> lines, CodingAgentExtensionStatus status)
    {
        if (status.Files.Count > 0)
        {
            var files = status.Files.Select(file =>
                $"{file.FilePath} ({file.Scope}, {file.CommandCount} commands, {file.PromptPaths.Count} prompts, {file.SkillPaths.Count} skills, {file.ThemePaths.Count} themes)");
            lines.Add($"extension files: {string.Join("; ", files)}");
        }

        var duplicateGroups = status.Commands
            .GroupBy(static command => command.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group =>
                $"{group.Key} -> {string.Join(", ", group.Select(static command => "/" + command.InvocationName))}")
            .ToArray();
        if (duplicateGroups.Length > 0)
        {
            lines.Add($"extension duplicates: {string.Join("; ", duplicateGroups)}");
        }

        var resources = new List<string>();
        if (status.Resources.PromptPaths.Count > 0)
        {
            resources.Add($"prompts {string.Join(", ", status.Resources.PromptPaths)}");
        }

        if (status.Resources.SkillPaths.Count > 0)
        {
            resources.Add($"skills {string.Join(", ", status.Resources.SkillPaths)}");
        }

        if (status.Resources.ThemePaths.Count > 0)
        {
            resources.Add($"themes {string.Join(", ", status.Resources.ThemePaths)}");
        }

        if (resources.Count > 0)
        {
            lines.Add($"extension resources: {string.Join("; ", resources)}");
        }

        if (status.Diagnostics.Count > 0)
        {
            var issues = status.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Severity} {diagnostic.Path} ({diagnostic.Scope}) - {diagnostic.Message}");
            lines.Add($"extension issues: {string.Join("; ", issues)}");
        }
    }

    private async Task<CodingAgentCommandResult> HandleAuthCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/auth"));
        }

        if (parts.Count == 2 && parts[1].Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            return await SelectAuthProviderAsync(cancellationToken).ConfigureAwait(false);
        }

        var status = parts.Count == 2 && !parts[1].Equals("current", StringComparison.OrdinalIgnoreCase)
            ? _runner.GetAuthStatus(parts[1])
            : _runner.GetAuthStatus();
        return CodingAgentCommandResult.Status(FormatAuthStatus(status));
    }

    private async Task<CodingAgentCommandResult> SelectAuthProviderAsync(CancellationToken cancellationToken)
    {
        if (_authSelector is null)
        {
            return CodingAgentCommandResult.Error("auth selector is not available in this session");
        }

        var providers = _runner.GetProviders()
            .Select(provider => _runner.GetAuthStatus(provider))
            .ToArray();
        if (providers.Length == 0)
        {
            return CodingAgentCommandResult.Error("auth selector has no providers");
        }

        var selected = await _authSelector(
                new CodingAgentAuthSelectorState(_runner.Model.Provider, providers),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return CodingAgentCommandResult.Status("auth selection cancelled");
        }

        return CodingAgentCommandResult.Status(FormatAuthStatus(_runner.GetAuthStatus(selected)));
    }

    private static string FormatAuthStatus(ProviderAuthStatus status)
    {
        var configured = status.IsConfigured ? "configured" : "missing";
        var oauth = status.UsesOAuth ? ", oauth" : string.Empty;
        return $"auth {status.Provider}: {configured} via {status.Source}{oauth}. {status.Message}";
    }

    private async Task<CodingAgentCommandResult> HandleLoginCommandAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/login"));
        }

        if (parts.Count == 2 && parts[1].Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            return await SelectAndLoginProviderAsync(cancellationToken).ConfigureAwait(false);
        }

        if (parts.Count == 1 && _authSelector is not null)
        {
            return await SelectAndLoginProviderAsync(cancellationToken).ConfigureAwait(false);
        }

        return await LoginProviderAsync(
                parts.Count == 2 ? parts[1] : null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CodingAgentCommandResult> SelectAndLoginProviderAsync(CancellationToken cancellationToken)
    {
        if (_authSelector is null)
        {
            return CodingAgentCommandResult.Error("login selector is not available in this session");
        }

        var providers = _runner.GetProviders()
            .Where(provider => _runner.GetOAuthProvider(provider) is not null)
            .Select(provider => _runner.GetAuthStatus(provider))
            .ToArray();
        if (providers.Length == 0)
        {
            return CodingAgentCommandResult.Error("login selector has no OAuth providers");
        }

        var selected = await _authSelector(
                new CodingAgentAuthSelectorState(_runner.Model.Provider, providers),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return CodingAgentCommandResult.Status("login selection cancelled");
        }

        return await LoginProviderAsync(selected, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingAgentCommandResult> LoginProviderAsync(
        string? providerId,
        CancellationToken cancellationToken)
    {
        var status = string.IsNullOrWhiteSpace(providerId)
            ? _runner.GetAuthStatus()
            : _runner.GetAuthStatus(providerId);
        if (status.IsConfigured)
        {
            return CodingAgentCommandResult.Status($"auth {status.Provider}: already configured via {status.Source}.");
        }

        if (!status.CanLogin)
        {
            return CodingAgentCommandResult.Error(
                $"login {status.Provider}: No OAuth login flow is registered for this provider; configure environment variables, auth.json, or models.json.");
        }

        var provider = _runner.GetOAuthProvider(status.Provider);
        if (provider is null)
        {
            return CodingAgentCommandResult.Error($"login {status.Provider}: OAuth provider not found.");
        }

        var callbacks = _oauthLoginCallbacksFactory();
        var credentials = await provider.LoginAsync(callbacks, cancellationToken).ConfigureAwait(false);
        _runner.SaveOAuthCredentials(status.Provider, credentials);
        return CodingAgentCommandResult.Status($"login {status.Provider}: authenticated successfully. Credentials saved to auth.json.");
    }

    private async Task<CodingAgentCommandResult> HandleLogoutCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/logout"));
        }

        if (parts.Count == 2 && parts[1].Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            return await SelectAndLogoutProviderAsync(cancellationToken).ConfigureAwait(false);
        }

        if (parts.Count == 1 && _authSelector is not null)
        {
            return await SelectAndLogoutProviderAsync(cancellationToken).ConfigureAwait(false);
        }

        return LogoutProvider(parts.Count == 2 ? parts[1] : null);
    }

    private async Task<CodingAgentCommandResult> SelectAndLogoutProviderAsync(CancellationToken cancellationToken)
    {
        if (_authSelector is null)
        {
            return CodingAgentCommandResult.Error("logout selector is not available in this session");
        }

        var providers = _runner.GetProviders()
            .Select(provider => _runner.GetAuthStatus(provider))
            .Where(status => status.UsesOAuth && _runner.GetOAuthProvider(status.Provider) is not null)
            .ToArray();
        if (providers.Length == 0)
        {
            return CodingAgentCommandResult.Status("No OAuth providers logged in. Use /login first.");
        }

        var selected = await _authSelector(
                new CodingAgentAuthSelectorState(_runner.Model.Provider, providers),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return CodingAgentCommandResult.Status("logout selection cancelled");
        }

        return LogoutProvider(selected);
    }

    private CodingAgentCommandResult LogoutProvider(string? providerId)
    {
        var status = string.IsNullOrWhiteSpace(providerId)
            ? _runner.GetAuthStatus()
            : _runner.GetAuthStatus(providerId);
        var removed = _runner.Logout(status.Provider);
        var unchanged = "Environment variables and models.json credentials are unchanged.";
        return removed
            ? CodingAgentCommandResult.Status($"logout {status.Provider}: auth.json credentials removed. {unchanged}")
            : CodingAgentCommandResult.Status($"logout {status.Provider}: no auth.json credentials found. {unchanged}");
    }

    private CodingAgentCommandResult HandleChangelogCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/changelog"));
        }

        var limit = 20;
        if (parts.Count == 2)
        {
            if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                limit = int.MaxValue;
            }
            else if (!int.TryParse(
                         parts[1],
                         System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture,
                         out limit)
                     || limit <= 0)
            {
                return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/changelog"));
            }
        }

        var snapshot = _changelogStore.Load();
        return CodingAgentCommandResult.Status(CodingAgentChangelogStore.Format(snapshot, limit));
    }

    private CodingAgentCommandResult HandleFindCommand(string input, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/find"));
        }

        var firstSpace = input.IndexOf(' ');
        var pattern = firstSpace > 0 ? input[(firstSpace + 1)..].Trim() : string.Empty;
        if (string.IsNullOrEmpty(pattern))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/find"));
        }

        var messages = _runner.Messages;
        if (messages.Count == 0)
        {
            return CodingAgentCommandResult.Status("History is empty.");
        }

        var matches = new List<string>();
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var role = message.Role;
            switch (message)
            {
                case UserMessage user:
                    AppendContentMatches(matches, role, i, user.Content, pattern);
                    break;
                case AssistantMessage assistant:
                    AppendContentMatches(matches, role, i, assistant.Content, pattern);
                    break;
                case ToolResultMessage tool:
                    AppendContentMatches(matches, role, i, tool.Content, pattern);
                    break;
            }
        }

        if (matches.Count == 0)
        {
            return CodingAgentCommandResult.Status($"No matches for \"{pattern}\".");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("Matches for \"").Append(pattern).Append("\" (").Append(matches.Count).AppendLine("):");
        foreach (var line in matches)
        {
            sb.Append("  ").AppendLine(line);
        }

        return CodingAgentCommandResult.Status(sb.ToString().TrimEnd());
    }

    private static void AppendContentMatches(
        List<string> matches,
        string role,
        int messageIndex,
        IReadOnlyList<ContentBlock> content,
        string pattern)
    {
        for (var blockIndex = 0; blockIndex < content.Count; blockIndex++)
        {
            var block = content[blockIndex];
            string? text = block switch
            {
                TextContent t => t.Text,
                ToolCallContent c => $"[{c.Name}] {c.Arguments}",
                _ => null
            };

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var matchIndex = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                continue;
            }

            const int contextRadius = 40;
            var start = Math.Max(0, matchIndex - contextRadius);
            var end = Math.Min(text.Length, matchIndex + pattern.Length + contextRadius);
            var preview = text[start..end].Replace('\n', '⏎').Replace('\r', '⏎');
            var prefix = start > 0 ? "…" : string.Empty;
            var suffix = end < text.Length ? "…" : string.Empty;
            matches.Add($"[{messageIndex + 1,2}] {role}: {prefix}{preview}{suffix}");
        }
    }

    private CodingAgentCommandResult HandleClearCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/clear"));
        }

        if (_clearScreenAction is null)
        {
            return CodingAgentCommandResult.Error("clear-screen is not supported in this session");
        }

        _clearScreenAction();
        return CodingAgentCommandResult.Status("");
    }

    private CodingAgentCommandResult HandleHistoryCommand(IReadOnlyList<string> parts)
    {
        if (_historySnapshotProvider is null)
        {
            return CodingAgentCommandResult.Error("Input history is not available in this session.");
        }

        var limit = 20;
        if (parts.Count >= 2)
        {
            if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                limit = int.MaxValue;
            }
            else if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out limit) || limit <= 0)
            {
                return CodingAgentCommandResult.Error("Usage: /history [count|all]");
            }
        }

        IReadOnlyList<string> entries;
        try
        {
            entries = _historySnapshotProvider(limit);
        }
        catch (Exception ex)
        {
            return CodingAgentCommandResult.Error($"Failed to load history: {ex.Message}");
        }

        if (entries.Count == 0)
        {
            return CodingAgentCommandResult.Status("History is empty.");
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Recent inputs ({entries.Count}):");
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            // Truncate long entries to keep the listing readable.
            var preview = entry.Length > 200 ? entry[..200] + "..." : entry;
            // Replace newlines with markers so each entry stays on one line.
            preview = preview.Replace("\r\n", " ⏎ ", StringComparison.Ordinal)
                .Replace('\n', '⏎')
                .Replace('\r', '⏎');
            builder.AppendLine($"  [{i + 1,2}] {preview}");
        }

        return CodingAgentCommandResult.Status(builder.ToString().TrimEnd());
    }

    private CodingAgentCommandResult HandleRetryCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count == 1 || (parts.Count == 2 && IsCurrentKeyword(parts[1])))
        {
            return CodingAgentCommandResult.Status($"retry: {FormatRetryPolicy(_retryOptions)}");
        }

        if (parts.Count == 2 && parts[1].Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return SaveRetryOptions(
                CodingAgentRetryOptions.FromEnvironment(),
                configuredMaxAttempts: null,
                configuredBaseDelayMilliseconds: null);
        }

        if (parts.Count == 2 && IsRetryOffKeyword(parts[1]))
        {
            return SaveRetryOptions(
                CodingAgentRetryOptions.Disabled,
                configuredMaxAttempts: 0,
                configuredBaseDelayMilliseconds: 0);
        }

        if (parts.Count is < 2 or > 3 ||
            !int.TryParse(parts[1], out var maxAttempts) ||
            maxAttempts < 0)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/retry"));
        }

        var baseDelayMilliseconds = CodingAgentRetryOptions.Default.BaseDelayMilliseconds;
        if (parts.Count == 3 &&
            (!int.TryParse(parts[2], out baseDelayMilliseconds) || baseDelayMilliseconds < 0))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/retry"));
        }

        if (maxAttempts == 0)
        {
            baseDelayMilliseconds = 0;
        }

        var options = maxAttempts == 0
            ? CodingAgentRetryOptions.Disabled
            : new CodingAgentRetryOptions(maxAttempts, baseDelayMilliseconds);
        return SaveRetryOptions(
            options,
            configuredMaxAttempts: maxAttempts,
            configuredBaseDelayMilliseconds: baseDelayMilliseconds);
    }

    private async Task<CodingAgentCommandResult> HandleThinkingCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count == 1 || (parts.Count == 2 && IsCurrentKeyword(parts[1])))
        {
            return CodingAgentCommandResult.Status($"thinking: {FormatThinkingLevel(_runner.ThinkingLevel)}");
        }

        if (parts.Count != 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/thinking"));
        }

        var arg = parts[1].ToLowerInvariant();

        if (arg == "select")
        {
            return await SelectThinkingLevelAsync(cancellationToken).ConfigureAwait(false);
        }

        if (arg is "off" or "none")
        {
            SetAndSaveThinkingLevel(null);
            return CodingAgentCommandResult.Status("thinking: off");
        }

        if (arg == "cycle")
        {
            _runner.ThinkingLevel = CodingAgentThinkingLevels.CycleForModel(_runner.Model, _runner.ThinkingLevel);
            SaveThinkingLevel(_runner.ThinkingLevel);
            return CodingAgentCommandResult.Status($"thinking: {FormatThinkingLevel(_runner.ThinkingLevel)}");
        }

        if (!TryParseThinkingLevel(arg, out var level))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/thinking"));
        }

        var effective = SetAndSaveThinkingLevel(level);
        return CodingAgentCommandResult.Status($"thinking: {FormatThinkingLevel(effective)}");
    }

    private async Task<CodingAgentCommandResult> SelectThinkingLevelAsync(CancellationToken cancellationToken)
    {
        if (_thinkingSelector is null)
        {
            return CodingAgentCommandResult.Error("thinking selector is not available in this session");
        }

        var selected = await _thinkingSelector(
                new CodingAgentThinkingSelectorState(
                    _runner.ThinkingLevel,
                    CodingAgentThinkingLevels.AvailableForModel(_runner.Model)),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return CodingAgentCommandResult.Status("thinking selection cancelled");
        }

        var normalized = selected.Trim().ToLowerInvariant();
        if (normalized is "off" or "none")
        {
            SetAndSaveThinkingLevel(null);
            return CodingAgentCommandResult.Status("thinking: off");
        }

        if (!TryParseThinkingLevel(normalized, out var level))
        {
            return CodingAgentCommandResult.Error($"thinking selector returned unsupported level '{selected.Trim()}'");
        }

        var effective = SetAndSaveThinkingLevel(level);
        return CodingAgentCommandResult.Status($"thinking: {FormatThinkingLevel(effective)}");
    }

    private ThinkingLevel? SetAndSaveThinkingLevel(ThinkingLevel? requested)
    {
        var effective = CodingAgentThinkingLevels.ClampForModel(_runner.Model, requested);
        _runner.ThinkingLevel = effective;
        SaveThinkingLevel(effective);
        return effective;
    }

    private void ClampCurrentThinkingLevel()
    {
        SetAndSaveThinkingLevel(_runner.ThinkingLevel);
    }

    private void SaveThinkingLevel(ThinkingLevel? level)
    {
        if (_settingsStore is null) return;
        var settings = _settingsStore.Load();
        _settingsStore.Save(settings with { DefaultThinkingLevel = FormatThinkingLevelRaw(level) });
    }

    private void SaveScopedModels(CodingAgentSettingsSnapshot current, IReadOnlyList<string>? enabledModels)
    {
        _settingsStore?.Save(current with { EnabledModels = enabledModels });
    }

    private void ApplyScopedThinkingOverride(string? thinkingLevel)
    {
        if (thinkingLevel is null)
        {
            ClampCurrentThinkingLevel();
            return;
        }

        var level = CodingAgentScopedModelPatterns.ParseThinkingLevelOrNull(thinkingLevel);
        SetAndSaveThinkingLevel(level);
    }

    private static ThinkingLevel? ParseThinkingLevelOrNull(string? value) =>
        CodingAgentThinkingLevels.ParseOrNull(value);

    private static bool TryParseThinkingLevel(string value, out ThinkingLevel level)
    {
        if (CodingAgentThinkingLevels.TryParse(value, out var parsed) && parsed is not null)
        {
            level = parsed.Value;
            return true;
        }

        level = default;
        return false;
    }

    private static string FormatThinkingLevel(ThinkingLevel? level) =>
        CodingAgentThinkingLevels.Format(level);

    private static string? FormatThinkingLevelRaw(ThinkingLevel? level) =>
        CodingAgentThinkingLevels.FormatRaw(level);

    private static string FormatModelCycleScopeSuffix(
        bool isScoped,
        string? requestedThinkingLevel,
        ThinkingLevel? effectiveThinkingLevel)
    {
        if (!isScoped)
        {
            return string.Empty;
        }

        return requestedThinkingLevel is null
            ? " (scoped)"
            : $" (scoped, thinking: {FormatThinkingLevel(effectiveThinkingLevel)})";
    }

    private async Task<CodingAgentCommandResult> HandleCompactCommandAsync(
        string input,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        var instructions = parts.Count == 1
            ? null
            : input[(input.IndexOf(' ') + 1)..].Trim();
        _treeSessionController?.SyncFromRunner(_runner);
        var result = await _runner
            .CompactAsync(string.IsNullOrWhiteSpace(instructions) ? null : instructions, cancellationToken)
            .ConfigureAwait(false);
        _treeSessionController?.RecordCompaction(_runner, result);
        var messagesAfter = _treeSessionController is null ? result.MessagesAfter : _runner.Messages.Count;
        return CodingAgentCommandResult.Status(
            $"compacted session: {result.MessagesBefore} -> {messagesAfter} messages");
    }

    private void SaveDefaultModel(Model model)
    {
        _settingsStore?.SaveDefaultModel(model);
    }

    private CodingAgentCommandResult SaveRetryOptions(
        CodingAgentRetryOptions options,
        int? configuredMaxAttempts,
        int? configuredBaseDelayMilliseconds)
    {
        _retryOptions = options;
        _retryOptionsChanged?.Invoke(options);
        if (_settingsStore is not null)
        {
            var settings = _settingsStore.Load();
            _settingsStore.Save(settings with
            {
                RetryMaxAttempts = configuredMaxAttempts,
                RetryBaseDelayMilliseconds = configuredBaseDelayMilliseconds
            });
        }

        return CodingAgentCommandResult.Status($"retry: {FormatRetryPolicy(_retryOptions)}");
    }

    private string GetDefaultHtmlExportPath()
    {
        var sourcePath = _treeSessionController?.Path ?? _sessionFile;
        var directory = string.IsNullOrWhiteSpace(sourcePath)
            ? Environment.CurrentDirectory
            : (Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory);
        var basename = string.IsNullOrWhiteSpace(sourcePath)
            ? "session"
            : Path.GetFileNameWithoutExtension(sourcePath);
        basename = SanitizeFileName(string.IsNullOrWhiteSpace(basename) ? "session" : basename);
        return Path.Combine(directory, $"tau-session-{basename}.html");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        return new string(chars);
    }

    private static bool IsCurrentKeyword(string value) =>
        value.Equals("current", StringComparison.OrdinalIgnoreCase);

    private static bool IsRetryOffKeyword(string value) =>
        value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("disable", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("disabled", StringComparison.OrdinalIgnoreCase);

    private static bool IsBranchSummaryOption(string value) =>
        value.Equals("--summarize", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--summary", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-s", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractBranchSummaryInstructions(IReadOnlyList<string> parts)
    {
        if (parts.Count <= 3)
        {
            return null;
        }

        var instructions = string.Join(' ', parts.Skip(3)).Trim();
        return string.IsNullOrWhiteSpace(instructions) ? null : instructions;
    }

    private static string FormatSessionName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "none" : name;

    private static string FormatSettingsDefaultModel(CodingAgentSettingsSnapshot settings) =>
        string.IsNullOrWhiteSpace(settings.DefaultProvider) || string.IsNullOrWhiteSpace(settings.DefaultModel)
            ? "unset"
            : $"{settings.DefaultProvider}/{settings.DefaultModel}";

    private static string FormatTreeFilterSetting(string? treeFilterMode) =>
        string.IsNullOrWhiteSpace(treeFilterMode) ? "default" : treeFilterMode;

    private static string FormatSettingsThinkingLevel(string? defaultThinkingLevel) =>
        string.IsNullOrWhiteSpace(defaultThinkingLevel) ? "off" : defaultThinkingLevel;

    private static string FormatSettingsScopedModels(IReadOnlyList<string>? enabledModels) =>
        enabledModels is null || enabledModels.Count == 0
            ? "all enabled"
            : $"{enabledModels.Count} enabled ({string.Join(", ", enabledModels)})";

    private static string FormatSettingsAutoCompaction(bool? enabled) => enabled switch
    {
        true => "enabled",
        false => "disabled",
        null => "default"
    };

    private static bool TryParseSettingsBoolean(string value, out bool parsed)
    {
        parsed = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "true":
            case "enabled":
            case "enable":
            case "yes":
            case "on":
                parsed = true;
                return true;
            case "false":
            case "disabled":
            case "disable":
            case "no":
            case "off":
                parsed = false;
                return true;
            default:
                return false;
        }
    }

    private static string FormatSettingsBoolean(bool enabled) =>
        enabled ? "enabled" : "disabled";

    private bool ResolveAutoCompactionEnabled(bool? setting) =>
        setting ?? _autoCompaction.IsEnabled;

    private static string FormatThemeSetting(string? theme) => FormatCurrentTheme(theme);

    private static string ToggleQueueMode(string? mode) =>
        CodingAgentQueueModes.NormalizeOrDefault(mode) == CodingAgentQueueModes.All
            ? CodingAgentQueueModes.OneAtATime
            : CodingAgentQueueModes.All;

    private static string CycleTreeFilterMode(string? treeFilterMode) =>
        NormalizeTreeFilterMode(treeFilterMode) switch
        {
            CodingAgentTreeFilterMode.Default => "no-tools",
            CodingAgentTreeFilterMode.NoTools => "user-only",
            CodingAgentTreeFilterMode.UserOnly => "labeled-only",
            CodingAgentTreeFilterMode.LabeledOnly => "all",
            CodingAgentTreeFilterMode.All => "default",
            _ => "default"
        };

    private static bool IsDefaultTreeFilterMode(string value) =>
        value.Equals("default", StringComparison.OrdinalIgnoreCase);

    private static string FormatTreeFilterModeRaw(CodingAgentTreeFilterMode mode) => mode switch
    {
        CodingAgentTreeFilterMode.NoTools => "no-tools",
        CodingAgentTreeFilterMode.UserOnly => "user-only",
        CodingAgentTreeFilterMode.LabeledOnly => "labeled-only",
        CodingAgentTreeFilterMode.All => "all",
        _ => "default"
    };

    private static CodingAgentTreeFilterMode NormalizeTreeFilterMode(string? treeFilterMode)
    {
        if (string.IsNullOrWhiteSpace(treeFilterMode))
        {
            return CodingAgentTreeFilterMode.Default;
        }

        return TryParseTreeFilterMode(treeFilterMode, out var filterMode)
            ? filterMode
            : CodingAgentTreeFilterMode.Default;
    }

    private static string FormatCurrentTheme(string? theme) =>
        string.IsNullOrWhiteSpace(theme) ? CodingAgentThemeStore.DefaultThemeName : theme;

    private static string FormatRetryPolicy(CodingAgentRetryOptions options) =>
        options.IsEnabled
            ? $"enabled {options.MaxAttempts} attempts, base {options.BaseDelayMilliseconds}ms"
            : "off";

    private static string FormatTreeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "root" : id.Length <= 8 ? id : id[..8];

    private static bool ShouldUseUserEntryNavigation(CodingAgentTreeViewItem item) =>
        string.Equals(item.EntryType, "message", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(item.MessageRole, "user", StringComparison.OrdinalIgnoreCase);

    private async Task<CodingAgentTreeNavigationDecision> ResolveTreeNavigationDecisionAsync(
        string? navigationTargetId,
        CancellationToken cancellationToken,
        CodingAgentTreeNavigationReason reason = CodingAgentTreeNavigationReason.TreeNavigation,
        string? targetSessionPath = null)
    {
        var summaryMessages = _treeSessionController?.CollectBranchSummaryMessages(navigationTargetId);
        if (summaryMessages is null || summaryMessages.Count == 0)
        {
            return CodingAgentTreeNavigationDecision.NoSummary;
        }

        if (_treeNavigationPrompt is null)
        {
            return CodingAgentTreeNavigationDecision.NoSummary;
        }

        var estimate = CodingAgentTokenEstimator.Estimate(summaryMessages);
        return await _treeNavigationPrompt(
                new CodingAgentTreeNavigationPromptState(
                    navigationTargetId,
                    summaryMessages.Count,
                    estimate,
                    reason,
                    targetSessionPath),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string FormatTreeSummarySuffix(CodingAgentBranchSummaryResult? summary) =>
        summary is null ? string.Empty : $", branch summary {summary.EntryCount} entries, tokens ~{summary.TokensBefore}";

    private static string FormatSessionSwitchSummarySuffix(CodingAgentSessionSwitchSummaryResult result) =>
        result.SummarizedCurrentBranch
            ? $", previous branch summary {result.SummaryEntryCount} entries, tokens ~{result.TokensBeforeSummary}"
            : string.Empty;

    private static string FormatParentSession(string? parentSession) =>
        string.IsNullOrWhiteSpace(parentSession) ? string.Empty : $", parent {parentSession}";

    private static string? NormalizeTreeLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private static bool TryParseTreeOptions(
        IReadOnlyList<string> parts,
        CodingAgentTreeFilterMode defaultFilterMode,
        out CodingAgentTreeFormatOptions options,
        out bool interactive)
    {
        interactive = false;
        var maxEntries = 24;
        var filterMode = defaultFilterMode;
        var showLabelTimestamps = false;
        string? searchQuery = null;
        var hasMaxEntries = false;
        var hasFilter = false;

        for (var i = 1; i < parts.Count; i++)
        {
            var part = parts[i];
            if (IsInteractiveOption(part))
            {
                interactive = true;
                continue;
            }

            if (IsSearchOption(part))
            {
                if (!string.IsNullOrWhiteSpace(searchQuery) || i == parts.Count - 1)
                {
                    options = new CodingAgentTreeFormatOptions();
                    return false;
                }

                searchQuery = string.Join(' ', parts.Skip(i + 1));
                break;
            }

            if (int.TryParse(part, out var parsedMax))
            {
                if (hasMaxEntries || parsedMax <= 0)
                {
                    options = new CodingAgentTreeFormatOptions();
                    return false;
                }

                maxEntries = parsedMax;
                hasMaxEntries = true;
                continue;
            }

            if (IsLabelTimestampOption(part))
            {
                showLabelTimestamps = true;
                continue;
            }

            if (TryParseTreeFilterMode(part, out var parsedFilter))
            {
                if (hasFilter)
                {
                    options = new CodingAgentTreeFormatOptions();
                    return false;
                }

                filterMode = parsedFilter;
                hasFilter = true;
                continue;
            }

            options = new CodingAgentTreeFormatOptions();
            return false;
        }

        options = new CodingAgentTreeFormatOptions(maxEntries, filterMode, showLabelTimestamps, searchQuery);
        return true;
    }

    private CodingAgentTreeFilterMode GetDefaultTreeFilterMode()
    {
        var configured = _settingsStore?.Load().TreeFilterMode;
        return !string.IsNullOrWhiteSpace(configured) &&
               TryParseTreeFilterMode(configured, out var filterMode)
            ? filterMode
            : CodingAgentTreeFilterMode.Default;
    }

    private static bool IsSearchOption(string value) =>
        value.Equals("--search", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-s", StringComparison.OrdinalIgnoreCase);

    private static bool IsInteractiveOption(string value) =>
        value.Equals("--interactive", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-i", StringComparison.OrdinalIgnoreCase);

    private static bool IsLabelTimestampOption(string value) =>
        value.Equals("--label-time", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--label-timestamps", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("+label-time", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseTreeFilterMode(string value, out CodingAgentTreeFilterMode filterMode)
    {
        filterMode = value.ToLowerInvariant() switch
        {
            "default" => CodingAgentTreeFilterMode.Default,
            "no-tools" or "notools" => CodingAgentTreeFilterMode.NoTools,
            "user" or "user-only" => CodingAgentTreeFilterMode.UserOnly,
            "labeled" or "labels" or "labeled-only" => CodingAgentTreeFilterMode.LabeledOnly,
            "all" => CodingAgentTreeFilterMode.All,
            _ => (CodingAgentTreeFilterMode)(-1)
        };
        return filterMode != (CodingAgentTreeFilterMode)(-1);
    }

    private string FormatTokenBudget(CodingAgentSessionStats stats)
    {
        var estimate = Math.Max(0, stats.EstimatedTokens);
        var context = stats.ContextWindowTokens.GetValueOrDefault();
        var contextBudget = context > 0
            ? $"~{estimate}/{context} context ({Math.Max(0, context - estimate)} remaining)"
            : $"~{estimate}";

        if (!_autoCompaction.IsEnabled)
        {
            return contextBudget;
        }

        var threshold = _autoCompaction.ThresholdTokens;
        return $"{contextBudget}, auto-compact {threshold} ({Math.Max(0, threshold - estimate)} remaining)";
    }

    private string? GetLastAssistantText()
    {
        var assistant = _runner.Messages
            .OfType<AssistantMessage>()
            .LastOrDefault();
        if (assistant is null)
        {
            return null;
        }

        var text = string.Join(
            "\n\n",
            assistant.Content
                .OfType<TextContent>()
                .Select(content => content.Text.Trim())
                .Where(content => !string.IsNullOrWhiteSpace(content)));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

}

public sealed record CodingAgentTreeNavigationPromptState(
    string? TargetEntryId,
    int EntryCount,
    int TokensBefore,
    CodingAgentTreeNavigationReason Reason,
    string? TargetSessionPath);

public sealed record CodingAgentTreeLabelPromptState(
    string EntryId,
    string? CurrentLabel);

public sealed record CodingAgentTreeNavigationDecision(
    bool Cancelled,
    bool Summarize,
    string? CustomInstructions = null,
    bool ReplaceInstructions = false,
    string? Label = null)
{
    public static CodingAgentTreeNavigationDecision NoSummary { get; } = new(false, false);
    public static CodingAgentTreeNavigationDecision CancelledDecision { get; } = new(true, false);

    public static CodingAgentTreeNavigationDecision SummarizeWith(
        string? customInstructions = null,
        bool replaceInstructions = false,
        string? label = null) =>
        new(
            false,
            true,
            string.IsNullOrWhiteSpace(customInstructions) ? null : customInstructions.Trim(),
            replaceInstructions,
            string.IsNullOrWhiteSpace(label) ? null : label.Trim());
}

public sealed record CodingAgentTreeLabelPromptResult(
    bool Cancelled,
    string? Label)
{
    public static CodingAgentTreeLabelPromptResult CancelledResult { get; } = new(true, null);

    public static CodingAgentTreeLabelPromptResult Saved(string? label) =>
        new(false, string.IsNullOrWhiteSpace(label) ? null : label.Trim());
}
