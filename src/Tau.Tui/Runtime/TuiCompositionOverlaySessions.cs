using Tau.Tui.Components;

namespace Tau.Tui.Runtime;

public static class TuiCompositionOverlaySessions
{
    public static async Task<TuiSelectorSessionResult> RunAsync(
        TuiSelectList selector,
        TuiCompositionSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(session);

        TuiSelectItem? selected = null;
        var cancelled = false;

        void OnSelected(TuiSelectItem item) => selected = item;
        void OnCancelled() => cancelled = true;

        selector.Selected += OnSelected;
        selector.Cancelled += OnCancelled;
        var handle = session.OpenOverlay(selector, CreateDefaultOptions(session));
        try
        {
            if (!session.IsStarted)
            {
                session.Start();
            }
            else
            {
                session.Render(force: true);
            }

            while (selected is null && !cancelled)
            {
                await session.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            }

            return cancelled
                ? TuiSelectorSessionResult.Cancelled
                : TuiSelectorSessionResult.Selected(selected!);
        }
        finally
        {
            session.CloseOverlay(handle);
            selector.Selected -= OnSelected;
            selector.Cancelled -= OnCancelled;
        }
    }

    public static async Task<TuiMultiSelectSessionResult> RunAsync(
        TuiMultiSelectList selector,
        TuiCompositionSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(session);

        IReadOnlyList<string>? selected = null;
        var cancelled = false;

        void OnSaved(IReadOnlyList<string>? values) => selected = values?.ToArray();
        void OnCancelled() => cancelled = true;

        selector.Saved += OnSaved;
        selector.Cancelled += OnCancelled;
        var handle = session.OpenOverlay(selector, CreateDefaultOptions(session, preferredWidth: 96));
        try
        {
            if (!session.IsStarted)
            {
                session.Start();
            }
            else
            {
                session.Render(force: true);
            }

            while (selected is null && !cancelled)
            {
                await session.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            }

            return cancelled
                ? TuiMultiSelectSessionResult.Cancelled
                : TuiMultiSelectSessionResult.Saved(selected);
        }
        finally
        {
            session.CloseOverlay(handle);
            selector.Saved -= OnSaved;
            selector.Cancelled -= OnCancelled;
        }
    }

    private static TuiTranscriptOverlayOptions CreateDefaultOptions(
        TuiCompositionSession session,
        int preferredWidth = 80)
    {
        var width = Math.Max(1, Math.Min(preferredWidth, session.Viewport.Width));
        var row = Math.Max(0, Math.Min(1, session.Viewport.MessageHeight - 1));
        var column = Math.Max(0, (session.Viewport.Width - width) / 2);
        return new TuiTranscriptOverlayOptions(Width: width, Row: row, Column: column);
    }
}
