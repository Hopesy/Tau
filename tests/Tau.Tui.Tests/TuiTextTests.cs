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
}
