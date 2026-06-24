using System.Text.Json;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class JsonlSessionStorageTests
{
    [Fact]
    public async Task CreateAsync_WritesHeaderAndAppendsEntries()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");

        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");

        Assert.True(File.Exists(filePath));
        var lines = File.ReadAllText(filePath).Trim().Split('\n');
        Assert.Single(lines);
        Assert.Equal("session", JsonDocument.Parse(lines[0]).RootElement.GetProperty("type").GetString());
        Assert.Null(await storage.GetLeafIdAsync());

        await storage.AppendEntryAsync(new MessageSessionEntry(
            "user-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));

        lines = File.ReadAllText(filePath).Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("user-1", JsonDocument.Parse(lines[1]).RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task OpenAsync_LoadsExistingEntriesAndReconstructsLeaf()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        var root = new MessageSessionEntry(
            "root",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("root"));
        var child = root with
        {
            Id = "child",
            ParentId = "root",
            Message = new AssistantMessage([new TextContent("child")])
        };
        await storage.AppendEntryAsync(root);
        await storage.AppendEntryAsync(child);

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);

        Assert.Equal("child", await loaded.GetLeafIdAsync());
        Assert.Equal(["root", "child"], (await loaded.GetEntriesAsync()).Select(static entry => entry.Id));
        await loaded.SetLeafIdAsync("root");

        var reloaded = await JsonlSessionStorage.OpenAsync(filePath);
        Assert.Equal("root", await reloaded.GetLeafIdAsync());
        Assert.IsType<LeafSessionEntry>((await reloaded.GetEntriesAsync()).Last());
        Assert.Equal(["root", "child"], (await loaded.GetPathToRootAsync("child")).Select(static entry => entry.Id));
    }

    [Fact]
    public async Task LoadMetadataAsync_ReadsOnlyHeaderMetadata()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(
            filePath,
            temp.Path,
            "session-1",
            parentSessionPath: "/tmp/parent.jsonl");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "entry-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));

        var metadata = await JsonlSessionStorage.LoadMetadataAsync(filePath);

        Assert.Equal("session-1", metadata.Id);
        Assert.Equal(temp.Path, metadata.Cwd);
        Assert.Equal(Path.GetFullPath(filePath), metadata.Path);
        Assert.Equal("/tmp/parent.jsonl", metadata.ParentSessionPath);
    }

    [Fact]
    public async Task OpenAsync_ThrowsForMalformedSessionHeader()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        await File.WriteAllTextAsync(filePath, "not json\n");

        var ex = await Assert.ThrowsAsync<SessionException>(() => JsonlSessionStorage.OpenAsync(filePath));

        Assert.Equal("invalid_session", ex.Code);
        Assert.Contains("first line is not a valid session header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_ThrowsForMalformedEntryLine()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        await File.WriteAllTextAsync(
            filePath,
            """
            {"type":"session","version":3,"id":"session-1","timestamp":"2026-01-01T00:00:00.000Z","cwd":"."}
            not json
            """);

        var ex = await Assert.ThrowsAsync<SessionException>(() => JsonlSessionStorage.OpenAsync(filePath));

        Assert.Equal("invalid_entry", ex.Code);
        Assert.Contains("line 2 is not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LabelLookupSurvivesReload()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "entry-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        await storage.AppendEntryAsync(new LabelSessionEntry(
            "label-1",
            "entry-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "entry-1",
            "checkpoint"));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);

        Assert.Equal("checkpoint", await loaded.GetLabelAsync("entry-1"));
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-agentcore-jsonl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
