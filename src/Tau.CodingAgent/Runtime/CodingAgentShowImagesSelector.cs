using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public static class CodingAgentShowImagesSelector
{
    public const string EnabledValue = "true";
    public const string DisabledValue = "false";

    public static Func<bool, CancellationToken, Task<bool?>> CreateConsoleSelector(
        IConsoleKeyReader keyReader,
        bool synchronizedOutput = true)
    {
        ArgumentNullException.ThrowIfNull(keyReader);

        return async (currentValue, cancellationToken) =>
        {
            var selected = await SelectAsync(
                    currentValue,
                    keyReader,
                    TuiAnsiRenderSurface.ForConsole(synchronizedOutput),
                    cancellationToken)
                .ConfigureAwait(false);
            return selected is null ? null : ParseValue(selected);
        };
    }

    public static Func<bool, CancellationToken, Task<bool?>> CreateCompositionSelector(
        TuiCompositionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return async (currentValue, cancellationToken) =>
        {
            var selector = CreateSelectList(currentValue);
            var result = await TuiCompositionOverlaySessions.RunAsync(selector, session, cancellationToken)
                .ConfigureAwait(false);
            return result.HasSelection ? ParseValue(result.SelectedItem?.Value) : null;
        };
    }

    public static TuiSelectList CreateSelectList(bool currentValue, int maxVisible = 5)
    {
        var items = new[]
        {
            new TuiSelectItem(EnabledValue, "Yes", "Show images inline in terminal"),
            new TuiSelectItem(DisabledValue, "No", "Show text placeholder instead")
        };
        var selector = new TuiSelectList(
            items,
            maxVisible: maxVisible,
            layout: new TuiSelectListLayout(MinPrimaryColumnWidth: 12, MaxPrimaryColumnWidth: 32));
        selector.SetSelectedIndex(currentValue ? 0 : 1);
        return selector;
    }

    public static ITuiInputComponent CreateSubmenu(
        string currentValue,
        Action<string?> done)
    {
        ArgumentNullException.ThrowIfNull(done);

        var selector = CreateSelectList(ParseValue(currentValue) ?? true);
        selector.Selected += item => done(item.Value);
        selector.Cancelled += () => done(null);
        return selector;
    }

    public static async Task<string?> SelectAsync(
        bool currentValue,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        var selector = CreateSelectList(currentValue);
        var result = await new TuiSelectorSession(selector, keyReader, surface)
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.HasSelection ? result.SelectedItem?.Value : null;
    }

    public static bool? ParseValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            EnabledValue or "yes" => true,
            DisabledValue or "no" => false,
            _ => null
        };
    }
}
