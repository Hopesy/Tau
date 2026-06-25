using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public static class TuiTestCollections
{
    public const string TuiKeyDecoderState = "Tui key decoder state";
}

[CollectionDefinition(TuiTestCollections.TuiKeyDecoderState, DisableParallelization = true)]
public sealed class TuiKeyDecoderStateCollection
{
}

[Collection(TuiTestCollections.TuiKeyDecoderState)]
public sealed class TuiKeyDecoderTests : IDisposable
{
    public void Dispose() => TuiKeyDecoder.SetKittyProtocolActive(false);

    [Fact]
    public void ParseKey_DecodesLegacyTerminalSequences()
    {
        TuiKeyDecoder.SetKittyProtocolActive(false);

        Assert.Equal("up", TuiKeyDecoder.ParseKey("\u001b[A"));
        Assert.Equal("ctrl+left", TuiKeyDecoder.ParseKey("\u001b[1;5D"));
        Assert.Equal("alt+right", TuiKeyDecoder.ParseKey("\u001bf"));
    }

    [Fact]
    public void MatchesKey_DecodesLegacyControlAndModifiedKeys()
    {
        TuiKeyDecoder.SetKittyProtocolActive(false);

        Assert.True(TuiKeyDecoder.MatchesKey("\u0003", "ctrl+c"));
        Assert.True(TuiKeyDecoder.MatchesKey("\u001b[Z", "shift+tab"));
        Assert.True(TuiKeyDecoder.MatchesKey("\u001b\r", "alt+enter"));
    }

    [Fact]
    public void ParseKey_TreatsShiftEnterAsModeAwareKittyAmbiguity()
    {
        try
        {
            TuiKeyDecoder.SetKittyProtocolActive(true);

            Assert.Equal("shift+enter", TuiKeyDecoder.ParseKey("\n"));
            Assert.True(TuiKeyDecoder.MatchesKey("\n", "shift+enter"));

            TuiKeyDecoder.SetKittyProtocolActive(false);
            Assert.Equal("enter", TuiKeyDecoder.ParseKey("\n"));
            Assert.False(TuiKeyDecoder.MatchesKey("\n", "shift+enter"));
        }
        finally
        {
            TuiKeyDecoder.SetKittyProtocolActive(false);
        }
    }

    [Fact]
    public void ParseKey_DecodesKittyCsiUAndBaseLayoutFallback()
    {
        TuiKeyDecoder.SetKittyProtocolActive(true);

        Assert.Equal("ctrl+c", TuiKeyDecoder.ParseKey("\u001b[99;5u"));
        Assert.Equal("shift+ctrl+c", TuiKeyDecoder.ParseKey("\u001b[67;6u"));
        Assert.Equal("ctrl+c", TuiKeyDecoder.ParseKey("\u001b[1089::99;5u"));
    }

    [Fact]
    public void DecodePrintableKey_DecodesKittyAndModifyOtherKeysPrintableTextOnly()
    {
        TuiKeyDecoder.SetKittyProtocolActive(true);

        Assert.Equal("A", TuiKeyDecoder.DecodePrintableKey("\u001b[97:65;2u"));
        Assert.Null(TuiKeyDecoder.DecodePrintableKey("\u001b[99;5u"));
        Assert.Equal("A", TuiKeyDecoder.DecodePrintableKey("\u001b[27;2;65~"));
    }

    [Fact]
    public void KeyEventHelpers_DetectReleaseAndRepeatWithoutTreatingPasteAsKeys()
    {
        Assert.True(TuiKeyDecoder.IsKeyRelease("\u001b[97;1:3u"));
        Assert.True(TuiKeyDecoder.IsKeyRepeat("\u001b[97;1:2u"));
        Assert.False(TuiKeyDecoder.IsKeyRelease("\u001b[200~90:62:3F:A5\u001b[201~"));
        Assert.False(TuiKeyDecoder.IsKeyRepeat("\u001b[200~90:62:3F:A5\u001b[201~"));
    }

    [Fact]
    public void ParseKey_TracksLastKittyEventType()
    {
        try
        {
            TuiKeyDecoder.SetKittyProtocolActive(true);

            Assert.Equal("a", TuiKeyDecoder.ParseKey("\u001b[97;1:2u"));
            Assert.Equal(TuiKeyEventType.Repeat, TuiKeyDecoder.LastEventType);

            Assert.Equal("a", TuiKeyDecoder.ParseKey("\u001b[97;1:3u"));
            Assert.Equal(TuiKeyEventType.Release, TuiKeyDecoder.LastEventType);
        }
        finally
        {
            TuiKeyDecoder.SetKittyProtocolActive(false);
        }
    }
}
