using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiTerminalColorsTests
{
    [Fact]
    public void TerminalColors_DetectsOsc11BackgroundColorResponse()
    {
        Assert.True(TuiTerminalColors.IsOsc11BackgroundColorResponse("\u001b]11;#112233\u0007"));
        Assert.True(TuiTerminalColors.IsOsc11BackgroundColorResponse("\u001b]11;rgb:1111/2222/3333\u001b\\"));
        Assert.False(TuiTerminalColors.IsOsc11BackgroundColorResponse("\u001b]10;#112233\u0007"));
        Assert.False(TuiTerminalColors.IsOsc11BackgroundColorResponse("11;#112233"));
    }

    [Fact]
    public void TerminalColors_ParsesOsc11HexColors()
    {
        Assert.Equal(new TuiRgbColor(17, 34, 51), TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;#112233\u0007"));
        Assert.Equal(new TuiRgbColor(255, 0, 128), TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;#ffff00008000\u001b\\"));
        Assert.Equal(new TuiRgbColor(170, 187, 204), TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;  #aabbcc  \u0007"));
    }

    [Fact]
    public void TerminalColors_ParsesOsc11RgbColors()
    {
        Assert.Equal(new TuiRgbColor(255, 0, 128), TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;rgb:ffff/0000/8000\u0007"));
        Assert.Equal(new TuiRgbColor(0, 255, 128), TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;rgba:0000/ffff/8000/ffff\u0007"));
        Assert.Equal(new TuiRgbColor(136, 255, 0), TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;8/f/0\u0007"));
    }

    [Fact]
    public void TerminalColors_ReturnsNullForInvalidOsc11Colors()
    {
        Assert.Null(TuiTerminalColors.ParseOsc11BackgroundColor("not a response"));
        Assert.Null(TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;#12345g\u0007"));
        Assert.Null(TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;#11223344\u0007"));
        Assert.Null(TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;rgb:ffff/0000\u0007"));
        Assert.Null(TuiTerminalColors.ParseOsc11BackgroundColor("\u001b]11;rgb:ffff/nothex/0000\u0007"));
    }

    [Fact]
    public void TerminalColors_ParsesColorSchemeReports()
    {
        Assert.Equal(TuiTerminalColorScheme.Dark, TuiTerminalColors.ParseTerminalColorSchemeReport("\u001b[?997;1n"));
        Assert.Equal(TuiTerminalColorScheme.Light, TuiTerminalColors.ParseTerminalColorSchemeReport("\u001b[?997;2n"));
        Assert.Null(TuiTerminalColors.ParseTerminalColorSchemeReport("\u001b[?997;3n"));
        Assert.Null(TuiTerminalColors.ParseTerminalColorSchemeReport("\u001b[997;1n"));
    }
}
