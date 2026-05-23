using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public readonly record struct TuiSelectorSessionResult(TuiSelectItem? SelectedItem, bool IsCancelled)
{
    public bool HasSelection => SelectedItem is not null && !IsCancelled;

    public static TuiSelectorSessionResult Selected(TuiSelectItem item) =>
        new(item ?? throw new ArgumentNullException(nameof(item)), IsCancelled: false);

    public static TuiSelectorSessionResult Cancelled { get; } =
        new(null, IsCancelled: true);
}

public sealed class TuiSelectorSession
{
    private readonly TuiSelectList _selector;
    private readonly TuiOverlayHost _host;

    public TuiSelectorSession(
        TuiSelectList selector,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        TuiDiffRenderer? renderer = null)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _host = new TuiOverlayHost(selector, keyReader, surface, renderer);
    }

    public async Task<TuiSelectorSessionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        TuiSelectItem? selected = null;
        var cancelled = false;

        void OnSelected(TuiSelectItem item) => selected = item;
        void OnCancelled() => cancelled = true;

        _selector.Selected += OnSelected;
        _selector.Cancelled += OnCancelled;
        try
        {
            _host.Render(force: true);
            while (selected is null && !cancelled)
            {
                await _host.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            }

            return cancelled || selected is null
                ? TuiSelectorSessionResult.Cancelled
                : TuiSelectorSessionResult.Selected(selected);
        }
        finally
        {
            _selector.Selected -= OnSelected;
            _selector.Cancelled -= OnCancelled;
        }
    }
}
