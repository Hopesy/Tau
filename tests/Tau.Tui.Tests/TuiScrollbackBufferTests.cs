using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiScrollbackBufferTests
{
    [Fact]
    public void VisibleLines_FollowsBottomByDefault()
    {
        var buffer = new TuiScrollbackBuffer(height: 3);

        buffer.Append(["one", "two", "three", "four"]);

        Assert.True(buffer.IsFollowingBottom);
        Assert.Equal(["two", "three", "four"], buffer.VisibleLines());
    }

    [Fact]
    public void ScrollUpAndDown_ClampsToAvailableHistory()
    {
        var buffer = new TuiScrollbackBuffer(height: 2);
        buffer.Append(["one", "two", "three", "four"]);

        buffer.ScrollUp(10);
        Assert.Equal(2, buffer.ScrollOffsetFromBottom);
        Assert.Equal(["one", "two"], buffer.VisibleLines());

        buffer.ScrollDown();
        Assert.Equal(1, buffer.ScrollOffsetFromBottom);
        Assert.Equal(["two", "three"], buffer.VisibleLines());

        buffer.PageDown();
        Assert.Equal(0, buffer.ScrollOffsetFromBottom);
        Assert.Equal(["three", "four"], buffer.VisibleLines());
    }

    [Fact]
    public void Append_WhileScrolledUp_PreservesViewport()
    {
        var buffer = new TuiScrollbackBuffer(height: 2);
        buffer.Append(["one", "two", "three"]);
        buffer.ScrollUp();

        Assert.Equal(["one", "two"], buffer.VisibleLines());

        buffer.Append("four");

        Assert.False(buffer.IsFollowingBottom);
        Assert.Equal(["one", "two"], buffer.VisibleLines());
    }

    [Fact]
    public void Resize_ClampsScrollOffsetAndVisibleLines()
    {
        var buffer = new TuiScrollbackBuffer(height: 2);
        buffer.Append(["one", "two", "three", "four"]);
        buffer.ScrollUp(2);

        buffer.SetHeight(3);

        Assert.Equal(1, buffer.ScrollOffsetFromBottom);
        Assert.Equal(["one", "two", "three"], buffer.VisibleLines());
    }

    [Fact]
    public void MaxLines_TrimsOldestLines()
    {
        var buffer = new TuiScrollbackBuffer(height: 3, maxLines: 4);

        buffer.Append(["one", "two", "three", "four", "five"]);

        Assert.Equal(4, buffer.Count);
        Assert.Equal(["three", "four", "five"], buffer.VisibleLines());
        Assert.Equal(["two", "three", "four", "five"], buffer.Lines);
    }

    [Fact]
    public void Replace_ResetsToBottomAndNormalizesLines()
    {
        var buffer = new TuiScrollbackBuffer(height: 2);
        buffer.Append(["one", "two", "three"]);
        buffer.ScrollUp();

        buffer.Replace(["a\rb", "c\nd", null!]);

        Assert.True(buffer.IsFollowingBottom);
        Assert.Equal(0, buffer.ScrollOffsetFromBottom);
        Assert.Equal(["c d", string.Empty], buffer.VisibleLines());
        Assert.Equal(["ab", "c d", string.Empty], buffer.Lines);
    }

    [Fact]
    public void Clear_RemovesLinesAndResetsScrollOffset()
    {
        var buffer = new TuiScrollbackBuffer(height: 2);
        buffer.Append(["one", "two", "three"]);
        buffer.ScrollUp();

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.IsFollowingBottom);
        Assert.Empty(buffer.VisibleLines());
    }

    [Fact]
    public void PageUpAndScrollToBottom_UpdateViewport()
    {
        var buffer = new TuiScrollbackBuffer(height: 2);
        buffer.Append(["one", "two", "three", "four", "five"]);

        buffer.PageUp();

        Assert.Equal(2, buffer.ScrollOffsetFromBottom);
        Assert.Equal(["two", "three"], buffer.VisibleLines());

        buffer.ScrollToBottom();

        Assert.True(buffer.IsFollowingBottom);
        Assert.Equal(["four", "five"], buffer.VisibleLines());
    }

    [Fact]
    public void Constructor_ClampsHeightAndMaxLines()
    {
        var buffer = new TuiScrollbackBuffer(height: 0, maxLines: 0);

        buffer.Append(["one", "two"]);

        Assert.Equal(1, buffer.Height);
        Assert.Equal(1, buffer.MaxLines);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(["two"], buffer.VisibleLines());
    }

    [Fact]
    public void MaxLinesTrim_WhileScrolledUp_PreservesNearestViewport()
    {
        var buffer = new TuiScrollbackBuffer(height: 2, maxLines: 4);
        buffer.Append(["one", "two", "three", "four"]);
        buffer.ScrollUp(2);

        buffer.Append(["five", "six"]);

        Assert.Equal(["three", "four"], buffer.VisibleLines());
        Assert.Equal(2, buffer.ScrollOffsetFromBottom);
        Assert.Equal(["three", "four", "five", "six"], buffer.Lines);
    }

    [Fact]
    public void Lines_ReturnsReadOnlyView()
    {
        var buffer = new TuiScrollbackBuffer(height: 2);
        buffer.Append(["one", "two"]);

        Assert.IsNotType<List<string>>(buffer.Lines);
    }
}
