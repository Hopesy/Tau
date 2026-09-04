using System.Collections.Concurrent;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiTranscriptViewportHostTests
{
    [Fact]
    public void Render_FirstFrameAppliesFullViewportFrame()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface, statusLeft: "ready", statusRight: "gpt");

        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        var result = host.Render();

        Assert.True(result.Diff.RequiresFullRedraw);
        Assert.Equal("first render", result.Diff.Reason);
        Assert.Equal(4, result.Frame.Lines.Count);
        Assert.Equal(["", "", "you> one"], MessageRows(result.Frame, host));
        Assert.Equal("ready            gpt", result.Frame.Lines[^1]);
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void ReservedBottomLinesKeepTranscriptAboveInputOverlay()
    {
        var surface = new MemoryRenderSurface(width: 24, height: 8);
        var host = new TuiTranscriptViewportHost(
            surface,
            [new TuiMessage(TuiMessageRole.System, "line one"), new TuiMessage(TuiMessageRole.System, "line two")],
            statusLeft: "cwd",
            statusRight: "model",
            displayOptions: TuiMessageDisplayOptions.Agent);

        host.SetReservedBottomLines(3);
        var result = host.Render();

        Assert.Equal(3, host.Viewport.ReservedBottomLines);
        Assert.Equal(4, host.Viewport.TranscriptHeight);
        Assert.Equal(7, host.Viewport.MessageHeight);
        Assert.Contains("line one", result.Frame.Lines[0], StringComparison.Ordinal);
        Assert.Contains("line two", result.Frame.Lines[1], StringComparison.Ordinal);
        Assert.All(result.Frame.Lines.Skip(2).Take(5), line => Assert.Equal(new string(' ', 24), line));
        Assert.StartsWith("cwd", result.Frame.Lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("model", result.Frame.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AppendMessage_WhileFollowingBottomAppliesOnlyChangedViewportRows()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface, statusLeft: "ready", statusRight: "gpt");
        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        host.Render();
        surface.Clear();

        host.AppendMessage(new TuiMessage(TuiMessageRole.Assistant, "two"));
        var result = host.Render();

        Assert.False(result.Diff.RequiresFullRedraw);
        Assert.Equal("line diff", result.Diff.Reason);
        Assert.Equal(["", "you> one", "tau> two"], MessageRows(result.Frame, host));
        Assert.Collection(
            result.Diff.Operations,
            operation =>
            {
                Assert.Equal(TuiRenderOperationKind.ReplaceLine, operation.Kind);
                Assert.Equal(1, operation.Row);
                Assert.Equal("you> one", operation.Text.TrimEnd());
            },
            operation =>
            {
                Assert.Equal(TuiRenderOperationKind.ReplaceLine, operation.Kind);
                Assert.Equal(2, operation.Row);
                Assert.Equal("tau> two", operation.Text.TrimEnd());
            });
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void AppendMessage_WhileScrolledUpKeepsVisibleViewportStable()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var host = new TuiTranscriptViewportHost(surface);
        host.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
            ]);
        host.Render();
        host.ScrollLine(delta: 1);
        var scrolled = host.Render();
        surface.Clear();

        host.AppendMessage(new TuiMessage(TuiMessageRole.System, "four"));
        var result = host.Render();

        Assert.False(host.Viewport.IsFollowingBottom);
        Assert.Equal(["you> one", "tau> two"], MessageRows(scrolled.Frame, host));
        Assert.Equal(["you> one", "tau> two"], MessageRows(result.Frame, host));
        Assert.False(result.Diff.RequiresFullRedraw);
        Assert.Empty(result.Diff.Operations);
        Assert.Same(TuiRenderDiff.Empty, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void ScrollTop_JumpsToOldestVisibleTranscriptRows()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var host = new TuiTranscriptViewportHost(surface);
        host.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
                new TuiMessage(TuiMessageRole.System, "four"),
                new TuiMessage(TuiMessageRole.Error, "five"),
            ]);
        host.Render();
        surface.Clear();

        host.ScrollTop();
        var result = host.Render();

        Assert.False(host.Viewport.IsFollowingBottom);
        Assert.Equal(3, host.Viewport.ScrollOffsetFromBottom);
        Assert.Equal(["you> one", "tau> two"], MessageRows(result.Frame, host));
        Assert.False(result.Diff.RequiresFullRedraw);
        Assert.Equal("line diff", result.Diff.Reason);
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void SetStatus_RecomposesOnlyStatusRow()
    {
        var surface = new MemoryRenderSurface(width: 16, height: 3);
        var host = new TuiTranscriptViewportHost(surface, statusLeft: "ready", statusRight: "gpt");
        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        host.Render();
        surface.Clear();

        host.SetStatus("busy", "haiku");
        var result = host.Render();

        var operation = Assert.Single(result.Diff.Operations);
        Assert.False(result.Diff.RequiresFullRedraw);
        Assert.Equal(TuiRenderOperationKind.ReplaceLine, operation.Kind);
        Assert.Equal(2, operation.Row);
        Assert.Equal("busy       haiku", operation.Text);
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void Render_WhenSurfaceWidthChangesRewrapsAndForcesFullRedraw()
    {
        var surface = new MemoryRenderSurface(width: 24, height: 4);
        var host = new TuiTranscriptViewportHost(surface);
        host.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "alpha beta gamma"),
                new TuiMessage(TuiMessageRole.Assistant, "delta epsilon zeta"),
            ]);
        host.Render();
        surface.Clear();

        surface.Width = 12;
        var result = host.Render();

        Assert.True(result.Diff.RequiresFullRedraw);
        Assert.Equal("width changed", result.Diff.Reason);
        Assert.Equal(12, result.Frame.Width);
        Assert.Equal(4, result.Frame.Height);
        Assert.All(result.Frame.Lines, line => Assert.Equal(12, TuiText.VisibleWidth(line)));
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void Render_WhenSurfaceHeightChangesKeepsStatusLastAndForcesFullRedraw()
    {
        var surface = new MemoryRenderSurface(width: 18, height: 3);
        var host = new TuiTranscriptViewportHost(surface, statusLeft: "ready");
        host.AppendMessages(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
            ]);
        host.Render();
        surface.Clear();

        surface.Height = 5;
        var result = host.Render();

        Assert.True(result.Diff.RequiresFullRedraw);
        Assert.Equal("height changed", result.Diff.Reason);
        Assert.Equal(5, result.Frame.Lines.Count);
        Assert.Equal("ready             ", result.Frame.Lines[^1]);
        Assert.Equal(["", "you> one", "tau> two", "tool> three"], MessageRows(result.Frame, host));
    }

    [Fact]
    public void Render_WhenSurfaceShrinksClampsReservedInputRowsBeforeComposingFrame()
    {
        var surface = new MemoryRenderSurface(width: 24, height: 10);
        var host = new TuiTranscriptViewportHost(
            surface,
            statusLeft: "cwd",
            statusRight: "model");

        host.SetReservedBottomLines(6);
        host.Render();
        surface.Clear();

        surface.Height = 4;
        var result = host.Render();

        Assert.Equal(3, host.Viewport.MessageHeight);
        Assert.Equal(3, host.Viewport.ReservedBottomLines);
        Assert.Equal(0, host.Viewport.TranscriptHeight);
        Assert.Equal(4, result.Frame.Lines.Count);
        Assert.All(result.Frame.Lines, line => Assert.Equal(24, TuiText.VisibleWidth(line)));
        Assert.StartsWith("cwd", result.Frame.Lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("model", result.Frame.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WhenForcedAppliesFullRedrawWithoutResettingMessages()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface, statusLeft: "ready");
        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        host.Render();
        surface.Clear();

        var result = host.Render(force: true);

        Assert.True(result.Diff.RequiresFullRedraw);
        Assert.Equal("forced", result.Diff.Reason);
        Assert.Equal(["", "", "you> one"], MessageRows(result.Frame, host));
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void ResetFrame_ForcesNextRenderToActLikeFirstRender()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface);
        host.AppendMessage(new TuiMessage(TuiMessageRole.Assistant, "one"));
        host.Render();
        surface.Clear();

        host.ResetFrame();
        Assert.Null(host.PreviousFrame);

        var result = host.Render();

        Assert.True(result.Diff.RequiresFullRedraw);
        Assert.Equal("first render", result.Diff.Reason);
        Assert.Equal(["", "", "tau> one"], MessageRows(result.Frame, host));
        Assert.Same(result.Frame, host.PreviousFrame);
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void Render_WhenSurfaceApplyFailsDoesNotAdvancePreviousFrame()
    {
        var surface = new ThrowingRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface, statusLeft: "ready");
        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));

        var ex = Assert.Throws<InvalidOperationException>(() => host.Render());

        Assert.Equal("apply failed", ex.Message);
        Assert.Null(host.PreviousFrame);

        surface.ThrowOnApply = false;
        var result = host.Render();

        Assert.True(result.Diff.RequiresFullRedraw);
        Assert.Equal("first render", result.Diff.Reason);
        Assert.Same(result.Frame, host.PreviousFrame);
    }

    [Fact]
    public void Render_WithOverlayComposesOverlayOverBaseTranscriptBeforeDiff()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface, statusLeft: "ready");
        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        host.Render();
        surface.Clear();

        host.OpenOverlay(
            new StaticComponent("overlay"),
            new TuiTranscriptOverlayOptions(Width: 8, Row: 1, Column: 6));
        var result = host.Render();

        Assert.False(result.Diff.RequiresFullRedraw);
        Assert.Equal("line diff", result.Diff.Reason);
        Assert.NotNull(result.BaseFrame);
        Assert.Equal(["", "", "you> one"], MessageRows(result.BaseFrame, host));
        Assert.Equal(["", "      overlay", "you> one"], MessageRows(result.Frame, host));
        Assert.False(result.CursorVisible);
        Assert.True(result.HasVisibleOverlay);
        Assert.Same(result.Frame, host.PreviousFrame);
        var operation = Assert.Single(result.Diff.Operations);
        Assert.Equal(1, operation.Row);
        Assert.Equal(Padded("      overlay", 20), operation.Text);
    }

    [Fact]
    public void Render_OverlayAnchorCentersWithinViewport()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 6);
        var host = new TuiTranscriptViewportHost(surface);
        host.OpenOverlay(
            new StaticComponent("one", "two"),
            new TuiTranscriptOverlayOptions(
                Width: 6,
                Anchor: TuiTranscriptOverlayAnchor.Center));

        var result = host.Render();

        Assert.Equal(Padded("       one", 20), result.Frame.Lines[2]);
        Assert.Equal(Padded("       two", 20), result.Frame.Lines[3]);
        Assert.True(result.HasVisibleOverlay);
        Assert.False(result.CursorVisible);
    }

    [Fact]
    public void Render_OverlayAnchorRespectsMarginAndOffset()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 6);
        var host = new TuiTranscriptViewportHost(surface);
        host.OpenOverlay(
            new StaticComponent("edge"),
            new TuiTranscriptOverlayOptions(
                Width: 8,
                Anchor: TuiTranscriptOverlayAnchor.BottomRight,
                OffsetRow: -1,
                OffsetColumn: -2,
                Margin: TuiTranscriptOverlayMargin.All(1)));

        var result = host.Render();

        Assert.Equal(Padded("         edge", 20), result.Frame.Lines[3]);
    }

    [Fact]
    public void Render_OverlayMinWidthAndMaxHeightClampToAvailableArea()
    {
        var surface = new MemoryRenderSurface(width: 12, height: 5);
        var host = new TuiTranscriptViewportHost(surface);
        host.OpenOverlay(
            new StaticComponent("abcdefghi", "second", "third"),
            new TuiTranscriptOverlayOptions(
                Width: 2,
                MinWidth: 8,
                MaxHeight: 2,
                Anchor: TuiTranscriptOverlayAnchor.TopLeft,
                Margin: TuiTranscriptOverlayMargin.All(1)));

        var result = host.Render();

        Assert.Equal(Padded(" abcdefgh", 12), result.Frame.Lines[1]);
        Assert.Equal(Padded(" second", 12), result.Frame.Lines[2]);
        Assert.DoesNotContain("third", result.Frame.Lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ClosingOverlayDiffsBackToBaseTranscript()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface);
        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        var overlay = host.OpenOverlay(
            new StaticComponent("overlay"),
            new TuiTranscriptOverlayOptions(Width: 8, Row: 1, Column: 6));
        host.Render();
        surface.Clear();

        overlay.Close();
        var result = host.Render();

        Assert.False(host.HasVisibleOverlay);
        Assert.False(result.HasVisibleOverlay);
        Assert.Equal(["", "", "you> one"], MessageRows(result.Frame, host));
        Assert.False(result.Diff.RequiresFullRedraw);
        var operation = Assert.Single(result.Diff.Operations);
        Assert.Equal(1, operation.Row);
        Assert.Equal(new string(' ', 20), operation.Text);
        Assert.Same(result.Diff, Assert.Single(surface.Diffs));
    }

    [Fact]
    public void Render_OverlayFocusOrderControlsVisualStacking()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface);
        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        var first = host.OpenOverlay(
            new StaticComponent("first"),
            new TuiTranscriptOverlayOptions(Width: 6, Row: 1, Column: 4));
        var second = host.OpenOverlay(
            new StaticComponent("second"),
            new TuiTranscriptOverlayOptions(Width: 6, Row: 1, Column: 4));

        var topSecond = host.Render();
        Assert.True(second.IsFocused);

        surface.Clear();

        first.Focus();
        var topFirst = host.Render();

        Assert.Equal(Padded("    second", 20), topSecond.Frame.Lines[1]);
        Assert.True(first.IsFocused);
        Assert.False(second.IsFocused);
        Assert.Equal(Padded("    first", 20), topFirst.Frame.Lines[1]);
        var operation = Assert.Single(topFirst.Diff.Operations);
        Assert.Equal(1, operation.Row);
        Assert.Equal(Padded("    first", 20), operation.Text);
    }

    [Fact]
    public void OverlayHandle_HiddenOverlayIsNotComposedUntilShown()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface);
        host.AppendMessage(new TuiMessage(TuiMessageRole.User, "one"));
        var overlay = host.OpenOverlay(
            new StaticComponent("overlay"),
            new TuiTranscriptOverlayOptions(Width: 8, Row: 1, Column: 6, InitiallyHidden: true));

        var hidden = host.Render();
        surface.Clear();

        overlay.SetHidden(false);
        var shown = host.Render();

        Assert.False(hidden.HasVisibleOverlay);
        Assert.Equal(["", "", "you> one"], MessageRows(hidden.Frame, host));
        Assert.True(shown.HasVisibleOverlay);
        Assert.Equal(Padded("      overlay", 20), shown.Frame.Lines[1]);
        Assert.True(overlay.IsFocused);
    }

    [Fact]
    public void OverlayVisibilityPredicateReevaluatesAgainstSurfaceSize()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface);
        host.OpenOverlay(
            new StaticComponent("wide"),
            new TuiTranscriptOverlayOptions(
                Width: 6,
                Row: 1,
                Column: 2,
                IsVisible: (width, _) => width >= 30));

        var hidden = host.Render();
        surface.Clear();

        surface.Width = 30;
        var shown = host.Render();

        Assert.False(hidden.HasVisibleOverlay);
        Assert.True(shown.HasVisibleOverlay);
        Assert.True(shown.Diff.RequiresFullRedraw);
        Assert.Equal("width changed", shown.Diff.Reason);
        Assert.Equal("  wide                        ", shown.Frame.Lines[1]);
    }

    [Fact]
    public void CursorVisibility_IsTrackedInRenderResult()
    {
        var surface = new MemoryRenderSurface(width: 20, height: 4);
        var host = new TuiTranscriptViewportHost(surface);

        var visible = host.Render();
        host.HideCursor();
        var hidden = host.Render();
        host.ShowCursor();
        var visibleAgain = host.Render();

        Assert.True(visible.CursorVisible);
        Assert.False(hidden.CursorVisible);
        Assert.True(visibleAgain.CursorVisible);
        Assert.True(host.CursorVisible);
    }

    [Fact]
    public void RuntimeError_IsRenderedAboveFocusedInputOverlay()
    {
        var surface = new MemoryRenderSurface(width: 40, height: 8);
        var composition = new TuiCompositionSession(
            surface,
            displayOptions: TuiMessageDisplayOptions.Agent,
            statusTheme: TuiStatusBarTheme.Agent);
        var terminal = new TuiPassiveTerminal();
        var console = new InteractiveConsoleSession(terminal);
        using var binding = composition.BindTranscript(console);
        var renderer = new TuiCompositionInteractiveRenderer(composition);

        renderer.WritePrompt(">> ");
        renderer.Render(string.Empty, 0);
        console.WriteRuntimeError("provider failed");

        var result = composition.LastRenderResult;
        Assert.NotNull(result);
        Assert.Contains(
            result!.BaseFrame!.Lines.Take(composition.Viewport.TranscriptHeight),
            line => line.Contains("provider failed", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Frame.Lines.Skip(composition.Viewport.TranscriptHeight),
            line => line.Contains("provider failed", StringComparison.Ordinal));
        Assert.Contains(result.Frame.Lines, line => line.Contains(">> ", StringComparison.Ordinal));
    }

    [Fact]
    public void CompositionInputRenderer_BatchesAutocompleteAndDraftIntoOneFrame()
    {
        var surface = new MemoryRenderSurface(width: 16, height: 10);
        var session = new TuiCompositionSession(surface);
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        surface.Clear();

        using (renderer.BeginRenderBatch())
        {
            renderer.RenderAutocomplete(
                [new TuiAutocompleteItem("one", "one", "first")],
                selectedIndex: 0);
            renderer.Render("", cursorIndex: 0);
        }

        Assert.Single(surface.Diffs);
        Assert.Equal(4, session.Viewport.ReservedBottomLines);
        Assert.Contains(session.LastRenderResult!.Frame.Lines, line => line.Contains(">> ", StringComparison.Ordinal));
    }

    [Fact]
    public void CompositionInputRenderer_UsesStableInputFrameAndPreservesWidth()
    {
        var surface = new MemoryRenderSurface(width: 30, height: 8);
        var session = new TuiCompositionSession(surface);
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        renderer.Render("hello", cursorIndex: 5);

        var renderResult = session.LastRenderResult;
        Assert.NotNull(renderResult);
        var frame = renderResult!.Frame;
        Assert.Contains(frame.Lines, line => line.Contains("─", StringComparison.Ordinal));
        Assert.Contains(frame.Lines, line => line.Contains(">> hello", StringComparison.Ordinal));
        Assert.DoesNotContain(frame.Lines, line => line.Contains("╭", StringComparison.Ordinal));
        Assert.DoesNotContain(frame.Lines, line => line.Contains("╰", StringComparison.Ordinal));
        Assert.DoesNotContain(frame.Lines, line => line.Contains("│", StringComparison.Ordinal));
        Assert.All(frame.Lines, line => Assert.Equal(30, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void CompositionInputRenderer_RebuildsOverlayWithOneTerminalFrame()
    {
        var surface = new MemoryRenderSurface(width: 12, height: 10);
        var session = new TuiCompositionSession(surface);
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        surface.Clear();

        renderer.Render("123456789", cursorIndex: 9);

        Assert.Single(surface.Diffs);
        Assert.Equal(4, session.Viewport.ReservedBottomLines);
        Assert.All(session.LastRenderResult!.Frame.Lines, line =>
            Assert.Equal(12, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void CompositionInputRenderer_ClearingMultilineDraftRemovesStaleSeparatorRows()
    {
        var surface = new MemoryRenderSurface(width: 12, height: 10);
        var session = new TuiCompositionSession(surface);
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        renderer.Render(new string('x', 80), cursorIndex: 80);
        surface.Clear();

        renderer.Render(string.Empty, cursorIndex: 0);

        Assert.Single(surface.Diffs);
        Assert.Equal(3, session.Viewport.ReservedBottomLines);
        var frame = session.LastRenderResult!.Frame;
        Assert.Equal(2, frame.Lines.Count(line => line.Contains("\u001b[90m", StringComparison.Ordinal)));
        Assert.DoesNotContain(frame.Lines, line => line.Contains(new string('x', 20), StringComparison.Ordinal));
        Assert.All(frame.Lines, line => Assert.Equal(12, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void CompositionInputRenderer_ClampsInputFrameForVeryNarrowTerminals()
    {
        foreach (var width in new[] { 1, 2, 3, 4 })
        {
            var surface = new MemoryRenderSurface(width, height: 6);
            var session = new TuiCompositionSession(surface);
            var renderer = new TuiCompositionInteractiveRenderer(session);

            renderer.WritePrompt(">> ");
            renderer.Render("x", cursorIndex: 1);

            var result = session.LastRenderResult;
            Assert.NotNull(result);
            Assert.All(result!.Frame.Lines, line => Assert.Equal(width, TuiText.VisibleWidth(line)));
        }
    }

    [Fact]
    public void CompositionInputRenderer_UsesHorizontalSeparatorsForMultilineInputAndKeepsCursorVisible()
    {
        var surface = new MemoryRenderSurface(width: 12, height: 10);
        var session = new TuiCompositionSession(surface);
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        renderer.Render("123456789", cursorIndex: 9);

        var frame = session.LastRenderResult!.Frame;
        var separatorIndexes = frame.Lines
            .Select((line, index) => (line, index))
            .Where(item => item.line.Contains("─", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();

        Assert.Equal(2, separatorIndexes.Length);
        Assert.Equal(3, separatorIndexes[1] - separatorIndexes[0]);
        Assert.Contains(frame.Lines, line => line.Contains("\u001b[7m", StringComparison.Ordinal));
        Assert.DoesNotContain(frame.Lines, line => line.Contains("╭", StringComparison.Ordinal));
        Assert.DoesNotContain(frame.Lines, line => line.Contains("╰", StringComparison.Ordinal));
        Assert.DoesNotContain(frame.Lines, line => line.Contains("│", StringComparison.Ordinal));
        Assert.All(frame.Lines, line => Assert.Equal(12, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void CompositionInputRenderer_UsesCadeFiveLineInputLimit()
    {
        var surface = new MemoryRenderSurface(width: 8, height: 16);
        var session = new TuiCompositionSession(surface);
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        renderer.Render(new string('x', 80), cursorIndex: 80);

        var frame = session.LastRenderResult!.Frame;
        var separatorCount = frame.Lines.Count(line => line.Contains("─", StringComparison.Ordinal));

        Assert.Equal(2, separatorCount);
        Assert.Equal(7, session.Viewport.ReservedBottomLines);
        Assert.All(frame.Lines, line => Assert.Equal(8, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void CompositionInputRenderer_RendersCadeStyleAutocompleteBelowInput()
    {
        var surface = new MemoryRenderSurface(width: 42, height: 12);
        var session = new TuiCompositionSession(surface);
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        renderer.RenderAutocomplete(
            [
                new TuiAutocompleteItem("help", "help", "show available commands"),
                new TuiAutocompleteItem("model", "model", "select a model")
            ],
            selectedIndex: 1);
        renderer.Render("/", cursorIndex: 1);

        var frame = session.LastRenderResult!.Frame;
        Assert.Contains(frame.Lines, line => line.Contains(">", StringComparison.Ordinal) && line.Contains("model", StringComparison.Ordinal));
        Assert.Contains(frame.Lines, line => line.Contains("help", StringComparison.Ordinal));
        Assert.Contains(frame.Lines, line => line.Contains("select a model", StringComparison.Ordinal));
        Assert.Equal(5, session.Viewport.ReservedBottomLines);
        Assert.All(frame.Lines, line => Assert.Equal(42, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public async Task CompositionInputRenderer_SerializesConcurrentStateUpdates()
    {
        var surface = new MemoryRenderSurface(width: 40, height: 12);
        var session = new TuiCompositionSession(surface);
        var renderer = new TuiCompositionInteractiveRenderer(session);
        var failures = new ConcurrentQueue<Exception>();

        renderer.WritePrompt(">> ");
        var tasks = Enumerable.Range(0, 80)
            .Select(index => Task.Run(() =>
            {
                try
                {
                    renderer.Render($"draft-{index}-中文", cursorIndex: index % 12);
                    session.Render();
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(failures);
        Assert.All(session.LastRenderResult!.Frame.Lines, line =>
            Assert.Equal(40, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void CompositionInputRenderer_DoesNotCoverStatusRowsInShortTerminal()
    {
        var surface = new MemoryRenderSurface(width: 24, height: 4);
        var session = new TuiCompositionSession(surface);
        session.SetStatusLines(
            [
                new TuiStatusBarLine("cwd", string.Empty),
                new TuiStatusBarLine("tokens", "model"),
                new TuiStatusBarLine("extension", string.Empty)
            ]);
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        renderer.Render("hello", cursorIndex: 5);

        var result = session.LastRenderResult;
        Assert.NotNull(result);
        Assert.Contains("hello", result!.Frame.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("cwd", result.Frame.Lines[1], StringComparison.Ordinal);
        Assert.StartsWith("tokens", result.Frame.Lines[2], StringComparison.Ordinal);
        Assert.StartsWith("extension", result.Frame.Lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionInputRenderer_ReservesBottomRowsAndReleasesThemAfterCommit()
    {
        var surface = new MemoryRenderSurface(width: 32, height: 8);
        var session = new TuiCompositionSession(
            surface,
            messages: Enumerable.Range(1, 7)
                .Select(index => new TuiMessage(TuiMessageRole.System, $"event {index}")),
            statusLeft: "cwd",
            statusRight: "model");
        var renderer = new TuiCompositionInteractiveRenderer(session);

        renderer.WritePrompt(">> ");
        renderer.Render("draft", cursorIndex: 5);

        var editing = session.LastRenderResult;
        Assert.NotNull(editing);
        Assert.Equal(3, session.Viewport.ReservedBottomLines);
        Assert.Equal(4, session.Viewport.TranscriptHeight);
        Assert.Contains("event 7", editing!.BaseFrame!.Lines[3], StringComparison.Ordinal);
        Assert.Contains("─", editing.Frame.Lines[4], StringComparison.Ordinal);
        Assert.DoesNotContain("╭", editing.Frame.Lines[4], StringComparison.Ordinal);
        Assert.Contains("draft", editing.Frame.Lines[5], StringComparison.Ordinal);
        Assert.StartsWith("cwd", editing.Frame.Lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("model", editing.Frame.Lines[^1], StringComparison.Ordinal);

        renderer.Commit();

        Assert.Equal(0, session.Viewport.ReservedBottomLines);
        Assert.Equal(7, session.Viewport.TranscriptHeight);
        Assert.DoesNotContain("draft", session.LastRenderResult!.Frame.Lines);
        Assert.StartsWith("cwd", session.LastRenderResult.Frame.Lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("model", session.LastRenderResult.Frame.Lines[^1], StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> MessageRows(TuiRenderFrame frame, TuiTranscriptViewportHost host) =>
        frame.Lines.Take(host.Viewport.MessageHeight).Select(row => row.TrimEnd()).ToArray();

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
}
