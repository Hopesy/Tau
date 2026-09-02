using System.Text.Json;
using Tau.AgentCore.Harness;
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
        var header = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("header", header.GetProperty("kind").GetString());
        Assert.Equal(4, header.GetProperty("version").GetInt32());
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

    [Fact]
    public async Task OpenAsync_RoundTripsCompactionAndCustomEntries()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "user-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        await storage.AppendEntryAsync(new CustomSessionEntry(
            "custom-1",
            "user-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "internal",
            JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone()));
        await storage.AppendEntryAsync(new CustomMessageSessionEntry(
            "custom-message-1",
            "custom-1",
            DateTimeOffset.Parse("2026-01-01T00:00:02.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "visible",
            [new TextContent("custom text"), new ImageContent("abc", "image/png")],
            Display: true,
            JsonDocument.Parse("""{"source":"test"}""").RootElement.Clone()));
        await storage.AppendEntryAsync(new CompactionSessionEntry(
            "compaction-1",
            "custom-message-1",
            DateTimeOffset.Parse("2026-01-01T00:00:03.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "summary",
            "user-1",
            42,
            JsonDocument.Parse("""{"files":["README.md"]}""").RootElement.Clone(),
            FromHook: true));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var entries = await loaded.GetEntriesAsync();

        var custom = Assert.IsType<CustomSessionEntry>(entries[1]);
        var customData = Assert.IsType<JsonElement>(custom.Data);
        Assert.Equal("ok", customData.GetProperty("status").GetString());

        var customMessage = Assert.IsType<CustomMessageSessionEntry>(entries[2]);
        Assert.Equal("visible", customMessage.CustomType);
        Assert.True(customMessage.Display);
        Assert.Equal("custom text", Assert.IsType<TextContent>(customMessage.Content[0]).Text);
        Assert.Equal("image/png", Assert.IsType<ImageContent>(customMessage.Content[1]).MimeType);
        var details = Assert.IsType<JsonElement>(customMessage.Details);
        Assert.Equal("test", details.GetProperty("source").GetString());

        var compaction = Assert.IsType<CompactionSessionEntry>(entries[3]);
        Assert.Equal("summary", compaction.Summary);
        Assert.Equal("user-1", compaction.FirstKeptEntryId);
        Assert.Equal(42, compaction.TokensBefore);
        Assert.True(compaction.FromHook);
    }

    [Fact]
    public async Task OpenAsync_RoundTripsTypedCompactionDetailsAsJson()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "user-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        await storage.AppendEntryAsync(new CompactionSessionEntry(
            "compaction-1",
            "user-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "summary",
            "user-1",
            42,
            new AgentCompactionDetails(["README.md"], ["src/Program.cs"])));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var compaction = Assert.IsType<CompactionSessionEntry>((await loaded.GetEntriesAsync()).Last());
        var details = Assert.IsType<JsonElement>(compaction.Details);

        Assert.Equal("README.md", details.GetProperty("readFiles")[0].GetString());
        Assert.Equal("src/Program.cs", details.GetProperty("modifiedFiles")[0].GetString());
    }

    [Fact]
    public async Task OpenAsync_RoundTripsTypedBranchSummaryDetailsAsJson()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "user-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one")));
        await storage.AppendEntryAsync(new BranchSummarySessionEntry(
            "branch-summary-1",
            "user-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "from-1",
            "summary",
            new AgentBranchSummaryDetails(["README.md"], ["src/Program.cs"])));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        var branchSummary = Assert.IsType<BranchSummarySessionEntry>((await loaded.GetEntriesAsync()).Last());
        var details = Assert.IsType<JsonElement>(branchSummary.Details);

        Assert.Equal("README.md", details.GetProperty("readFiles")[0].GetString());
        Assert.Equal("src/Program.cs", details.GetProperty("modifiedFiles")[0].GetString());
    }

    [Fact]
    public async Task AppendRecordAsync_PersistsRecordLogAcrossReload()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-1");
        await storage.AppendRecordAsync(new OperationStartedRecord(
            "run-1",
            1,
            "main",
            DateTimeOffset.UtcNow,
            "run"));
        await storage.AppendRecordAsync(new QueueEnqueuedRecord(
            "queue-1",
            2,
            "main",
            DateTimeOffset.UtcNow,
            "nextRun",
            "entry-1"));

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);

        var records = await loaded.FindRecordsAsync(new RecordQuery { Lane = "main", Order = EntryOrder.OldestFirst });
        Assert.Collection(
            records,
            record => Assert.IsType<OperationStartedRecord>(record),
            record => Assert.IsType<QueueEnqueuedRecord>(record));
        Assert.Single(await loaded.FindOpenOperationsAsync("main"));
        Assert.Contains("\"kind\":\"record\"", File.ReadAllText(filePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_ReadsPiV4HeaderAndMutationLines()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session-v4.jsonl");
        await File.WriteAllTextAsync(
            filePath,
            ""
            + "{\"kind\":\"header\",\"version\":4,\"id\":\"session-v4\",\"createdAt\":1767225600000,\"cwd\":\"" + temp.Path.Replace("\\", "\\\\") + "\"}\n"
            + "{\"kind\":\"entry\",\"seq\":1,\"id\":\"entry-1\",\"parentId\":null,\"timestamp\":1767225600000,\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}\n"
            + "{\"kind\":\"record\",\"seq\":2,\"id\":\"run-1\",\"lane\":\"main\",\"timestamp\":1767225600000,\"type\":\"operation_started\",\"operationKind\":\"run\"}\n");

        var storage = await JsonlSessionStorage.OpenAsync(filePath);

        Assert.Equal("session-v4", (await storage.GetMetadataAsync()).Id);
        Assert.Equal("entry-1", (await storage.GetEntriesAsync()).Single().Id);
        Assert.Equal("run-1", Assert.Single(await storage.FindOpenOperationsAsync("main")).Id);
    }

    [Fact]
    public async Task V4LaneAndFactMutations_SurviveReloadAndPreserveLogOrder()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "session-v4-lanes.jsonl");
        var storage = await JsonlSessionStorage.CreateAsync(filePath, temp.Path, "session-v4-lanes");
        await storage.AppendEntryAsync(new MessageSessionEntry(
            "root",
            null,
            DateTimeOffset.UtcNow,
            new UserMessage("root")));
        await storage.CreateLaneAsync("review", "root");
        await storage.SetNameAsync("Review session");
        await storage.SetLabelAsync("root", "checkpoint");

        var loaded = await JsonlSessionStorage.OpenAsync(filePath);
        Assert.Contains(await loaded.GetLanesAsync(), lane => lane == new LanePointer("review", "root"));
        Assert.Equal("Review session", await loaded.GetNameAsync());
        Assert.Equal("checkpoint", await loaded.GetLabelAsync("root"));

        var log = await loaded.GetLogAsync();
        Assert.Equal(["entry", "lane", "fact", "fact"], log.Select(item => item.Kind));
        Assert.True(log.Zip(log.Skip(1), (left, right) => left.Sequence < right.Sequence).All(static result => result));
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
