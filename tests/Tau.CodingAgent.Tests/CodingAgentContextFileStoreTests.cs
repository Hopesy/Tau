using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentContextFileStoreTests
{
    [Fact]
    public async Task Load_ReadsUserThenProjectAncestorsInTopDownOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "tau-context-files-" + Guid.NewGuid().ToString("N"));
        var userDirectory = Path.Combine(root, "user");
        var projectDirectory = Path.Combine(root, "repo");
        var nestedDirectory = Path.Combine(projectDirectory, "src", "feature");
        Directory.CreateDirectory(userDirectory);
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(Path.Combine(userDirectory, "AGENTS.md"), "user context");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "CLAUDE.md"), "project parent context");
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "AGENTS.md"), "nested agents context");
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "CLAUDE.md"), "nested claude should be ignored");

        try
        {
            var store = new CodingAgentContextFileStore(
                cwd: nestedDirectory,
                userContextDirectory: userDirectory);

            var files = store.Load();

            Assert.Collection(files,
                file =>
                {
                    Assert.Equal(Path.Combine(userDirectory, "AGENTS.md"), file.FilePath);
                    Assert.Equal("user", file.Scope);
                    Assert.Equal("user context", file.Content);
                },
                file =>
                {
                    Assert.Equal(Path.Combine(projectDirectory, "CLAUDE.md"), file.FilePath);
                    Assert.Equal("project", file.Scope);
                    Assert.Equal("project parent context", file.Content);
                },
                file =>
                {
                    Assert.Equal(Path.Combine(nestedDirectory, "AGENTS.md"), file.FilePath);
                    Assert.Equal("project", file.Scope);
                    Assert.Equal("nested agents context", file.Content);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_PrefersAgentsOverClaudeInSameDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-context-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "AGENTS.md"), "agents wins");
        await File.WriteAllTextAsync(Path.Combine(directory, "CLAUDE.md"), "claude loses");

        try
        {
            var store = new CodingAgentContextFileStore(
                cwd: directory,
                userContextDirectory: Path.Combine(directory, "missing-user"));

            var file = Assert.Single(store.Load());

            Assert.Equal(Path.Combine(directory, "AGENTS.md"), file.FilePath);
            Assert.Equal("agents wins", file.Content);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_WhenDefaultsDisabled_ReturnsEmpty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-context-disabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "AGENTS.md"), "project context");

        try
        {
            var store = new CodingAgentContextFileStore(cwd: directory, includeDefaults: false);

            Assert.Empty(store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
