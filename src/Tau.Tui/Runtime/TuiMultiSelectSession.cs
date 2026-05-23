using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public readonly record struct TuiMultiSelectSessionResult(
    IReadOnlyList<string>? SelectedValues,
    bool IsCancelled)
{
    public bool HasSelection => !IsCancelled;

    public static TuiMultiSelectSessionResult Saved(IReadOnlyList<string>? selectedValues) =>
        new(selectedValues?.ToArray(), IsCancelled: false);

    public static TuiMultiSelectSessionResult Cancelled { get; } =
        new(null, IsCancelled: true);
}

public sealed class TuiMultiSelectSession
{
    private readonly TuiMultiSelectList _selector;
    private readonly TuiOverlayHost _host;

    public TuiMultiSelectSession(
        TuiMultiSelectList selector,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        TuiDiffRenderer? renderer = null)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _host = new TuiOverlayHost(selector, keyReader, surface, renderer);
    }

    public async Task<TuiMultiSelectSessionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string>? selected = null;
        var saved = false;
        var cancelled = false;

        void OnSaved(IReadOnlyList<string>? values)
        {
            selected = values?.ToArray();
            saved = true;
        }

        void OnCancelled() => cancelled = true;

        _selector.Saved += OnSaved;
        _selector.Cancelled += OnCancelled;
        try
        {
            _host.Render(force: true);
            while (!saved && !cancelled)
            {
                await _host.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            }

            return cancelled
                ? TuiMultiSelectSessionResult.Cancelled
                : TuiMultiSelectSessionResult.Saved(selected);
        }
        finally
        {
            _selector.Saved -= OnSaved;
            _selector.Cancelled -= OnCancelled;
        }
    }
}
