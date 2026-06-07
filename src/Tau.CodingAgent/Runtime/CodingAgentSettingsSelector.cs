using Tau.Ai;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSettingsSelectorState(
    string SettingsPath,
    CodingAgentSettingsSnapshot Settings,
    Model CurrentModel,
    ThinkingLevel? CurrentThinkingLevel,
    bool AutoCompactionEnabled,
    string CurrentTheme);

public static class CodingAgentSettingsSelector
{
    public const string AutoCompactionAction = "auto-compaction";
    public const string SteeringModeAction = "steering-mode";
    public const string FollowUpModeAction = "follow-up-mode";
    public const string TerminalShowImagesAction = "show-images";
    public const string ImagesAutoResizeAction = "auto-resize-images";
    public const string ImagesBlockImagesAction = "block-images";
    public const string ShowHardwareCursorAction = "show-hardware-cursor";
    public const string EditorPaddingAction = "editor-padding";
    public const string AutocompleteMaxVisibleAction = "autocomplete-max-visible";
    public const string TerminalClearOnShrinkAction = "clear-on-shrink";
    public const string TreeFilterModeAction = "tree-filter-mode";
    public const string ThinkingLevelAction = "thinking-level";
    public const string ScopedModelsAction = "scoped-models";
    public const string ThemeAction = "theme";
    public const string QuietStartupAction = "quiet-startup";
    public const string CollapseChangelogAction = "collapse-changelog";
    public const string InstallTelemetryAction = "install-telemetry";
    public const char SelectionSeparator = '=';

    public static Func<CodingAgentSettingsSelectorState, CancellationToken, Task<string?>> CreateConsoleSelector(
        IConsoleKeyReader keyReader,
        bool synchronizedOutput = true)
    {
        ArgumentNullException.ThrowIfNull(keyReader);

        return (state, cancellationToken) => SelectSettingsListAsync(
            state,
            keyReader,
            TuiAnsiRenderSurface.ForConsole(synchronizedOutput),
            cancellationToken);
    }

    public static Func<CodingAgentSettingsSelectorState, CancellationToken, Task<string?>> CreateCompositionSelector(
        TuiCompositionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return async (state, cancellationToken) =>
        {
            var selector = CreateSettingsList(state);
            var result = await TuiCompositionOverlaySessions.RunSettingsListAsync(selector, session, cancellationToken)
                .ConfigureAwait(false);
            return result.HasChange ? FormatSelection(result.Id!, result.Value!) : null;
        };
    }

    public static string FormatSelection(string id, string value) =>
        $"{id}{SelectionSeparator}{value}";

    public static bool TryParseSelection(string selection, out string id, out string value)
    {
        id = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(selection))
        {
            return false;
        }

        var separatorIndex = selection.IndexOf(SelectionSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        id = selection[..separatorIndex].Trim();
        value = selection[(separatorIndex + 1)..].Trim();
        return id.Length > 0;
    }

    public static TuiSettingsList CreateSettingsList(CodingAgentSettingsSelectorState state, int maxVisible = 10)
    {
        ArgumentNullException.ThrowIfNull(state);

        var settings = state.Settings;
        var items = new[]
        {
            new TuiSettingItem(
                AutoCompactionAction,
                "Auto compaction",
                FormatBooleanValue(state.AutoCompactionEnabled),
                "Automatically compact context when it gets too large.",
                ["true", "false"]),
            new TuiSettingItem(
                TerminalShowImagesAction,
                "Show images",
                FormatBooleanValue(settings.TerminalShowImages ?? true),
                "Render images inline in terminal when the terminal supports images.",
                ["true", "false"]),
            new TuiSettingItem(
                ImagesAutoResizeAction,
                "Auto-resize images",
                FormatBooleanValue(settings.ImagesAutoResize ?? true),
                "Resize large images to improve model compatibility.",
                ["true", "false"]),
            new TuiSettingItem(
                ImagesBlockImagesAction,
                "Block images",
                FormatBooleanValue(settings.ImagesBlockImages ?? false),
                "Prevent images from being sent to LLM providers.",
                ["true", "false"]),
            new TuiSettingItem(
                ShowHardwareCursorAction,
                "Show hardware cursor",
                FormatBooleanValue(settings.ShowHardwareCursor ?? false),
                "Show the terminal cursor while still positioning it for IME support.",
                ["true", "false"]),
            new TuiSettingItem(
                EditorPaddingAction,
                "Editor padding",
                FormatBoundedNumber(settings.EditorPaddingX, defaultValue: 0, min: 0, max: 3),
                "Horizontal padding for input editor.",
                ["0", "1", "2", "3"]),
            new TuiSettingItem(
                AutocompleteMaxVisibleAction,
                "Autocomplete max items",
                FormatBoundedNumber(settings.AutocompleteMaxVisible, defaultValue: 5, min: 3, max: 20),
                "Max visible items in autocomplete dropdown.",
                ["3", "5", "7", "10", "15", "20"]),
            new TuiSettingItem(
                TerminalClearOnShrinkAction,
                "Clear on shrink",
                FormatBooleanValue(settings.TerminalClearOnShrink ?? false),
                "Clear empty rows when content shrinks.",
                ["true", "false"]),
            new TuiSettingItem(
                SteeringModeAction,
                "Steering mode",
                CodingAgentQueueModes.NormalizeOrDefault(settings.SteeringMode),
                "Enter while streaming queues steering messages.",
                [CodingAgentQueueModes.OneAtATime, CodingAgentQueueModes.All]),
            new TuiSettingItem(
                FollowUpModeAction,
                "Follow-up mode",
                CodingAgentQueueModes.NormalizeOrDefault(settings.FollowUpMode),
                "Alt+Enter queues follow-up messages until agent stops.",
                [CodingAgentQueueModes.OneAtATime, CodingAgentQueueModes.All]),
            new TuiSettingItem(
                TreeFilterModeAction,
                "Tree filter mode",
                FormatTreeFilter(settings.TreeFilterMode),
                "Default filter when opening /tree.",
                ["default", "no-tools", "user-only", "labeled-only", "all"]),
            new TuiSettingItem(
                ThinkingLevelAction,
                "Thinking level",
                FormatThinkingLevel(state.CurrentThinkingLevel),
                "Reasoning depth for thinking-capable models.",
                CodingAgentThinkingLevels.AvailableForModel(state.CurrentModel)),
            new TuiSettingItem(
                QuietStartupAction,
                "Quiet startup",
                FormatBooleanValue(settings.QuietStartup ?? false),
                "Disable verbose printing at startup.",
                ["true", "false"]),
            new TuiSettingItem(
                CollapseChangelogAction,
                "Collapse changelog",
                FormatBooleanValue(settings.CollapseChangelog ?? false),
                "Show condensed changelog after updates.",
                ["true", "false"]),
            new TuiSettingItem(
                InstallTelemetryAction,
                "Install telemetry",
                FormatBooleanValue(settings.EnableInstallTelemetry ?? true),
                "Send an anonymous version/update ping after changelog-detected updates.",
                ["true", "false"]),
            new TuiSettingItem(
                ScopedModelsAction,
                "Scoped models",
                FormatScopedModels(settings.EnabledModels),
                "Open the scoped model selector.",
                [FormatScopedModels(settings.EnabledModels)]),
            new TuiSettingItem(
                ThemeAction,
                "Theme",
                state.CurrentTheme,
                "Open the theme selector.",
                [state.CurrentTheme])
        };

        return new TuiSettingsList(
            items,
            maxVisible: maxVisible,
            options: new TuiSettingsListOptions(EnableSearch: true));
    }

    public static TuiSelectList CreateSelectList(CodingAgentSettingsSelectorState state, int maxVisible = 8)
    {
        ArgumentNullException.ThrowIfNull(state);

        var settings = state.Settings;
        var items = new[]
        {
            new TuiSelectItem(
                AutoCompactionAction,
                "Auto compaction",
                $"effective {FormatBoolean(state.AutoCompactionEnabled)}, setting {FormatAutoCompactionSetting(settings.AutoCompactionEnabled)}"),
            new TuiSelectItem(
                SteeringModeAction,
                "Steering mode",
                CodingAgentQueueModes.NormalizeOrDefault(settings.SteeringMode)),
            new TuiSelectItem(
                FollowUpModeAction,
                "Follow-up mode",
                CodingAgentQueueModes.NormalizeOrDefault(settings.FollowUpMode)),
            new TuiSelectItem(
                TreeFilterModeAction,
                "Tree filter",
                FormatTreeFilter(settings.TreeFilterMode)),
            new TuiSelectItem(
                ThinkingLevelAction,
                "Thinking level",
                $"current {FormatThinkingLevel(state.CurrentThinkingLevel)}, default {FormatThinkingSetting(settings.DefaultThinkingLevel)}"),
            new TuiSelectItem(
                ScopedModelsAction,
                "Scoped models",
                FormatScopedModels(settings.EnabledModels)),
            new TuiSelectItem(
                ThemeAction,
                "Theme",
                state.CurrentTheme)
        };

        return new TuiSelectList(
            items,
            maxVisible: maxVisible,
            layout: new TuiSelectListLayout(MinPrimaryColumnWidth: 18, MaxPrimaryColumnWidth: 28));
    }

    public static async Task<string?> SelectAsync(
        CodingAgentSettingsSelectorState state,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        var selector = CreateSelectList(state);
        var result = await new TuiSelectorSession(selector, keyReader, surface)
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.HasSelection ? result.SelectedItem?.Value : null;
    }

    public static async Task<string?> SelectSettingsListAsync(
        CodingAgentSettingsSelectorState state,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        var selector = CreateSettingsList(state);
        var result = await new TuiSettingsListSession(selector, keyReader, surface)
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.HasChange ? FormatSelection(result.Id!, result.Value!) : null;
    }

    private static string FormatBoolean(bool value) => value ? "enabled" : "disabled";

    private static string FormatBooleanValue(bool value) => value ? "true" : "false";

    private static string FormatBoundedNumber(int? value, int defaultValue, int min, int max) =>
        Math.Clamp(value ?? defaultValue, min, max).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatAutoCompactionSetting(bool? value) => value switch
    {
        true => "enabled",
        false => "disabled",
        null => "default"
    };

    private static string FormatTreeFilter(string? treeFilterMode) =>
        string.IsNullOrWhiteSpace(treeFilterMode) ? "default" : treeFilterMode;

    private static string FormatThinkingSetting(string? defaultThinkingLevel) =>
        string.IsNullOrWhiteSpace(defaultThinkingLevel) ? "off" : defaultThinkingLevel;

    private static string FormatThinkingLevel(ThinkingLevel? level) => level switch
    {
        null => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        _ => "off"
    };

    private static string FormatScopedModels(IReadOnlyList<string>? enabledModels) =>
        enabledModels is null || enabledModels.Count == 0
            ? "all enabled"
            : $"{enabledModels.Count} enabled";
}
