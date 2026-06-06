using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiLoaderTests
{
    [Fact]
    public void Loader_RendersInitialFrameWithBlankLeadingLineAndMessage()
    {
        var loader = new TuiLoader("Working");

        var lines = loader.Render(12);

        Assert.Equal(0, loader.CurrentFrameIndex);
        Assert.Equal("", lines[0]);
        Assert.Equal(" \u280b Working  ", lines[1]);
        Assert.All(lines, line => Assert.True(TuiText.VisibleWidth(line) <= 12));
    }

    [Fact]
    public void Loader_AdvanceFrameCyclesSpinnerAndRequestsRender()
    {
        var renderRequests = 0;
        var loader = new TuiLoader(
            "Loading...",
            requestRender: () => renderRequests++,
            frames: ["a", "b"]);

        loader.AdvanceFrame();
        var secondFrame = loader.Render(14);
        loader.AdvanceFrame();
        var firstFrame = loader.Render(14);

        Assert.Equal(2, renderRequests);
        Assert.Equal(" b Loading... ", secondFrame[1]);
        Assert.Equal(" a Loading... ", firstFrame[1]);
    }

    [Fact]
    public void Loader_SetMessageUpdatesDisplayAndSkipsNoOpRenderRequest()
    {
        var renderRequests = 0;
        var loader = new TuiLoader("Loading...", requestRender: () => renderRequests++);

        loader.SetMessage("Done");
        loader.SetMessage("Done");

        Assert.Equal(1, renderRequests);
        Assert.Equal(" \u280b Done   ", loader.Render(10)[1]);
    }

    [Fact]
    public void Loader_AppliesSpinnerAndMessageFormattersWithoutBreakingVisibleWidth()
    {
        var loader = new TuiLoader(
            "Loading",
            spinnerFormatter: static value => $"\u001b[36m{value}\u001b[0m",
            messageFormatter: static value => $"\u001b[2m{value}\u001b[0m");

        var line = loader.Render(12)[1];

        Assert.Contains("\u001b[36m", line, StringComparison.Ordinal);
        Assert.Contains("\u001b[2m", line, StringComparison.Ordinal);
        Assert.Equal(12, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void Loader_StartStopManageTimerStateAndDisposeStops()
    {
        using var loader = new TuiLoader("Loading...");

        loader.Start();
        loader.Start();
        Assert.True(loader.IsRunning);

        loader.Stop();
        Assert.False(loader.IsRunning);

        loader.Start();
        loader.Dispose();

        Assert.False(loader.IsRunning);
        Assert.Throws<ObjectDisposedException>(loader.Start);
    }

    [Fact]
    public void CancellableLoader_EscapeAbortsSignalAndInvokesCallbackOnce()
    {
        using var loader = new TuiCancellableLoader("Working");
        var aborts = 0;
        loader.Aborted += () => aborts++;

        var first = loader.HandleInput(Key(ConsoleKey.Escape));
        var second = loader.HandleInput(Key(ConsoleKey.Escape));

        Assert.True(first.Consumed);
        Assert.True(second.Consumed);
        Assert.True(loader.IsAborted);
        Assert.True(loader.Signal.IsCancellationRequested);
        Assert.Equal(1, aborts);
    }

    [Fact]
    public void CancellableLoader_CtrlCAbortsAndOtherKeysAreIgnored()
    {
        using var loader = new TuiCancellableLoader("Working");

        Assert.False(loader.HandleInput(new ConsoleKeyInfo('x', ConsoleKey.X, shift: false, alt: false, control: false)).Consumed);

        var result = loader.HandleInput(Key(ConsoleKey.C, keyChar: '\x03', control: true));

        Assert.True(result.Consumed);
        Assert.True(loader.IsAborted);
    }

    [Fact]
    public void CancellableLoader_DelegatesRenderToLoader()
    {
        using var loader = new TuiCancellableLoader("Working", frames: ["x"]);

        Assert.Equal(["", " x Working  "], loader.Render(12));
    }

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char keyChar = '\0',
        bool control = false) =>
        new(keyChar, key, shift: false, alt: false, control: control);
}
