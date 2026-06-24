using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class InMemorySessionStorageTests
{
    [Fact]
    public async Task GetMetadataAsync_ReturnsConfiguredSessionMetadata()
    {
        var metadata = new SessionMetadata("session-1", "2026-01-01T00:00:00.0000000Z");
        var storage = new InMemorySessionStorage<SessionMetadata>(metadata);

        Assert.Equal(metadata, await storage.GetMetadataAsync());
    }

    [Fact]
    public async Task Constructor_CopiesInitialEntriesAndSetLeafPersistsLeafChanges()
    {
        var entry = new MessageSessionEntry(
            "entry-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one"));
        var initialEntries = new List<SessionTreeEntry> { entry };
        var storage = new InMemorySessionStorage<SessionMetadata>(entries: initialEntries);
        initialEntries.Add(entry with { Id = "entry-2" });

        Assert.Equal(["entry-1"], (await storage.GetEntriesAsync()).Select(static storedEntry => storedEntry.Id));
        Assert.Equal("entry-1", await storage.GetLeafIdAsync());

        await storage.SetLeafIdAsync(null);

        Assert.Null(await storage.GetLeafIdAsync());
        Assert.IsType<LeafSessionEntry>((await storage.GetEntriesAsync()).Last());
    }

    [Fact]
    public async Task SetLeafIdAsync_RejectsInvalidLeafIds()
    {
        var storage = new InMemorySessionStorage<SessionMetadata>();

        var ex = await Assert.ThrowsAsync<SessionException>(() => storage.SetLeafIdAsync("missing"));

        Assert.Equal("not_found", ex.Code);
        Assert.Equal("Entry missing not found", ex.Message);
    }

    [Fact]
    public async Task FindEntriesAsync_FiltersByEntryType()
    {
        var entry = new MessageSessionEntry(
            "entry-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one"));
        var storage = new InMemorySessionStorage<SessionMetadata>(entries: [entry]);

        Assert.Equal(["entry-1"], (await storage.FindEntriesAsync("message")).Select(static found => found.Id));
        Assert.Empty(await storage.FindEntriesAsync("session_info"));
    }

    [Fact]
    public async Task AppendEntryAsync_MaintainsLabelLookup()
    {
        var entry = new MessageSessionEntry(
            "entry-1",
            null,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            new UserMessage("one"));
        var storage = new InMemorySessionStorage<SessionMetadata>(entries: [entry]);

        Assert.Null(await storage.GetLabelAsync("entry-1"));

        await storage.AppendEntryAsync(new LabelSessionEntry(
            "label-1",
            "entry-1",
            DateTimeOffset.Parse("2026-01-01T00:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "entry-1",
            "checkpoint"));

        Assert.Equal("checkpoint", await storage.GetLabelAsync("entry-1"));

        await storage.AppendEntryAsync(new LabelSessionEntry(
            "label-2",
            "label-1",
            DateTimeOffset.Parse("2026-01-01T00:00:02.000Z", System.Globalization.CultureInfo.InvariantCulture),
            "entry-1",
            null));

        Assert.Null(await storage.GetLabelAsync("entry-1"));
    }

    [Fact]
    public async Task GetPathToRootAsync_WalksPathToRoot()
    {
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
        var storage = new InMemorySessionStorage<SessionMetadata>(entries: [root, child]);

        Assert.Equal(["root", "child"], (await storage.GetPathToRootAsync("child")).Select(static entry => entry.Id));
        Assert.Empty(await storage.GetPathToRootAsync(null));
    }
}
