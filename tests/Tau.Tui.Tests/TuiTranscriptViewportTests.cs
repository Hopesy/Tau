using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiTranscriptViewportTests
{
    [Fact]
    public void Render_FollowsBottomByDefaultAndKeepsStatusLast()
    {
        var viewport = new TuiTranscriptViewport(width: 16, height: 4, statusLeft: "ready", statusRight: "model");

        viewport.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
                new TuiMessage(TuiMessageRole.System, "four"),
            ]);

        var rows = viewport.Render();

        Assert.True(viewport.IsFollowingBottom);
        Assert.Equal(4, rows.Count);
        Assert.Equal(["tau> two", "tool> three", "system> four"], MessageRows(rows, viewport));
        Assert.Equal("ready      model", rows[^1]);
        Assert.All(rows, row => Assert.Equal(16, TuiText.VisibleWidth(row)));
    }

    [Fact]
    public void Append_WhileScrolledUp_DoesNotStealViewport()
    {
        var viewport = new TuiTranscriptViewport(width: 18, height: 3, statusLeft: "status");
        viewport.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
            ]);

        viewport.ScrollUp();
        var before = MessageRows(viewport.Render(), viewport);

        viewport.AppendMessage(new TuiMessage(TuiMessageRole.System, "four"));

        Assert.False(viewport.IsFollowingBottom);
        Assert.Equal(["you> one", "tau> two"], before);
        Assert.Equal(["you> one", "tau> two"], MessageRows(viewport.Render(), viewport));
    }

    [Fact]
    public void Append_AfterScrollBottom_FollowsNewMessages()
    {
        var viewport = new TuiTranscriptViewport(width: 18, height: 3);
        viewport.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
            ]);
        viewport.ScrollUp();
        viewport.AppendMessage(new TuiMessage(TuiMessageRole.System, "four"));

        viewport.ScrollBottom();
        viewport.AppendMessage(new TuiMessage(TuiMessageRole.Error, "five"));

        Assert.True(viewport.IsFollowingBottom);
        Assert.Equal(["system> four", "error> five"], MessageRows(viewport.Render(), viewport));
    }

    [Fact]
    public void Resize_ClampsScrollOffsetAndKeepsStatusOnLastLine()
    {
        var viewport = new TuiTranscriptViewport(width: 18, height: 3, statusLeft: "ready");
        viewport.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
                new TuiMessage(TuiMessageRole.System, "four"),
            ]);
        viewport.ScrollUp(99);

        viewport.Resize(width: 18, height: 5);

        var rows = viewport.Render();
        Assert.Equal(0, viewport.ScrollOffsetFromBottom);
        Assert.Equal(5, rows.Count);
        Assert.Equal(["you> one", "tau> two", "tool> three", "system> four"], MessageRows(rows, viewport));
        Assert.Equal("ready             ", rows[^1]);
    }

    [Fact]
    public void Render_PadsEmptyMessageRowsAboveTranscript()
    {
        var viewport = new TuiTranscriptViewport(width: 12, height: 4, statusRight: "ok");

        viewport.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));

        var rows = viewport.Render();
        Assert.Equal(["", "", "you> one"], MessageRows(rows, viewport));
        Assert.Equal("          ok", rows[^1]);
    }

    [Fact]
    public void Render_TruncatesNarrowMessagesAndStatusToWidth()
    {
        var viewport = new TuiTranscriptViewport(width: 8, height: 3, statusLeft: "left-status", statusRight: "ok");

        viewport.AppendMessage(new TuiMessage(TuiMessageRole.Assistant, "supercalifragilistic"));

        var rows = viewport.Render();

        Assert.Equal(3, rows.Count);
        Assert.Equal("left- ok", rows[^1]);
        Assert.All(rows, row => Assert.Equal(8, TuiText.VisibleWidth(row)));
    }

    [Fact]
    public void Render_MultilineStatusReservesRowsBelowTranscript()
    {
        var viewport = new TuiTranscriptViewport(width: 18, height: 5);
        viewport.SetStatusLines(
            [
                new TuiStatusBarLine("~/repo (main)", ""),
                new TuiStatusBarLine("in12 out4", "gpt"),
                new TuiStatusBarLine("build ok", "")
            ]);
        viewport.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
            ]);

        var rows = viewport.Render();

        Assert.Equal(3, viewport.StatusHeight);
        Assert.Equal(2, viewport.MessageHeight);
        Assert.Equal(["tau> two", "tool> three"], MessageRows(rows, viewport));
        Assert.Equal("~/repo (main)     ", rows[^3]);
        Assert.Equal("in12 out4      gpt", rows[^2]);
        Assert.Equal("build ok          ", rows[^1]);
        Assert.All(rows, row => Assert.Equal(18, TuiText.VisibleWidth(row)));
    }

    [Fact]
    public void Resize_RewrapsMessagesWhenWidthChangesAndRestoresScrolledOffset()
    {
        var viewport = new TuiTranscriptViewport(width: 24, height: 3);
        viewport.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "alpha beta gamma"),
                new TuiMessage(TuiMessageRole.Assistant, "delta epsilon zeta"),
                new TuiMessage(TuiMessageRole.Tool, "eta theta iota"),
            ]);
        viewport.ScrollUp();

        viewport.Resize(width: 12, height: 3);

        Assert.False(viewport.IsFollowingBottom);
        Assert.Equal(1, viewport.ScrollOffsetFromBottom);
        Assert.All(viewport.Render(), row => Assert.Equal(12, TuiText.VisibleWidth(row)));
    }

    private static IReadOnlyList<string> MessageRows(IReadOnlyList<string> rows, TuiTranscriptViewport viewport) =>
        rows.Take(viewport.MessageHeight).Select(row => row.TrimEnd()).ToArray();
}
