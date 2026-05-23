using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiMessageStatusComponentTests
{
    [Fact]
    public void MessageArea_RendersWrappedMessagesWithContinuationIndent()
    {
        var area = new TuiMessageArea(
            [
                new TuiMessage(TuiMessageRole.Assistant, "alpha beta gamma"),
            ]);

        var lines = area.Render(16);

        Assert.Equal(["tau> alpha beta ", "     gamma      "], lines);
        Assert.All(lines, line => Assert.Equal(16, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_TakesLastVisibleLinesForBottomAnchoredTranscript()
    {
        var area = new TuiMessageArea(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
            ],
            maxVisibleLines: 2);

        var lines = area.Render(20);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("tau> two", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("tool> three", lines[1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(20, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_StaticRendererKeepsMultilineContentInOrder()
    {
        var lines = TuiMessageArea.RenderMessages(
            [
                new TuiMessage(TuiMessageRole.User, "first line\nsecond line"),
            ],
            width: 18);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("you> first line", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("     second line", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBar_AlignsRightSegmentWithoutOverlappingLeft()
    {
        var line = TuiStatusBar.RenderLine("model: openai/gpt-5", "tokens 12k", 32);

        Assert.Equal("model: openai/gpt-5   tokens 12k", line);
        Assert.Equal(32, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void StatusBar_TruncatesLeftBeforeRightWhenNarrow()
    {
        var line = TuiStatusBar.RenderLine("long-left-segment", "ready", 12);

        Assert.Equal("long-l ready", line);
        Assert.Equal(12, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void StatusBar_RightOnlyIsRightAligned()
    {
        var bar = new TuiStatusBar(right: "ready");

        Assert.Equal(["     ready"], bar.Render(10));
    }
}
