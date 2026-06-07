using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public readonly record struct TuiSettingsListSessionResult(
    string? Id,
    string? Value,
    bool IsCancelled)
{
    public bool HasChange => !IsCancelled && Id is not null && Value is not null;

    public static TuiSettingsListSessionResult Changed(string id, string value) =>
        new(id ?? throw new ArgumentNullException(nameof(id)), value ?? string.Empty, IsCancelled: false);

    public static TuiSettingsListSessionResult Cancelled { get; } =
        new(null, null, IsCancelled: true);
}

public sealed class TuiSettingsListSession
{
    private readonly TuiSettingsList _selector;
    private readonly TuiOverlayHost _host;

    public TuiSettingsListSession(
        TuiSettingsList selector,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        TuiDiffRenderer? renderer = null)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _host = new TuiOverlayHost(selector, keyReader, surface, renderer);
    }

    public async Task<TuiSettingsListSessionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        string? changedId = null;
        string? changedValue = null;
        var cancelled = false;

        void OnChanged(string id, string value)
        {
            changedId = id;
            changedValue = value;
        }

        void OnCancelled() => cancelled = true;

        _selector.Changed += OnChanged;
        _selector.Cancelled += OnCancelled;
        try
        {
            _host.Render(force: true);
            while (changedId is null && !cancelled)
            {
                await _host.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            }

            return cancelled || changedId is null
                ? TuiSettingsListSessionResult.Cancelled
                : TuiSettingsListSessionResult.Changed(changedId, changedValue ?? string.Empty);
        }
        finally
        {
            _selector.Changed -= OnChanged;
            _selector.Cancelled -= OnCancelled;
        }
    }
}
