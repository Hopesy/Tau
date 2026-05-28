namespace Tau.Tui.Rendering;

public sealed class TuiNullRenderSurface(int width = 120, int height = 40) : ITuiRenderSurface
{
    public int Width { get; } = Math.Max(1, width);
    public int Height { get; } = Math.Max(1, height);

    public void Apply(TuiRenderDiff diff)
    {
    }
}
