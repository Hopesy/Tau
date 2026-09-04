using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiSpectreRendererTests
{
    [Fact]
    public void RenderPanel_UsesRoundedBorderAndStableWidth()
    {
        var renderer = new TuiSpectreRenderer();

        var lines = renderer.RenderPanel("tool", "output", width: 32);

        Assert.Contains(lines, line => line.Contains("╭", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("tool", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("output", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("╰", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("[bold yellow]", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\u001b[", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(32, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void RenderMarkup_PreservesSpectreAnsiStylesWithoutChangingWidth()
    {
        var renderer = new TuiSpectreRenderer();

        var lines = renderer.RenderMarkup("[green]ok[/] [bold]ready[/]", width: 24);

        Assert.Contains(lines, line => line.Contains("ok", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("ready", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\u001b[", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(24, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void RenderJsonPanel_UsesSpectreJsonStylesWithoutChangingWidth()
    {
        var renderer = new TuiSpectreRenderer();

        var lines = renderer.RenderJsonPanel("args", "{\"enabled\":true,\"count\":2}", width: 36);

        Assert.Contains(lines, line => line.Contains("enabled", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("count", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("args", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\u001b[", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(36, TuiText.VisibleWidth(line)));
    }
}
