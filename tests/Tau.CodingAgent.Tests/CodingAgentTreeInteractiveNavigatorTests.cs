using System.Text;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Tests;

public class CodingAgentTreeInteractiveNavigatorTests
{
    [Fact]
    public async Task NavigateAsync_EmptyItems_ReturnsNull()
    {
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(
            Array.Empty<CodingAgentTreeViewItem>(),
            new FakeKeyReader(),
            new StringWriter());

        Assert.Null(result.SelectedEntryId);
        Assert.Equal(0, result.Frames);
    }

    [Fact]
    public async Task NavigateAsync_EnterReturnsLastEntryByDefault()
    {
        var items = MakeItems("a", "b", "c");
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("c", result.SelectedEntryId);
        Assert.Equal(2, result.LastIndex);
    }

    [Fact]
    public async Task NavigateAsync_JKMovesSelectionDownAndUp()
    {
        var items = MakeItems("a", "b", "c");
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.K); // up from index 2 -> 1
        reader.EnqueueKey(ConsoleKey.K); // -> 0
        reader.EnqueueKey(ConsoleKey.J); // back to 1
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("b", result.SelectedEntryId);
        Assert.Equal(1, result.LastIndex);
    }

    [Fact]
    public async Task NavigateAsync_GoesToFirstWithLowerG_AndLastWithShiftG()
    {
        var items = MakeItems("a", "b", "c", "d");
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.J);
        reader.EnqueueRaw(new ConsoleKeyInfo('G', ConsoleKey.G, shift: true, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("d", result.SelectedEntryId);
    }

    [Fact]
    public async Task NavigateAsync_QuitReturnsNullSelection()
    {
        var items = MakeItems("a", "b");
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.K);
        reader.EnqueueKey(ConsoleKey.Q);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Null(result.SelectedEntryId);
        Assert.Equal(0, result.LastIndex);
    }

    [Fact]
    public async Task NavigateAsync_EscapeAlsoQuits()
    {
        var items = MakeItems("a");
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.Escape);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Null(result.SelectedEntryId);
    }

    [Fact]
    public async Task NavigateAsync_RendersHeaderAndHighlightsSelected()
    {
        var items = MakeItems("a", "b", "c");
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        await navigator.NavigateAsync(items, reader, writer);

        var rendered = writer.ToString();
        Assert.Contains("tree navigator: 3 entries, selected 3/3", rendered, StringComparison.Ordinal);
        Assert.Contains(">> >  c", rendered, StringComparison.Ordinal);
        Assert.Contains("   >  a", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_UnknownKeyDoesNotMoveSelectionOrRedraw()
    {
        var items = MakeItems("a", "b");
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.F12); // ignored
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("b", result.SelectedEntryId);
        Assert.Equal(1, result.Frames);
    }

    private static IReadOnlyList<CodingAgentTreeViewItem> MakeItems(params string[] ids) =>
        ids.Select(id => new CodingAgentTreeViewItem(id, $">  {id}  <- _ message", id == ids[^1], true)).ToArray();

    private static IReadOnlyList<CodingAgentTreeViewItem> MakeFilterableItems() =>
    [
        new("u1", ">  u1  <- _ message user hello", false, true),
        new("a1", "*  a1  <- u1 message assistant [tool-only] read_file", false, true),
        new("t1", "*  t1  <- a1 message toolResult done", false, true),
        new("u2", ">  u2  <- t1 message user [labeled] world", true, true),
    ];

    private static IReadOnlyList<CodingAgentTreeViewItem> MakeNestedItems() =>
    [
        new("root", ">  root <- _ session name none", false, true, null, 0, "session_info"),
        new("child", "*    child <- root message user hello", false, true, "root", 1, "message"),
        new("grandchild", "*      grandchild <- child message assistant answer", false, true, "child", 2, "message"),
        new("sibling", ">    sibling <- root message user follow-up", true, true, "root", 1, "message"),
    ];

    private static IReadOnlyList<CodingAgentTreeViewItem> MakeBranchItems() =>
    [
        new("root", ">  root <- _ message user root", false, true, null, 0, "message"),
        new("a1", "*    a1 <- root message user branch-a", false, true, "root", 1, "message"),
        new("a2", "*      a2 <- a1 message assistant branch-a-response", false, true, "a1", 2, "message"),
        new("a3", "*      a3 <- a2 message user branch-a-deep", true, true, "a2", 2, "message"),
        new("b1", ">    b1 <- root message user branch-b", false, false, "root", 1, "message"),
        new("b2", ">      b2 <- b1 message assistant branch-b-response", false, false, "b1", 2, "message"),
    ];

    [Fact]
    public async Task NavigateAsync_SlashSearchFiltersVisibleItems()
    {
        var items = MakeFilterableItems();
        var reader = new FakeKeyReader();
        // '/' triggers search, type "user", Enter confirms, then Enter selects
        reader.EnqueueRaw(new ConsoleKeyInfo('/', ConsoleKey.Oem2, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('u', ConsoleKey.U, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('s', ConsoleKey.S, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('r', ConsoleKey.R, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter); // confirm search
        reader.EnqueueKey(ConsoleKey.Enter); // select

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        // Only user entries match "user", last one selected by default
        Assert.Equal("u2", result.SelectedEntryId);
    }

    [Fact]
    public async Task NavigateAsync_EscapeClearsSearchAndRestoresFullList()
    {
        var items = MakeFilterableItems();
        var reader = new FakeKeyReader();
        // Search for "user"
        reader.EnqueueRaw(new ConsoleKeyInfo('/', ConsoleKey.Oem2, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('u', ConsoleKey.U, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('s', ConsoleKey.S, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('r', ConsoleKey.R, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter); // confirm search
        // Escape clears search
        reader.EnqueueKey(ConsoleKey.Escape);
        // Now select — should be back to full list
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, writer);

        // After clearing search, last item in full list is selected
        Assert.Equal("u2", result.SelectedEntryId);
        var rendered = writer.ToString();
        Assert.Contains("4 entries", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_FCyclesFilterModes()
    {
        var items = MakeFilterableItems();
        var reader = new FakeKeyReader();
        // Press 'f' once → no-tools (removes toolResult and tool-only assistant)
        reader.EnqueueRaw(new ConsoleKeyInfo('f', ConsoleKey.F, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, writer);

        // no-tools filter removes a1 (tool-only) and t1 (toolResult), leaves u1 and u2
        Assert.Equal("u2", result.SelectedEntryId);
        var rendered = writer.ToString();
        Assert.Contains("filter=notools", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 entries", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_NMovesToNextMatchInFilteredList()
    {
        var items = MakeFilterableItems();
        var reader = new FakeKeyReader();
        // Search for "message"
        reader.EnqueueRaw(new ConsoleKeyInfo('/', ConsoleKey.Oem2, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('m', ConsoleKey.M, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('s', ConsoleKey.S, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter); // confirm search — all 4 match
        // n wraps forward from last (index 3) to first (index 0)
        reader.EnqueueRaw(new ConsoleKeyInfo('n', ConsoleKey.N, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("u1", result.SelectedEntryId);
    }

    [Fact]
    public async Task NavigateAsync_EnterWithNoSearchMatches_ReturnsNullSelection()
    {
        var items = MakeFilterableItems();
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('/', ConsoleKey.Oem2, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('z', ConsoleKey.Z, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('z', ConsoleKey.Z, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('z', ConsoleKey.Z, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, writer);

        Assert.Null(result.SelectedEntryId);
        Assert.Contains("no matches", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_SpaceFoldsSelectedEntryDescendants()
    {
        var items = MakeNestedItems();
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, writer);

        Assert.Equal("root", result.SelectedEntryId);
        var rendered = writer.ToString();
        Assert.Contains("folded 1", rendered, StringComparison.Ordinal);
        Assert.Contains("root <- _ session name none [folded]", rendered, StringComparison.Ordinal);
        Assert.Contains("1 entries", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_SpaceExpandsFoldedEntry()
    {
        var items = MakeNestedItems();
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('G', ConsoleKey.G, shift: true, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, writer);

        Assert.Equal("sibling", result.SelectedEntryId);
        var rendered = writer.ToString();
        Assert.Contains("4 entries", rendered, StringComparison.Ordinal);
        Assert.Contains("entry sibling, type message, depth 1, leaf", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_PageKeysMoveByWindowStep()
    {
        var items = MakeItems("a", "b", "c", "d", "e", "f", "g");
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.PageUp);
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("b", result.SelectedEntryId);

        reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.PageDown);
        reader.EnqueueKey(ConsoleKey.Enter);

        result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("f", result.SelectedEntryId);
    }

    [Fact]
    public async Task NavigateAsync_LeftRightArrowsPageSelection()
    {
        var items = MakeItems("a", "b", "c", "d", "e", "f", "g");
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.LeftArrow);
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("b", result.SelectedEntryId);

        reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.RightArrow);
        reader.EnqueueKey(ConsoleKey.Enter);

        result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("f", result.SelectedEntryId);
    }

    [Fact]
    public async Task NavigateAsync_CtrlLeftFoldsOrJumpsToPreviousBranchSegment()
    {
        var items = MakeBranchItems();
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: false, control: true));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, writer);

        Assert.Equal("root", result.SelectedEntryId);
        var rendered = writer.ToString();
        Assert.Contains("b1 <- root message user branch-b [folded]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_CtrlRightUnfoldsOrJumpsToNextBranchSegmentEnd()
    {
        var items = MakeBranchItems();
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueKey(ConsoleKey.UpArrow);
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: false, control: true));
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, writer);

        Assert.Equal("a3", result.SelectedEntryId);
        var rendered = writer.ToString();
        Assert.Contains("6 entries", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_AltLeftRightAreBranchNavigationAliases()
    {
        var items = MakeBranchItems();
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: true, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: true, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: true, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("b1", result.SelectedEntryId);
    }

    [Fact]
    public async Task NavigateAsync_SearchAndFilterResetFoldState()
    {
        var items = MakeBranchItems();
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueRaw(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('/', ConsoleKey.Oem2, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('b', ConsoleKey.B, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);
        reader.EnqueueKey(ConsoleKey.Escape);
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("a2", result.SelectedEntryId);

        reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueRaw(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('f', ConsoleKey.F, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('f', ConsoleKey.F, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('f', ConsoleKey.F, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('f', ConsoleKey.F, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('f', ConsoleKey.F, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueKey(ConsoleKey.DownArrow);
        reader.EnqueueKey(ConsoleKey.Enter);

        result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Equal("a2", result.SelectedEntryId);
    }

    private sealed class FakeKeyReader : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();

        public void EnqueueKey(ConsoleKey key) =>
            _keys.Enqueue(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: false));

        public void EnqueueRaw(ConsoleKeyInfo info) => _keys.Enqueue(info);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            if (_keys.Count == 0) throw new InvalidOperationException("No more queued keys.");
            return ValueTask.FromResult(_keys.Dequeue());
        }
    }
}
