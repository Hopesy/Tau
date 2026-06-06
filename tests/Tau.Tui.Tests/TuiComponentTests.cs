using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiComponentTests
{
    [Fact]
    public void Text_VisibleWidthCountsCjkAsTwoColumnsAndStripsAnsi()
    {
        Assert.Equal(4, TuiText.VisibleWidth("a你b"));
        Assert.Equal(3, TuiText.VisibleWidth("\u001b[31mred\u001b[0m"));
    }

    [Fact]
    public void Text_TruncateToWidthAddsEllipsisWithinBudget()
    {
        Assert.Equal("ab...", TuiText.TruncateToWidth("abcdef", 5));
        Assert.Equal(5, TuiText.VisibleWidth(TuiText.TruncateToWidth("abcdef", 5)));
    }

    [Fact]
    public void TextBlock_WrapsAndPadsRenderedLines()
    {
        var block = new TuiTextBlock("alpha beta gamma", paddingX: 1, paddingY: 0);

        var lines = block.Render(10);

        Assert.Equal([" alpha    ", " beta     ", " gamma    "], lines);
        Assert.All(lines, line => Assert.Equal(10, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void TextBlock_AppliesBackgroundFormatterAndInvalidatesCache()
    {
        var block = new TuiTextBlock(
            "body",
            paddingX: 1,
            paddingY: 1,
            backgroundFormatter: static value => $"\u001b[41m{value}\u001b[0m");

        var first = block.Render(8);
        var second = block.Render(8);

        Assert.Same(first, second);
        Assert.Equal(3, first.Count);
        Assert.All(first, line =>
        {
            Assert.StartsWith("\u001b[41m", line, StringComparison.Ordinal);
            Assert.Equal(8, TuiText.VisibleWidth(line));
        });

        block.SetCustomBackgroundFormatter(static value => $"\u001b[42m{value}\u001b[0m");

        var updated = block.Render(8);

        Assert.NotSame(first, updated);
        Assert.All(updated, line => Assert.StartsWith("\u001b[42m", line, StringComparison.Ordinal));
        Assert.Contains("body", updated[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedText_RendersFirstLineWithPaddingAndEllipsis()
    {
        var truncated = new TuiTruncatedText("abcdef\nsecond", paddingX: 1, paddingY: 1);

        var lines = truncated.Render(6);

        Assert.Equal(["      ", " a... ", "      "], lines);
        Assert.All(lines, line => Assert.Equal(6, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void TruncatedText_EmptyTextStillRendersPaddedLine()
    {
        var truncated = new TuiTruncatedText(string.Empty, paddingX: 1, paddingY: 1);

        var lines = truncated.Render(4);

        Assert.Equal(["    ", "    ", "    "], lines);
    }

    [Fact]
    public void Container_RendersChildrenInOrder()
    {
        var root = new TuiContainer();
        root.Add(new TuiTextBlock("one", paddingX: 0, paddingY: 0, wrap: false));
        root.Add(new TuiTextBlock("two", paddingX: 0, paddingY: 0, wrap: false));

        Assert.Equal(["one       ", "two       "], root.Render(10));
    }

    [Fact]
    public void Spacer_RendersConfigurableEmptyLines()
    {
        var spacer = new TuiSpacer();

        Assert.Equal([string.Empty], spacer.Render(80));

        spacer.SetLines(3);

        Assert.Equal([string.Empty, string.Empty, string.Empty], spacer.Render(1));
        Assert.Equal(3, spacer.Lines);
    }

    [Fact]
    public void Spacer_NegativeLinesRenderNothing()
    {
        var spacer = new TuiSpacer(lines: -1);

        Assert.Empty(spacer.Render(80));
    }

    [Fact]
    public void Container_RendersSpacerBetweenChildren()
    {
        var root = new TuiContainer();
        root.Add(new TuiTextBlock("one", paddingX: 0, paddingY: 0, wrap: false));
        root.Add(new TuiSpacer(lines: 2));
        root.Add(new TuiTextBlock("two", paddingX: 0, paddingY: 0, wrap: false));

        Assert.Equal(["one       ", string.Empty, string.Empty, "two       "], root.Render(10));
    }

    [Fact]
    public void Box_AppliesPaddingAroundChildren()
    {
        var box = new TuiBox(paddingX: 2, paddingY: 1);
        box.Add(new TuiTextBlock("body", paddingX: 0, paddingY: 0, wrap: false));

        Assert.Equal(["        ", "  body  ", "        "], box.Render(8));
    }

    [Fact]
    public void Box_AppliesBackgroundFormatterAndDetectsFormatterChanges()
    {
        var box = new TuiBox(
            paddingX: 1,
            paddingY: 1,
            backgroundFormatter: static value => $"\u001b[44m{value}\u001b[0m");
        box.Add(new TuiTextBlock("body", paddingX: 0, paddingY: 0, wrap: false));

        var first = box.Render(8);
        var second = box.Render(8);

        Assert.Same(first, second);
        Assert.Equal(3, first.Count);
        Assert.All(first, line =>
        {
            Assert.StartsWith("\u001b[44m", line, StringComparison.Ordinal);
            Assert.Equal(8, TuiText.VisibleWidth(line));
        });
        Assert.Contains("body", first[1], StringComparison.Ordinal);

        box.SetBackgroundFormatter(static value => $"\u001b[45m{value}\u001b[0m");

        var updated = box.Render(8);

        Assert.NotSame(first, updated);
        Assert.All(updated, line => Assert.StartsWith("\u001b[45m", line, StringComparison.Ordinal));
        Assert.Contains("body", updated[1], StringComparison.Ordinal);
    }

    [Fact]
    public void SelectList_RendersDescriptionsInAlignedColumn()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("short", "short", "short description"),
                new TuiSelectItem("very-long-command-name-that-needs-truncation", "very-long-command-name-that-needs-truncation", "long description"),
            ],
            maxVisible: 5);

        var lines = list.Render(80);

        Assert.Equal(VisibleIndexOf(lines[0], "short description"), VisibleIndexOf(lines[1], "long description"));
        Assert.StartsWith("> short", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SelectList_FilterResetsSelectionAndShowsNoMatch()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("alpha", "alpha"),
                new TuiSelectItem("beta", "beta"),
            ]);
        list.SetSelectedIndex(1);

        list.SetFilter("alp");

        Assert.Equal("alpha", list.SelectedItem?.Value);

        list.SetFilter("zzz");
        Assert.Empty(list.FilteredItems);
        Assert.Contains("No matching items", list.Render(40)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SelectList_FilterUsesFuzzySortingAndEmptyFilterRestoresOriginalOrder()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("a_p_p", "a_p_p"),
                new TuiSelectItem("app", "app"),
                new TuiSelectItem("application", "application"),
            ]);

        list.SetFilter("app");

        Assert.Equal(["app", "application", "a_p_p"], list.FilteredItems.Select(static item => item.Value));

        list.SetFilter(string.Empty);

        Assert.Equal(["a_p_p", "app", "application"], list.FilteredItems.Select(static item => item.Value));
    }

    [Fact]
    public void SelectList_RendersFooterHintAfterItemsAndScrollInfo()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("one", "one"),
                new TuiSelectItem("two", "two"),
                new TuiSelectItem("three", "three"),
            ],
            maxVisible: 1,
            layout: new TuiSelectListLayout(FooterHint: "footer hint text"));

        var lines = list.Render(8);

        Assert.Equal(3, lines.Count);
        Assert.Contains("(1/3)", lines[^2], StringComparison.Ordinal);
        Assert.Equal("footer h", lines[^1]);
    }

    [Fact]
    public void SelectList_RendersFooterHintWhenFilterHasNoMatches()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("alpha", "alpha"),
                new TuiSelectItem("beta", "beta"),
            ],
            layout: new TuiSelectListLayout(FooterHint: "press escape to cancel"));

        list.SetFilter("zzz");
        var lines = list.Render(40);

        Assert.Equal(2, lines.Count);
        Assert.Contains("No matching items", lines[0], StringComparison.Ordinal);
        Assert.Equal("press escape to cancel", lines[^1]);
    }

    [Fact]
    public void SelectList_HandleInputMovesSelectsAndCancels()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("one", "one"),
                new TuiSelectItem("two", "two"),
                new TuiSelectItem("three", "three"),
            ]);
        TuiSelectItem? selected = null;
        var cancelled = false;
        list.Selected += item => selected = item;
        list.Cancelled += () => cancelled = true;

        Assert.True(list.HandleInput(Key(ConsoleKey.DownArrow)).Consumed);
        Assert.Equal("two", list.SelectedItem?.Value);
        Assert.True(list.HandleInput(new ConsoleKeyInfo('k', ConsoleKey.K, shift: false, alt: false, control: false)).Consumed);
        Assert.Equal("one", list.SelectedItem?.Value);
        Assert.True(list.HandleInput(Key(ConsoleKey.Enter)).Consumed);
        Assert.Equal("one", selected?.Value);
        Assert.True(list.HandleInput(Key(ConsoleKey.Escape)).Consumed);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task MultiSelectSession_TogglesAndSavesExplicitSelection()
    {
        var list = new TuiMultiSelectList(
            [
                new TuiMultiSelectItem("one", "one", "first", "alpha"),
                new TuiMultiSelectItem("two", "two", "second", "beta"),
            ]);
        var keyReader = new ScriptedKeyReader(
            Key(ConsoleKey.DownArrow),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.S, control: true));
        var surface = new CapturingRenderSurface(width: 80, height: 10);
        var session = new TuiMultiSelectSession(list, keyReader, surface);

        var result = await session.RunAsync();

        Assert.True(result.HasSelection);
        Assert.False(result.IsCancelled);
        Assert.Equal(["two"], result.SelectedValues);
        Assert.True(surface.Diffs[0].RequiresFullRedraw);
        Assert.Contains("[x] one", surface.Diffs[0].Operations[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiSelectList_FiltersTogglesProviderAndReordersSelection()
    {
        var list = new TuiMultiSelectList(
            [
                new TuiMultiSelectItem("openai/a", "openai/a", "A", "openai"),
                new TuiMultiSelectItem("openai/b", "openai/b", "B", "openai"),
                new TuiMultiSelectItem("google/c", "google/c", "C", "google"),
            ],
            selectedValues: ["openai/a", "google/c"]);
        IReadOnlyList<string>? changed = null;
        list.SelectionChanged += values => changed = values;

        list.SetSelectedValue("google/c");
        Assert.True(list.HandleInput(Key(ConsoleKey.UpArrow, alt: true)).Consumed);
        Assert.Equal(["google/c", "openai/a"], list.SelectedValues);

        list.SetSelectedValue("openai/a");
        Assert.True(list.HandleInput(Key(ConsoleKey.P, control: true)).Consumed);
        Assert.Null(list.SelectedValues);

        list.HandleInput(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        Assert.Equal("g", list.Filter);
        Assert.Single(list.FilteredItems);
        Assert.Equal("google/c", list.SelectedItem?.Value);

        list.HandleInput(Key(ConsoleKey.X, control: true));
        Assert.Equal(["openai/a", "openai/b"], list.SelectedValues);
        Assert.Equal(changed, list.SelectedValues);
    }

    [Fact]
    public void MultiSelectList_FilterUsesFuzzySortingAndEmptyFilterKeepsDisplayOrder()
    {
        var list = new TuiMultiSelectList(
            [
                new TuiMultiSelectItem("a_p_p", "a_p_p"),
                new TuiMultiSelectItem("app", "app"),
                new TuiMultiSelectItem("application", "application"),
            ],
            selectedValues: ["a_p_p"]);

        Assert.Equal(["a_p_p", "app", "application"], list.FilteredItems.Select(static item => item.Value));

        list.SetFilter("app");

        Assert.Equal(["app", "application", "a_p_p"], list.FilteredItems.Select(static item => item.Value));

        list.SetFilter(string.Empty);

        Assert.Equal(["a_p_p", "app", "application"], list.FilteredItems.Select(static item => item.Value));
    }

    [Fact]
    public async Task MultiSelectSession_ReturnsCancelledOnEscape()
    {
        var list = new TuiMultiSelectList(
            [
                new TuiMultiSelectItem("one", "one"),
                new TuiMultiSelectItem("two", "two"),
            ]);
        var keyReader = new ScriptedKeyReader(Key(ConsoleKey.Escape));
        var surface = new CapturingRenderSurface(width: 40, height: 10);
        var session = new TuiMultiSelectSession(list, keyReader, surface);

        var result = await session.RunAsync();

        Assert.True(result.IsCancelled);
        Assert.False(result.HasSelection);
        Assert.Null(result.SelectedValues);
    }

    [Fact]
    public void DiffRenderer_OnlyReturnsChangedLinesAfterInitialFrame()
    {
        var previous = new TuiRenderFrame(20, 10, ["header", "old", "footer"]);
        var next = new TuiRenderFrame(20, 10, ["header", "new", "footer"]);

        var diff = TuiDiffRenderer.Diff(previous, next);

        Assert.False(diff.RequiresFullRedraw);
        var operation = Assert.Single(diff.Operations);
        Assert.Equal(TuiRenderOperationKind.ReplaceLine, operation.Kind);
        Assert.Equal(1, operation.Row);
        Assert.Equal("new", operation.Text);
    }

    [Fact]
    public void DiffRenderer_ClearsRemovedLines()
    {
        var previous = new TuiRenderFrame(20, 10, ["header", "stale"]);
        var next = new TuiRenderFrame(20, 10, ["header"]);

        var diff = TuiDiffRenderer.Diff(previous, next);

        var operation = Assert.Single(diff.Operations);
        Assert.Equal(TuiRenderOperationKind.ClearLine, operation.Kind);
        Assert.Equal(1, operation.Row);
    }

    [Fact]
    public void DiffRenderer_ForcesFullRedrawWhenWidthChanges()
    {
        var previous = new TuiRenderFrame(20, 10, ["old"]);
        var next = new TuiRenderFrame(40, 10, ["new"]);

        var diff = TuiDiffRenderer.Diff(previous, next);

        Assert.True(diff.RequiresFullRedraw);
        Assert.Equal("width changed", diff.Reason);
        Assert.Equal("new", Assert.Single(diff.Operations).Text);
    }

    [Fact]
    public async Task SelectorSession_RendersInitialFrameAndReturnsSelectedItem()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("one", "one"),
                new TuiSelectItem("two", "two"),
            ]);
        var keyReader = new ScriptedKeyReader(
            Key(ConsoleKey.DownArrow),
            Key(ConsoleKey.Enter));
        var surface = new CapturingRenderSurface(width: 40, height: 10);
        var session = new TuiSelectorSession(list, keyReader, surface);

        var result = await session.RunAsync();

        Assert.True(result.HasSelection);
        Assert.False(result.IsCancelled);
        Assert.Equal("two", result.SelectedItem?.Value);
        Assert.Equal(2, surface.Diffs.Count);
        Assert.True(surface.Diffs[0].RequiresFullRedraw);
        Assert.StartsWith("> one", surface.Diffs[0].Operations[0].Text, StringComparison.Ordinal);
        Assert.False(surface.Diffs[1].RequiresFullRedraw);
        Assert.Collection(
            surface.Diffs[1].Operations,
            op => Assert.StartsWith("  one", op.Text, StringComparison.Ordinal),
            op => Assert.StartsWith("> two", op.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectorSession_ReturnsCancelledOnEscape()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("one", "one"),
                new TuiSelectItem("two", "two"),
            ]);
        var keyReader = new ScriptedKeyReader(Key(ConsoleKey.Escape));
        var surface = new CapturingRenderSurface(width: 40, height: 10);
        var session = new TuiSelectorSession(list, keyReader, surface);

        var result = await session.RunAsync();

        Assert.True(result.IsCancelled);
        Assert.False(result.HasSelection);
        Assert.Null(result.SelectedItem);
        Assert.Single(surface.Diffs);
        Assert.True(surface.Diffs[0].RequiresFullRedraw);
    }

    [Fact]
    public void OverlayHost_UsesFullRedrawWhenSurfaceDimensionsChange()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("one", "one"),
                new TuiSelectItem("two", "two"),
            ]);
        var keyReader = new ScriptedKeyReader();
        var surface = new CapturingRenderSurface(width: 40, height: 10);
        var host = new TuiOverlayHost(list, keyReader, surface);

        host.Render();
        surface.Width = 50;
        var resized = host.Render();

        Assert.Equal(2, surface.Diffs.Count);
        Assert.True(resized.RequiresFullRedraw);
        Assert.Equal("width changed", resized.Reason);
    }

    [Fact]
    public async Task OverlayHost_DoesNotRenderAfterIgnoredInput()
    {
        var list = new TuiSelectList(
            [
                new TuiSelectItem("one", "one"),
                new TuiSelectItem("two", "two"),
            ]);
        var keyReader = new ScriptedKeyReader(new ConsoleKeyInfo('x', ConsoleKey.X, shift: false, alt: false, control: false));
        var surface = new CapturingRenderSurface(width: 40, height: 10);
        var host = new TuiOverlayHost(list, keyReader, surface);

        host.Render();
        var result = await host.ReadInputAsync();

        Assert.False(result.Consumed);
        Assert.Single(surface.Diffs);
    }

    private static int VisibleIndexOf(string line, string value)
    {
        var index = line.IndexOf(value, StringComparison.Ordinal);
        Assert.NotEqual(-1, index);
        return TuiText.VisibleWidth(line[..index]);
    }

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char keyChar = '\0',
        bool alt = false,
        bool control = false) =>
        new(keyChar, key, shift: false, alt: alt, control: control);

    private sealed class ScriptedKeyReader(params ConsoleKeyInfo[] keys) : Tau.Tui.Abstractions.IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_keys.Dequeue());
        }
    }

    private sealed class CapturingRenderSurface(int width, int height) : ITuiRenderSurface
    {
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public List<TuiRenderDiff> Diffs { get; } = [];

        public void Apply(TuiRenderDiff diff) => Diffs.Add(diff);
    }
}
