using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentTreeCompositionNavigatorTests
{
    [Fact]
    public async Task RunAsync_MetadataViewerSelectionUpdatesTreeSelection()
    {
        var items = new[]
        {
            new CodingAgentTreeViewItem("root", ">  root <- _ message user root", false, true, null, 0, "message"),
            new CodingAgentTreeViewItem("child", ">  child <- root message user child", true, true, "root", 1, "message")
        };

        var requestedEntryIds = new List<string>();
        var snapshot = new CodingAgentTreeMetadataSnapshot(
            FilePath: "session.jsonl",
            SessionId: "session-1",
            LeafId: "child",
            Cwd: "cwd",
            ParentSession: null,
            EntryCount: 2,
            BranchEntryCount: 2,
            MessageCount: 2,
            BranchMessageCount: 2,
            BranchCount: 0,
            LabelCount: 0,
            FocusEntryId: "child",
            VisibleEntryIds: ["child", "root"],
            EntriesById: new Dictionary<string, CodingAgentTreeMetadataEntrySnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["child"] = new(
                    "child",
                    "child <- root message user child",
                    ["entry: child", "type: message", "parent: root", "path: leaf"],
                    [new CodingAgentTreeMetadataRelationSnapshot("parent", "root")],
                    [new CodingAgentTreeMetadataSectionSnapshot("Message", ["message role: user", "preview: child"])]),
                ["root"] = new(
                    "root",
                    "root <- none message user root",
                    ["entry: root", "type: message", "parent: none", "path: branch"],
                    [],
                    [new CodingAgentTreeMetadataSectionSnapshot("Message", ["message role: user", "preview: root"])])
            });

        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('i', ConsoleKey.I, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('1', ConsoleKey.D1, shift: false, alt: false, control: false));
        reader.EnqueueRaw(new ConsoleKeyInfo('q', ConsoleKey.Q, shift: false, alt: false, control: false));
        reader.EnqueueKey(ConsoleKey.Enter);

        var session = new TuiCompositionSession(new TuiNullRenderSurface(), reader);
        var result = await CodingAgentTreeCompositionNavigator.RunAsync(
            items,
            session,
            metadataSnapshotProvider: entryId =>
            {
                requestedEntryIds.Add(entryId);
                return snapshot;
            });

        Assert.Equal(["child"], requestedEntryIds);
        Assert.Equal("root", result.SelectedEntryId);
        Assert.Equal(0, result.LastIndex);
    }

    [Fact]
    public async Task RunAsync_LocalSearchUsesSearchTextWhenDisplayLineDoesNotContainQuery()
    {
        var items = new[]
        {
            new CodingAgentTreeViewItem(
                "first",
                ">  first <- _ message user alpha",
                false,
                true,
                null,
                0,
                "message",
                SearchText: "message user alpha"),
            new CodingAgentTreeViewItem(
                "second",
                ">  second <- first message assistant beta",
                true,
                true,
                "first",
                1,
                "message",
                SearchText: "message assistant beta rare-token")
        };

        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('/', ConsoleKey.Oem2, shift: false, alt: false, control: false));
        foreach (var ch in "rare-token")
        {
            reader.EnqueueRaw(new ConsoleKeyInfo(ch, ConsoleKey.A, shift: false, alt: false, control: false));
        }

        reader.EnqueueKey(ConsoleKey.Enter);
        reader.EnqueueKey(ConsoleKey.Enter);

        var session = new TuiCompositionSession(new TuiNullRenderSurface(), reader);
        var result = await CodingAgentTreeCompositionNavigator.RunAsync(items, session);

        Assert.Equal("second", result.SelectedEntryId);
    }

    [Fact]
    public async Task RunAsync_ShiftLReturnsLabelEditRequestForSelectedEntry()
    {
        var items = new[]
        {
            new CodingAgentTreeViewItem("root", ">  root <- _ message user root", false, true, null, 0, "message"),
            new CodingAgentTreeViewItem("child", ">  child <- root message assistant child", true, true, "root", 1, "message")
        };

        var reader = new FakeKeyReader();
        reader.EnqueueRaw(new ConsoleKeyInfo('L', ConsoleKey.L, shift: true, alt: false, control: false));

        var session = new TuiCompositionSession(new TuiNullRenderSurface(), reader);
        var result = await CodingAgentTreeCompositionNavigator.RunAsync(items, session);

        Assert.Null(result.SelectedEntryId);
        Assert.Equal("child", result.LabelEditEntryId);
    }

    [Fact]
    public async Task RunAsync_FilterHotkeysMatchUpstreamParity()
    {
        var items = new[]
        {
            new CodingAgentTreeViewItem("first", ">  first <- _ message user alpha", false, true, null, 0, "message"),
            new CodingAgentTreeViewItem("second", ">  second <- first message user beta [saved]", false, true, "first", 1, "message", HasLabel: true),
            new CodingAgentTreeViewItem("third", ">  third <- second message assistant gamma", true, true, "second", 2, "message")
        };

        var labeledReader = new FakeKeyReader();
        labeledReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.L, shift: false, alt: false, control: true));
        labeledReader.EnqueueKey(ConsoleKey.Enter);
        var labeledSession = new TuiCompositionSession(new TuiNullRenderSurface(), labeledReader);
        var labeledResult = await CodingAgentTreeCompositionNavigator.RunAsync(items, labeledSession);
        Assert.Equal("second", labeledResult.SelectedEntryId);

        var defaultReader = new FakeKeyReader();
        defaultReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.L, shift: false, alt: false, control: true));
        defaultReader.EnqueueRaw(new ConsoleKeyInfo('\0', ConsoleKey.D, shift: false, alt: false, control: true));
        defaultReader.EnqueueKey(ConsoleKey.Enter);
        var defaultSession = new TuiCompositionSession(new TuiNullRenderSurface(), defaultReader);
        var defaultResult = await CodingAgentTreeCompositionNavigator.RunAsync(items, defaultSession);
        Assert.Equal("second", defaultResult.SelectedEntryId);
    }

    private sealed class FakeKeyReader : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();

        public void EnqueueKey(ConsoleKey key) =>
            _keys.Enqueue(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: false));

        public void EnqueueRaw(ConsoleKeyInfo info) => _keys.Enqueue(info);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            if (_keys.Count == 0)
            {
                throw new InvalidOperationException("No more queued keys.");
            }

            return ValueTask.FromResult(_keys.Dequeue());
        }
    }
}
