using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class JsonlSessionRepoTests
{
    [Fact]
    public async Task CreateAsync_StoresSessionsBelowEncodedCwdDirectoriesAndListsByCwd()
    {
        using var temp = TempDirectory.Create();
        var cwd = OperatingSystem.IsWindows() ? "C:\\tmp\\my-project" : "/tmp/my-project";
        var otherCwd = OperatingSystem.IsWindows() ? "C:\\tmp\\other-project" : "/tmp/other-project";
        var repo = new JsonlSessionRepo(temp.Path);

        var session = await repo.CreateAsync(cwd, "session-1");
        var otherSession = await repo.CreateAsync(otherCwd, "session-2");
        var metadata = await session.GetMetadataAsync();
        var otherMetadata = await otherSession.GetMetadataAsync();

        Assert.Contains("--", metadata.Path, StringComparison.Ordinal);
        Assert.Contains("my-project", metadata.Path, StringComparison.Ordinal);
        Assert.Contains("other-project", otherMetadata.Path, StringComparison.Ordinal);
        Assert.True(File.Exists(metadata.Path));
        Assert.Equal(["session-1"], (await repo.ListAsync(cwd)).Select(static sessionMetadata => sessionMetadata.Id));
        Assert.Equal(["session-1", "session-2"], (await repo.ListAsync()).Select(static sessionMetadata => sessionMetadata.Id).Order());
    }

    [Fact]
    public async Task OpenDeleteAndForkByMetadata()
    {
        using var temp = TempDirectory.Create();
        var repo = new JsonlSessionRepo(temp.Path);
        var source = await repo.CreateAsync("/tmp/source", "source-session");
        var sourceMetadata = await source.GetMetadataAsync();
        var user1 = await source.AppendMessageAsync(new UserMessage("one"));
        var assistant1 = await source.AppendMessageAsync(new AssistantMessage([new TextContent("two")]));
        var user2 = await source.AppendMessageAsync(new UserMessage("three"));

        Assert.Equal(sourceMetadata, await (await repo.OpenAsync(sourceMetadata)).GetMetadataAsync());

        var fork = await repo.ForkAsync(
            sourceMetadata,
            "/tmp/target",
            new SessionForkOptions(EntryId: user2, Id: "fork-session"));
        var forkMetadata = await fork.GetMetadataAsync();

        Assert.Equal("/tmp/target", forkMetadata.Cwd);
        Assert.Equal(sourceMetadata.Path, forkMetadata.ParentSessionPath);
        Assert.Equal([user1, assistant1], (await fork.GetEntriesAsync()).Select(static entry => entry.Id));

        var fullFork = await repo.ForkAsync(
            sourceMetadata,
            "/tmp/target",
            new SessionForkOptions(Id: "full-fork-session"));
        Assert.Equal([user1, assistant1, user2], (await fullFork.GetEntriesAsync()).Select(static entry => entry.Id));

        await repo.DeleteAsync(sourceMetadata);
        Assert.False(File.Exists(sourceMetadata.Path));
        var ex = await Assert.ThrowsAsync<SessionException>(() => repo.OpenAsync(sourceMetadata));
        Assert.Equal("not_found", ex.Code);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-agentcore-jsonl-repo-" + Guid.NewGuid().ToString("N"));
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
