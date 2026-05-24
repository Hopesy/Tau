using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiTranscriptSessionTests
{
    [Fact]
    public void Start_ForcesInitialRenderAndMarksSessionStarted()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface, statusLeft: "ready", statusRight: "gpt");

        session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        var result = session.Start();

        Assert.True(session.IsStarted);
        Assert.Same(result, session.LastRenderResult);
        Assert.True(result.Diff.RequiresFullRedraw);
        Assert.Equal("first render", result.Diff.Reason);
        Assert.Equal(["", "", "you> one"], MessageRows(result.Frame, session));
        Assert.Equal("ready            gpt", result.Frame.Lines[^1]);
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void AppendMessage_WhenStartedAutoRendersIncrementalDiff()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface);
        session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        session.Start();
        surface.Clear();

        var result = session.AppendMessage(new TuiMessage(TuiMessageRole.Assistant, "two"));

        Assert.NotNull(result);
        Assert.Same(result, session.LastRenderResult);
        Assert.False(result.Diff.RequiresFullRedraw);
        Assert.Equal("line diff", result.Diff.Reason);
        Assert.Equal(["", "you> one", "tau> two"], MessageRows(result.Frame, session));
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void AppendMessage_WhenStoppedUpdatesViewportWithoutRendering()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface);

        var result = session.AppendMessage(new TuiMessage(TuiMessageRole.Tool, "queued"));

        Assert.Null(result);
        Assert.False(session.IsStarted);
        Assert.Null(session.LastRenderResult);
        Assert.Empty(surface.Diffs);
        Assert.Single(session.Viewport.Messages);

        var started = session.Start();

        Assert.True(started.Diff.RequiresFullRedraw);
        Assert.Equal(["", "", "tool> queued"], MessageRows(started.Frame, session));
    }

    [Fact]
    public void SetStatus_WhenStartedRendersOnlyStatusRow()
    {
        var surface = new MemoryRenderSurface(width: 16, height: 3);
        var session = new TuiTranscriptSession(surface, statusLeft: "ready", statusRight: "gpt");
        session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        session.Start();
        surface.Clear();

        var result = session.SetStatus("busy", "haiku");

        Assert.NotNull(result);
        var operation = Assert.Single(result.Diff.Operations);
        Assert.False(result.Diff.RequiresFullRedraw);
        Assert.Equal(2, operation.Row);
        Assert.Equal("busy       haiku", operation.Text);
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void HandleInput_PageUpScrollsViewportAndRenders()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var session = StartedSession(surface);
        surface.Clear();

        var input = session.HandleInput(Key(ConsoleKey.PageUp));

        Assert.True(input.Consumed);
        Assert.Equal(TuiTranscriptInputAction.ScrollPageUp, input.Action);
        Assert.NotNull(input.RenderResult);
        Assert.False(session.Viewport.IsFollowingBottom);
        Assert.Equal(2, session.Viewport.ScrollOffsetFromBottom);
        var renderResult = input.RenderResult;
        Assert.Equal(["tau> two", "tool> three"], MessageRows(renderResult!.Frame, session));
        Assert.Same(renderResult.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void HandleInput_EndScrollsBackToBottomAndRenders()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var session = StartedSession(surface);
        session.HandleInput(Key(ConsoleKey.PageUp));
        surface.Clear();

        var input = session.HandleInput(Key(ConsoleKey.End));

        Assert.True(input.Consumed);
        Assert.Equal(TuiTranscriptInputAction.ScrollBottom, input.Action);
        Assert.True(session.Viewport.IsFollowingBottom);
        var renderResult = input.RenderResult;
        Assert.NotNull(renderResult);
        Assert.Equal(["system> four", "error> five"], MessageRows(renderResult.Frame, session));
        Assert.Same(renderResult.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void HandleInput_HomeScrollsToTopAndRenders()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var session = StartedSession(surface);
        surface.Clear();

        var input = session.HandleInput(Key(ConsoleKey.Home));

        Assert.True(input.Consumed);
        Assert.Equal(TuiTranscriptInputAction.ScrollTop, input.Action);
        Assert.False(session.Viewport.IsFollowingBottom);
        Assert.Equal(3, session.Viewport.ScrollOffsetFromBottom);
        var renderResult = input.RenderResult;
        Assert.NotNull(renderResult);
        Assert.Equal(["you> one", "tau> two"], MessageRows(renderResult.Frame, session));
        Assert.Same(renderResult.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void HandleInput_UnmappedKeyDoesNotRender()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var session = StartedSession(surface);
        surface.Clear();

        var input = session.HandleInput(new ConsoleKeyInfo('x', ConsoleKey.X, shift: false, alt: false, control: false));

        Assert.False(input.Consumed);
        Assert.Equal(TuiTranscriptInputAction.None, input.Action);
        Assert.Null(input.RenderResult);
        Assert.Empty(surface.Diffs);
    }

    [Fact]
    public async Task ReadInputAsync_UsesInjectedKeyReader()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var reader = new ScriptedKeyReader();
        reader.Enqueue(Key(ConsoleKey.PageUp));
        var session = StartedSession(surface, reader);
        surface.Clear();

        var input = await session.ReadInputAsync();

        Assert.True(input.Consumed);
        Assert.Equal(TuiTranscriptInputAction.ScrollPageUp, input.Action);
        Assert.NotNull(input.RenderResult);
        Assert.Single(surface.Diffs);
    }

    [Fact]
    public async Task ReadInputAsync_WithoutKeyReaderThrowsClearError()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var session = new TuiTranscriptSession(surface);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.ReadInputAsync());

        Assert.Equal("TuiTranscriptSession requires an IConsoleKeyReader to read input.", ex.Message);
    }

    [Fact]
    public void Stop_ResetsFrameAndDisablesAutoRenderUntilRestart()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface);
        session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        session.Start();
        surface.Clear();

        session.Stop();
        var stoppedAppend = session.AppendMessage(new TuiMessage(TuiMessageRole.Assistant, "two"));

        Assert.False(session.IsStarted);
        Assert.Null(stoppedAppend);
        Assert.Null(session.LastRenderResult);
        Assert.Null(session.Host.PreviousFrame);
        Assert.Empty(surface.Diffs);

        var restarted = session.Start();

        Assert.True(session.IsStarted);
        Assert.True(restarted.Diff.RequiresFullRedraw);
        Assert.Equal("first render", restarted.Diff.Reason);
        Assert.Equal(["", "you> one", "tau> two"], MessageRows(restarted.Frame, session));
    }

    [Fact]
    public void AutoRenderFalse_MutationsDoNotRenderUntilExplicitRender()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface, autoRender: false);
        session.Start();
        surface.Clear();

        var appendResult = session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));

        Assert.Null(appendResult);
        Assert.Empty(surface.Diffs);

        var render = session.Render();

        Assert.Equal(["", "", "you> one"], MessageRows(render.Frame, session));
        Assert.Same(render.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void AutoRenderFailureDoesNotAdvanceLastRenderResultOrPreviousFrame()
    {
        var surface = new ThrowingRenderSurface(width: 20, height: 4) { ThrowOnApply = false };
        var session = new TuiTranscriptSession(surface);
        session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        var initial = session.Start();
        surface.ThrowOnApply = true;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            session.AppendMessage(new TuiMessage(TuiMessageRole.Assistant, "two")));

        Assert.Equal("apply failed", ex.Message);
        Assert.Same(initial, session.LastRenderResult);
        Assert.Same(initial.Frame, session.Host.PreviousFrame);
    }

    [Fact]
    public void Start_HidesCursorAndStopShowsCursor()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface);

        var started = session.Start();

        Assert.False(session.Host.CursorVisible);
        Assert.False(started.CursorVisible);

        session.Stop();
        Assert.True(session.Host.CursorVisible);

        var restarted = session.Start();

        Assert.False(session.Host.CursorVisible);
        Assert.False(restarted.CursorVisible);
    }

    [Fact]
    public void OpenOverlay_WhenStartedAutoRendersFocusedOverlayOverTranscript()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface);
        session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        session.Start();
        surface.Clear();

        var handle = session.OpenOverlay(
            new StaticComponent("overlay"),
            new TuiTranscriptOverlayOptions(Width: 8, Row: 1, Column: 6));

        Assert.True(handle.IsFocused);
        Assert.Single(surface.Diffs);
        Assert.NotNull(session.LastRenderResult);
        Assert.True(session.LastRenderResult.HasVisibleOverlay);
        Assert.Equal(Padded("      overlay", 20), session.LastRenderResult.Frame.Lines[1]);
        Assert.False(session.LastRenderResult.CursorVisible);
    }

    [Fact]
    public void CloseOverlay_WhenStartedAutoRendersBaseTranscript()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface);
        session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        session.Start();
        var handle = session.OpenOverlay(
            new StaticComponent("overlay"),
            new TuiTranscriptOverlayOptions(Width: 8, Row: 1, Column: 6));
        surface.Clear();

        var result = session.CloseOverlay(handle);

        Assert.NotNull(result);
        Assert.False(result.HasVisibleOverlay);
        Assert.True(handle.IsClosed);
        Assert.Equal(["", "", "you> one"], MessageRows(result.Frame, session));
        var operation = Assert.Single(result.Diff.Operations);
        Assert.Equal(1, operation.Row);
        Assert.Equal(new string(' ', 20), operation.Text);
    }

    [Fact]
    public void OpenOverlay_WhenStoppedDefersRenderUntilStart()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var session = new TuiTranscriptSession(surface);
        session.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));

        var handle = session.OpenOverlay(
            new StaticComponent("overlay"),
            new TuiTranscriptOverlayOptions(Width: 8, Row: 1, Column: 6));

        Assert.True(handle.IsFocused);
        Assert.Empty(surface.Diffs);
        Assert.Null(session.LastRenderResult);

        var started = session.Start();

        Assert.True(started.HasVisibleOverlay);
        Assert.Equal(Padded("      overlay", 20), started.Frame.Lines[1]);
    }

    private static TuiTranscriptSession StartedSession(
        ITuiRenderSurface surface,
        IConsoleKeyReader? keyReader = null)
    {
        var session = new TuiTranscriptSession(surface, keyReader);
        session.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
                new TuiMessage(TuiMessageRole.System, "four"),
                new TuiMessage(TuiMessageRole.Error, "five"),
            ]);
        session.Start();
        return session;
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);

    private static IReadOnlyList<string> MessageRows(TuiRenderFrame frame, TuiTranscriptSession session) =>
        frame.Lines.Take(session.Viewport.MessageHeight).Select(row => row.TrimEnd()).ToArray();

    private static string Padded(string value, int width) =>
        TuiText.TruncateToWidth(value, width, string.Empty, pad: true);

    private sealed class MemoryRenderSurface(int width, int height) : ITuiRenderSurface
    {
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public List<TuiRenderDiff> Diffs { get; } = [];

        public void Apply(TuiRenderDiff diff) => Diffs.Add(diff);

        public void Clear() => Diffs.Clear();
    }

    private sealed class ThrowingRenderSurface(int width, int height) : ITuiRenderSurface
    {
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public bool ThrowOnApply { get; set; } = true;
        public List<TuiRenderDiff> Diffs { get; } = [];

        public void Apply(TuiRenderDiff diff)
        {
            if (ThrowOnApply)
            {
                throw new InvalidOperationException("apply failed");
            }

            Diffs.Add(diff);
        }
    }

    private sealed class StaticComponent(params string[] lines) : ITuiComponent
    {
        public IReadOnlyList<string> Render(int width) =>
            lines.Select(line => TuiText.TruncateToWidth(line, width, string.Empty, pad: true)).ToArray();

        public void Invalidate()
        {
        }
    }

    private sealed class ScriptedKeyReader : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();

        public void Enqueue(ConsoleKeyInfo key) => _keys.Enqueue(key);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_keys.Dequeue());
    }
}
