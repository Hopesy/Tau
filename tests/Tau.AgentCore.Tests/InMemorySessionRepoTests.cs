using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class InMemorySessionRepoTests
{
    [Fact]
    public async Task OpenDeleteAndForkByMetadata()
    {
        var repo = new InMemorySessionRepo();
        var session = await repo.CreateAsync("session-1");
        var metadata = await session.GetMetadataAsync();
        var user1 = await session.AppendMessageAsync(new UserMessage("one"));
        var assistant1 = await session.AppendMessageAsync(new AssistantMessage([new TextContent("two")]));
        var user2 = await session.AppendMessageAsync(new UserMessage("three"));

        Assert.Same(session, await repo.OpenAsync(metadata));
        Assert.Equal(["session-1"], (await repo.ListAsync()).Select(static info => info.Id));

        var fork = await repo.ForkAsync(metadata, new SessionForkOptions(EntryId: user2, Id: "session-2"));
        Assert.Equal([user1, assistant1], (await fork.GetEntriesAsync()).Select(static entry => entry.Id));

        var fullFork = await repo.ForkAsync(metadata, new SessionForkOptions(Id: "session-3"));
        Assert.Equal([user1, assistant1, user2], (await fullFork.GetEntriesAsync()).Select(static entry => entry.Id));

        var atFork = await repo.ForkAsync(metadata, new SessionForkOptions(EntryId: user2, Position: "at", Id: "session-4"));
        Assert.Equal([user1, assistant1, user2], (await atFork.GetEntriesAsync()).Select(static entry => entry.Id));

        await repo.DeleteAsync(metadata);
        var ex = await Assert.ThrowsAsync<SessionException>(() => repo.OpenAsync(metadata));
        Assert.Equal("not_found", ex.Code);
        Assert.Equal("Session not found: session-1", ex.Message);
    }

    [Fact]
    public async Task ForkBeforeNonUserMessageFails()
    {
        var repo = new InMemorySessionRepo();
        var session = await repo.CreateAsync("session-1");
        var metadata = await session.GetMetadataAsync();
        await session.AppendMessageAsync(new UserMessage("one"));
        var assistant = await session.AppendMessageAsync(new AssistantMessage([new TextContent("two")]));

        var ex = await Assert.ThrowsAsync<SessionException>(
            () => repo.ForkAsync(metadata, new SessionForkOptions(EntryId: assistant, Id: "session-2")));

        Assert.Equal("invalid_fork_target", ex.Code);
        Assert.Equal($"Entry {assistant} is not a user message", ex.Message);
    }
}
