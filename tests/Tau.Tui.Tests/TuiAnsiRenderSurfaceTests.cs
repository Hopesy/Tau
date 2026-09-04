using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiAnsiRenderSurfaceTests
{
    [Fact]
    public void Apply_FullRedrawClearsScreenAndWritesAllLines()
    {
        using var writer = new StringWriter();
        var surface = new TuiAnsiRenderSurface(writer, () => 80, () => 24);
        var diff = new TuiRenderDiff(
            RequiresFullRedraw: true,
            Reason: "first render",
            Operations:
            [
                TuiRenderOperation.ReplaceLine(0, "one"),
                TuiRenderOperation.ReplaceLine(1, "two"),
            ]);

        surface.Apply(diff);

        Assert.Equal("\u001b[?2026h\u001b[2J\u001b[Hone\r\ntwo\u001b[?2026l", writer.ToString());
    }

    [Fact]
    public void Apply_LineDiffMovesToRowsAndClearsBeforeWriting()
    {
        using var writer = new StringWriter();
        var surface = new TuiAnsiRenderSurface(writer, () => 80, () => 24);
        var diff = new TuiRenderDiff(
            RequiresFullRedraw: false,
            Reason: "line diff",
            Operations:
            [
                TuiRenderOperation.ReplaceLine(2, "third"),
                TuiRenderOperation.ClearLine(4),
            ]);

        surface.Apply(diff);

        Assert.Equal("\u001b[?2026h\u001b[3;1H\u001b[2Kthird\u001b[5;1H\u001b[2K\u001b[?2026l", writer.ToString());
    }

    [Fact]
    public void Apply_EmptyDiffDoesNotWrite()
    {
        using var writer = new StringWriter();
        var surface = new TuiAnsiRenderSurface(writer, () => 80, () => 24);

        surface.Apply(TuiRenderDiff.Empty);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void DimensionsClampToAtLeastOne()
    {
        using var writer = new StringWriter();
        var surface = new TuiAnsiRenderSurface(writer, () => 0, () => -10);

        Assert.Equal(1, surface.Width);
        Assert.Equal(1, surface.Height);
    }

    [Fact]
    public void Apply_CanTemporarilyDisableAutoWrapForFullWidthConsoleFrames()
    {
        using var writer = new StringWriter();
        var surface = new TuiAnsiRenderSurface(
            writer,
            () => 80,
            () => 24,
            synchronizedOutput: true,
            avoidLastColumnWrap: true);

        surface.Apply(new TuiRenderDiff(
            RequiresFullRedraw: false,
            Reason: "full width",
            Operations: [TuiRenderOperation.ReplaceLine(0, new string('-', 80))]));

        Assert.Equal(
            "\u001b[?2026h\u001b[?7l\u001b[1;1H\u001b[2K" + new string('-', 80) +
            "\u001b[?7h\u001b[?2026l",
            writer.ToString());
    }

    [Fact]
    public void SetCursorVisible_WritesOnlyWhenVisibilityChanges()
    {
        using var writer = new StringWriter();
        var surface = new TuiAnsiRenderSurface(writer, () => 80, () => 24);

        surface.SetCursorVisible(false);
        surface.SetCursorVisible(false);
        surface.SetCursorVisible(true);
        surface.SetCursorVisible(true);

        Assert.Equal("\u001b[?25l\u001b[?25h", writer.ToString());
    }
}
