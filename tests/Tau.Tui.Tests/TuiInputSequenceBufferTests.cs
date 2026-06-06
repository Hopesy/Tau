using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiInputSequenceBufferTests
{
    [Fact]
    public void InputSequenceBuffer_EmitsPlainCharactersAndCompleteCsiSequences()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process("ab\u001b[A");

        Assert.Equal(
            [
                Event.Data("a"),
                Event.Data("b"),
                Event.Data("\u001b[A"),
            ],
            events);
        Assert.Equal(string.Empty, buffer.Pending);
    }

    [Fact]
    public void InputSequenceBuffer_BuffersPartialSgrMouseSequenceAcrossChunks()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process("\u001b[<35");

        Assert.Empty(events);
        Assert.Equal("\u001b[<35", buffer.Pending);

        buffer.Process(";20;5mZ");

        Assert.Equal(
            [
                Event.Data("\u001b[<35;20;5m"),
                Event.Data("Z"),
            ],
            events);
        Assert.Equal(string.Empty, buffer.Pending);
    }

    [Fact]
    public void InputSequenceBuffer_KeepsMalformedSgrMouseSequencePendingUntilFlush()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process("\u001b[<;;m");
        var flushed = buffer.Flush();

        Assert.Empty(events);
        Assert.Equal(["\u001b[<;;m"], flushed);
    }

    [Fact]
    public void InputSequenceBuffer_BuffersOscDcsAndApcUntilTerminator()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process("\u001b]0;title");
        Assert.Empty(events);
        buffer.Process("\u0007\u001bP>|version");
        Assert.Equal([Event.Data("\u001b]0;title\u0007")], events);
        buffer.Process("\u001b\\\u001b_Gkitty");
        Assert.Equal(
            [
                Event.Data("\u001b]0;title\u0007"),
                Event.Data("\u001bP>|version\u001b\\"),
            ],
            events);
        buffer.Process("\u001b\\");

        Assert.Equal(
            [
                Event.Data("\u001b]0;title\u0007"),
                Event.Data("\u001bP>|version\u001b\\"),
                Event.Data("\u001b_Gkitty\u001b\\"),
            ],
            events);
    }

    [Fact]
    public void InputSequenceBuffer_HandlesSs3AndMetaEscapeSequences()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process("\u001bO");
        Assert.Empty(events);

        buffer.Process("P\u001bx");

        Assert.Equal(
            [
                Event.Data("\u001bOP"),
                Event.Data("\u001bx"),
            ],
            events);
    }

    [Fact]
    public void InputSequenceBuffer_EmitsPasteContentAndProcessesRemainder()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process("a\u001b[200~hello");

        Assert.Equal([Event.Data("a")], events);
        Assert.True(buffer.IsPasteMode);

        buffer.Process(" world\u001b[201~b\u001b[A");

        Assert.Equal(
            [
                Event.Data("a"),
                Event.Paste("hello world"),
                Event.Data("b"),
                Event.Data("\u001b[A"),
            ],
            events);
        Assert.False(buffer.IsPasteMode);
    }

    [Fact]
    public void InputSequenceBuffer_ConvertsSingleHighByteToMetaSequence()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process(stackalloc byte[] { 0xE1 });

        Assert.Equal([Event.Data("\u001ba")], events);
    }

    [Fact]
    public void InputSequenceBuffer_FlushesIncompletePendingSequence()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process("\u001b[");
        var flushed = buffer.Flush();

        Assert.Empty(events);
        Assert.Equal(["\u001b["], flushed);
        Assert.Equal(string.Empty, buffer.Pending);
    }

    [Fact]
    public void InputSequenceBuffer_ClearResetsPendingPasteState()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        var events = Capture(buffer);

        buffer.Process("\u001b[200~hello");
        buffer.Clear();
        buffer.Process("x");

        Assert.False(buffer.IsPasteMode);
        Assert.Equal([Event.Data("x")], events);
    }

    [Fact]
    public async Task InputSequenceBuffer_TimeoutEmitsIncompleteSequence()
    {
        using var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMilliseconds(10));
        var events = Capture(buffer);

        buffer.Process("\u001b[");
        await SpinUntilAsync(() => events.Count == 1);

        Assert.Equal([Event.Data("\u001b[")], events);
        Assert.Equal(string.Empty, buffer.Pending);
    }

    [Fact]
    public void InputSequenceBuffer_DestroyClearsStateAndPreventsFurtherUse()
    {
        var buffer = new TuiInputSequenceBuffer(TimeSpan.FromMinutes(1));
        buffer.Process("\u001b[");

        buffer.Destroy();

        Assert.Throws<ObjectDisposedException>(() => buffer.Process("x"));
        Assert.Throws<ObjectDisposedException>(() => buffer.Flush());
        Assert.Throws<ObjectDisposedException>(() => buffer.Clear());
    }

    private static List<Event> Capture(TuiInputSequenceBuffer buffer)
    {
        var events = new List<Event>();
        buffer.Data += value => events.Add(Event.Data(value));
        buffer.Paste += value => events.Add(Event.Paste(value));
        return events;
    }

    private static async Task SpinUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, timeout.Token);
        }
    }

    private readonly record struct Event(string Kind, string Value)
    {
        public static Event Data(string value) => new("data", value);

        public static Event Paste(string value) => new("paste", value);
    }
}
