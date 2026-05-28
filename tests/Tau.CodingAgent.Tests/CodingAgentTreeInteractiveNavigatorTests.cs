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
        new("u2", ">  u2  <- t1 message user [labeled] world", true, true, HasLabel: true),
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
    public async Task NavigateAsync_CtrlUTogglesUserOnly_AndCtrlATogglesAll()
    {
        var items = MakeFilterableItems();
        var navigator = new CodingAgentTreeInteractiveNavigator();

        var userOnlyReader = new FakeKeyReader();
        userOnlyReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.U, shift: false, alt: false, control: true));
        userOnlyReader.EnqueueKey(ConsoleKey.Enter);

        var userOnlyWriter = new StringWriter();
        var userOnlyResult = await navigator.NavigateAsync(items, userOnlyReader, userOnlyWriter);

        Assert.Equal("u2", userOnlyResult.SelectedEntryId);
        var userOnlyRendered = userOnlyWriter.ToString();
        Assert.Contains("filter=useronly", userOnlyRendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 entries", userOnlyRendered, StringComparison.Ordinal);

        var allReader = new FakeKeyReader();
        allReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.A, shift: false, alt: false, control: true));
        allReader.EnqueueKey(ConsoleKey.Enter);

        var allWriter = new StringWriter();
        var allResult = await navigator.NavigateAsync(items, allReader, allWriter);

        Assert.Equal("u2", allResult.SelectedEntryId);
        var allRendered = allWriter.ToString();
        Assert.Contains("filter=all", allRendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4 entries", allRendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_FilterHotkeysMatchUpstreamParity()
    {
        var items = MakeFilterableItems();
        var navigator = new CodingAgentTreeInteractiveNavigator();

        var noToolsReader = new FakeKeyReader();
        noToolsReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.T, shift: false, alt: false, control: true));
        noToolsReader.EnqueueKey(ConsoleKey.Enter);
        var noToolsWriter = new StringWriter();
        var noToolsResult = await navigator.NavigateAsync(items, noToolsReader, noToolsWriter);
        Assert.Equal("u2", noToolsResult.SelectedEntryId);
        Assert.Contains("filter=notools", noToolsWriter.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 entries", noToolsWriter.ToString(), StringComparison.Ordinal);

        var labeledReader = new FakeKeyReader();
        labeledReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.L, shift: false, alt: false, control: true));
        labeledReader.EnqueueKey(ConsoleKey.Enter);
        var labeledWriter = new StringWriter();
        var labeledResult = await navigator.NavigateAsync(items, labeledReader, labeledWriter);
        Assert.Equal("u2", labeledResult.SelectedEntryId);
        Assert.Contains("filter=labeledonly", labeledWriter.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 entries", labeledWriter.ToString(), StringComparison.Ordinal);

        var defaultReader = new FakeKeyReader();
        defaultReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.L, shift: false, alt: false, control: true));
        defaultReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.D, shift: false, alt: false, control: true));
        defaultReader.EnqueueKey(ConsoleKey.Enter);
        var defaultWriter = new StringWriter();
        var defaultResult = await navigator.NavigateAsync(items, defaultReader, defaultWriter);
        Assert.Equal("u2", defaultResult.SelectedEntryId);
        Assert.Contains("filter=default", defaultWriter.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4 entries", defaultWriter.ToString(), StringComparison.Ordinal);

        var cycleReader = new FakeKeyReader();
        cycleReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.O, shift: false, alt: false, control: true));
        cycleReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.O, shift: true, alt: false, control: true));
        cycleReader.EnqueueKey(ConsoleKey.Enter);
        var cycleWriter = new StringWriter();
        var cycleResult = await navigator.NavigateAsync(items, cycleReader, cycleWriter);
        Assert.Equal("u2", cycleResult.SelectedEntryId);
        Assert.Contains("filter=default", cycleWriter.ToString(), StringComparison.OrdinalIgnoreCase);
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
    public async Task NavigateAsync_InitialFoldedEntryIdsHideDescendants()
    {
        var items = MakeNestedItems();
        var reader = new FakeKeyReader();
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(
            items,
            reader,
            writer,
            initialFoldedEntryIds: ["root"]);

        Assert.Equal("root", result.SelectedEntryId);
        var rendered = writer.ToString();
        Assert.Contains("folded 1", rendered, StringComparison.Ordinal);
        Assert.Contains("root <- _ session name none [folded]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("grandchild", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_FoldChangesInvokeCallback()
    {
        var items = MakeNestedItems();
        var reader = new FakeKeyReader();
        var changes = new List<IReadOnlySet<string>>();
        reader.EnqueueRaw(new ConsoleKeyInfo('g', ConsoleKey.G, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var navigator = new CodingAgentTreeInteractiveNavigator();
        await navigator.NavigateAsync(
            items,
            reader,
            new StringWriter(),
            foldedEntryIdsChanged: folded => changes.Add(folded));

        Assert.Equal(2, changes.Count);
        Assert.Contains("root", changes[0]);
        Assert.Empty(changes[1]);
    }

    [Fact]
    public async Task NavigateAsync_ShiftTTogglesLabelTimestampsInRenderedLines()
    {
        var items = new[]
        {
            new CodingAgentTreeViewItem(
                "entry",
                ">  entry <- _ message user labeled [important]",
                true,
                true,
                null,
                0,
                "message",
                SearchText: "message user labeled important",
                BaseDisplayLine: ">  entry <- _ message user labeled [important]",
                LabelTimestampSuffix: " @2026-05-24 18:00:00 +08:00",
                LabelTimestampsEnabled: false)
        };

        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('T', ConsoleKey.T, shift: true, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var writer = new StringWriter();
        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, writer);

        Assert.Equal("entry", result.SelectedEntryId);
        var rendered = writer.ToString();
        Assert.Contains("[+label time]", rendered, StringComparison.Ordinal);
        Assert.Contains("@2026-05-24 18:00:00 +08:00", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigateAsync_ShiftLReturnsLabelEditRequestForSelectedEntry()
    {
        var items = MakeItems("a", "b", "c");
        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('L', ConsoleKey.L, shift: true, alt: false, control: false));

        var navigator = new CodingAgentTreeInteractiveNavigator();
        var result = await navigator.NavigateAsync(items, reader, new StringWriter());

        Assert.Null(result.SelectedEntryId);
        Assert.Equal("c", result.LabelEditEntryId);
        Assert.Equal(2, result.LastIndex);
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
