using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiTextTests
{
    [Fact]
    public void WrapTextWithAnsi_ReopensActiveStyleOnWrappedLine()
    {
        var lines = TuiText.WrapTextWithAnsi("\u001b[31mabcdef", 3);

        Assert.Equal(
            [
                "\u001b[31mabc",
                "\u001b[31mdef"
            ],
            lines);
        Assert.All(lines, line => Assert.Equal(3, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void WrapTextWithAnsi_ClosesUnderlineAtSoftLineBreak()
    {
        var lines = TuiText.WrapTextWithAnsi("\u001b[4mabcdef", 3);

        Assert.Equal(
            [
                "\u001b[4mabc\u001b[24m",
                "\u001b[4mdef"
            ],
            lines);
        Assert.All(lines, line => Assert.Equal(3, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void SliceWithWidth_PreservesAnsiCodesFromBeforeSliceStart()
    {
        var slice = TuiText.SliceWithWidth("\u001b[31mabcdef\u001b[0m", startColumn: 2, length: 3);

        Assert.Equal("\u001b[31mcde", slice.Text);
        Assert.Equal(3, slice.Width);
        Assert.Equal(3, TuiText.VisibleWidth(slice.Text));
    }

    [Fact]
    public void SliceWithWidth_HandlesWideCharacterBoundaries()
    {
        var loose = TuiText.SliceWithWidth("a表b", startColumn: 1, length: 1);
        var strict = TuiText.SliceWithWidth("a表b", startColumn: 1, length: 1, strict: true);

        Assert.Equal("表", loose.Text);
        Assert.Equal(2, loose.Width);
        Assert.Equal(string.Empty, strict.Text);
        Assert.Equal(0, strict.Width);
    }

    [Fact]
    public void NormalizeTerminalOutput_DecomposesThaiAndLaoAmVowels()
    {
        Assert.Equal("ก\u0e4d\u0e32 ລ\u0ecd\u0eb2", TuiText.NormalizeTerminalOutput("กำ ລຳ"));
    }

    [Fact]
    public void ExtractSegments_PreservesBeforeAndInheritedAfterStyles()
    {
        var segments = TuiText.ExtractSegments(
            "\u001b[31mabcdef\u001b[0m",
            beforeEnd: 2,
            afterStart: 3,
            afterLength: 2);

        Assert.Equal("\u001b[31mab", segments.Before);
        Assert.Equal(2, segments.BeforeWidth);
        Assert.Equal("\u001b[31mde", segments.After);
        Assert.Equal(2, segments.AfterWidth);
    }

    [Fact]
    public void ExtractSegments_HandlesWideCharacterAfterBoundary()
    {
        var loose = TuiText.ExtractSegments("a表bc", beforeEnd: 1, afterStart: 1, afterLength: 1);
        var strict = TuiText.ExtractSegments("a表bc", beforeEnd: 1, afterStart: 1, afterLength: 1, strictAfter: true);

        Assert.Equal("a", loose.Before);
        Assert.Equal(1, loose.BeforeWidth);
        Assert.Equal("表", loose.After);
        Assert.Equal(2, loose.AfterWidth);
        Assert.Equal(string.Empty, strict.After);
        Assert.Equal(0, strict.AfterWidth);
    }

    [Fact]
    public void TruncateToVisualLines_ReturnsEmptyForEmptyText()
    {
        var result = TuiText.TruncateToVisualLines(string.Empty, maxVisualLines: 5, width: 12);

        Assert.Empty(result.VisualLines);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void TruncateToVisualLines_TakesLastWrappedVisualLines()
    {
        var result = TuiText.TruncateToVisualLines("one two three four", maxVisualLines: 2, width: 5);

        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(
            [
                "three",
                "four "
            ],
            result.VisualLines);
        Assert.All(result.VisualLines, line => Assert.Equal(5, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void TruncateToVisualLines_AppliesHorizontalPadding()
    {
        var result = TuiText.TruncateToVisualLines("alpha beta", maxVisualLines: 3, width: 8, paddingX: 1);

        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(
            [
                " alpha  ",
                " beta   "
            ],
            result.VisualLines);
        Assert.All(result.VisualLines, line => Assert.Equal(8, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void TruncateToVisualLines_PreservesAnsiStyleAcrossWrappedTail()
    {
        var result = TuiText.TruncateToVisualLines("\u001b[31mabcdef", maxVisualLines: 2, width: 3);

        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(
            [
                "\u001b[31mabc",
                "\u001b[31mdef"
            ],
            result.VisualLines);
        Assert.All(result.VisualLines, line => Assert.Equal(3, TuiText.VisibleWidth(line)));
    }
}
