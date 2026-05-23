using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public sealed class TuiOverlayHost
{
    private readonly ITuiInputComponent _component;
    private readonly IConsoleKeyReader _keyReader;
    private readonly ITuiRenderSurface _surface;
    private readonly TuiDiffRenderer _renderer;

    public TuiOverlayHost(
        ITuiInputComponent component,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        TuiDiffRenderer? renderer = null)
    {
        _component = component ?? throw new ArgumentNullException(nameof(component));
        _keyReader = keyReader ?? throw new ArgumentNullException(nameof(keyReader));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _renderer = renderer ?? new TuiDiffRenderer();
    }

    public TuiRenderFrame? PreviousFrame => _renderer.PreviousFrame;

    public TuiRenderDiff Render(bool force = false)
    {
        var diff = _renderer.Render(
            _component,
            Math.Max(1, _surface.Width),
            Math.Max(1, _surface.Height),
            force);

        if (diff.RequiresFullRedraw || diff.Operations.Count > 0)
        {
            _surface.Apply(diff);
        }

        return diff;
    }

    public async ValueTask<TuiInputResult> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        var key = await _keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        var result = _component.HandleInput(key);
        if (result.Consumed)
        {
            Render();
        }

        return result;
    }
}
