namespace Tau.Tui.Rendering;

public interface ITuiRenderSurface
{
    int Width { get; }
    int Height { get; }
    void Apply(TuiRenderDiff diff);
}
