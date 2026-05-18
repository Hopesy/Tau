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
