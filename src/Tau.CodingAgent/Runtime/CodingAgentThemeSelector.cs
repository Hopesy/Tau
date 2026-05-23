using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public static class CodingAgentThemeSelector
{
    public static Func<CodingAgentThemeStatus, string?, CancellationToken, Task<string?>> CreateConsoleSelector(
        IConsoleKeyReader keyReader,
        bool synchronizedOutput = true)
    {
        ArgumentNullException.ThrowIfNull(keyReader);

        return (status, currentTheme, cancellationToken) => SelectAsync(
            status,
            currentTheme,
            keyReader,
            TuiAnsiRenderSurface.ForConsole(synchronizedOutput),
            cancellationToken);
    }

    public static TuiSelectList CreateSelectList(
        CodingAgentThemeStatus status,
        string? currentTheme,
        int maxVisible = 8)
    {
        ArgumentNullException.ThrowIfNull(status);

        var items = status.Themes
            .Select(static theme => new TuiSelectItem(
                theme.Name,
                theme.Name,
                FormatDescription(theme)))
            .ToArray();
        var selector = new TuiSelectList(
            items,
            maxVisible: maxVisible,
            layout: new TuiSelectListLayout(MinPrimaryColumnWidth: 18, MaxPrimaryColumnWidth: 28));
        var current = FormatCurrentTheme(currentTheme);
        var index = Array.FindIndex(items, item => item.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            selector.SetSelectedIndex(index);
        }

        return selector;
    }

    public static async Task<string?> SelectAsync(
        CodingAgentThemeStatus status,
        string? currentTheme,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        var selector = CreateSelectList(status, currentTheme);
        if (selector.FilteredItems.Count == 0)
        {
            return null;
        }

        var result = await new TuiSelectorSession(selector, keyReader, surface)
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.HasSelection ? result.SelectedItem?.Value : null;
    }

    private static string FormatDescription(CodingAgentTheme theme) =>
        theme.FilePath is null ? theme.Scope : $"{theme.Scope} {theme.FilePath}";

    private static string FormatCurrentTheme(string? theme) =>
        string.IsNullOrWhiteSpace(theme) ? CodingAgentThemeStore.DefaultThemeName : theme.Trim();
}
