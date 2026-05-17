using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class InputHistoryStoreTests
{
    [Fact]
    public void InMemoryStore_LoadsExistingEntriesIntoHistory()
    {
        var store = new InMemoryStore(["alpha", "beta", "gamma"]);

        var history = new InputHistory(store);

        Assert.Equal(3, history.Count);
        Assert.Equal("gamma", history.Peek(0));
        Assert.Equal("alpha", history.Peek(2));
    }

    [Fact]
    public void InMemoryStore_PersistsNewlyAddedEntries()
    {
        var store = new InMemoryStore([]);
        var history = new InputHistory(store);

        history.Add("first");
        history.Add("second");

        Assert.Equal(new[] { "first", "second" }, store.AppendedEntries);
    }

    [Fact]
    public void InMemoryStore_SkipsConsecutiveDuplicatesOnBothMemoryAndDisk()
    {
        var store = new InMemoryStore([]);
        var history = new InputHistory(store);

        history.Add("same");
        history.Add("same");
        history.Add("other");

        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { "same", "other" }, store.AppendedEntries);
    }

    [Fact]
    public void FileInputHistoryStore_RoundTripsThroughDisk()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-tui-history-");
        try
        {
            var path = Path.Combine(tempDir.FullName, "history");
            var store = new FileInputHistoryStore(path);

            var initial = new InputHistory(store);
            initial.Add("git status");
            initial.Add("dotnet build");

            // Recreate to ensure persistence.
            var reopened = new InputHistory(new FileInputHistoryStore(path));
            Assert.Equal(2, reopened.Count);
            Assert.Equal("dotnet build", reopened.Peek(0));
            Assert.Equal("git status", reopened.Peek(1));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FileInputHistoryStore_TruncatesPastMaxEntries()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-tui-history-cap-");
        try
        {
            var path = Path.Combine(tempDir.FullName, "history");
            var store = new FileInputHistoryStore(path, maxEntries: 3);
            var history = new InputHistory(store);

            history.Add("a");
            history.Add("b");
            history.Add("c");
            history.Add("d");

            var reopened = new InputHistory(new FileInputHistoryStore(path, maxEntries: 10));
            Assert.Equal(3, reopened.Count);
            Assert.Equal("d", reopened.Peek(0));
            Assert.Equal("b", reopened.Peek(2));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FileInputHistoryStore_IgnoresMissingFileOnLoad()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"tau-history-missing-{Guid.NewGuid():N}");
        var store = new FileInputHistoryStore(missing);

        var history = new InputHistory(store);

        Assert.Equal(0, history.Count);
    }

    private sealed class InMemoryStore : IInputHistoryStore
    {
        private readonly List<string> _initial;

        public InMemoryStore(IEnumerable<string> initial)
        {
            _initial = initial.ToList();
        }

        public List<string> AppendedEntries { get; } = [];

        public IEnumerable<string> Load() => _initial;

        public void Append(string entry) => AppendedEntries.Add(entry);
    }
}
