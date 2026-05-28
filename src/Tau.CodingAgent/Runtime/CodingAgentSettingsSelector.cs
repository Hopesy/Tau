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
    public const string TreeFilterModeAction = "tree-filter-mode";
    public const string ThinkingLevelAction = "thinking-level";
    public const string ScopedModelsAction = "scoped-models";
    public const string ThemeAction = "theme";

    public static Func<CodingAgentSettingsSelectorState, CancellationToken, Task<string?>> CreateConsoleSelector(
        IConsoleKeyReader keyReader,
        bool synchronizedOutput = true)
    {
        ArgumentNullException.ThrowIfNull(keyReader);

        return (state, cancellationToken) => SelectAsync(
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
            var selector = CreateSelectList(state);
            var result = await TuiCompositionOverlaySessions.RunAsync(selector, session, cancellationToken)
                .ConfigureAwait(false);
            return result.HasSelection ? result.SelectedItem?.Value : null;
        };
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

    private static string FormatBoolean(bool value) => value ? "enabled" : "disabled";

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
