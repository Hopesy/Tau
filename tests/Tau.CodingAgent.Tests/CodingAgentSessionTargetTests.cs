using Tau.Ai;
using Tau.AgentCore;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

[CollectionDefinition(nameof(CodingAgentSessionTargetEnvironmentCollection), DisableParallelization = true)]
public sealed class CodingAgentSessionTargetEnvironmentCollection;

[Collection(nameof(CodingAgentSessionTargetEnvironmentCollection))]
public sealed class CodingAgentSessionTargetTests
{
    [Fact]
    public void Resolve_WithFlatSessionPath_UsesOnlyFlatSessionStore()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "session.json");
        var store = new CodingAgentSessionStore(path);
        store.Save(
            [new UserMessage([new TextContent("flat prompt")])],
            CreateModel(),
            "flat session");

        var target = CodingAgentSessionTarget.Resolve(path);
        var snapshot = target.LoadInitialSnapshot();

        Assert.NotNull(target.SessionStore);
        Assert.Null(target.TreeSessionController);
        Assert.False(target.PreferTreeSession);
        Assert.Equal(Path.GetFullPath(path), target.SessionStore!.Path);
        Assert.Equal("flat session", snapshot.Name);
        Assert.Equal("openai", snapshot.Provider);
        Assert.Equal("gpt-5.4", snapshot.Model);
        var user = Assert.IsType<UserMessage>(Assert.Single(snapshot.Messages));
        Assert.Equal("flat prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public void Resolve_WithJsonlSessionPath_UsesTreeSessionAndPrefersIt()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "session.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        runner.SessionName = "tree session";
        runner.MutableMessages.Add(new UserMessage([new TextContent("tree prompt")]));
        var controller = CodingAgentTreeSessionController.OpenOrCreate(path);
        controller.SyncFromRunner(runner);

        var target = CodingAgentSessionTarget.Resolve(path);
        var snapshot = target.LoadInitialSnapshot();

        Assert.Null(target.SessionStore);
        Assert.NotNull(target.TreeSessionController);
        Assert.True(target.PreferTreeSession);
        Assert.Equal(Path.GetFullPath(path), target.TreeSessionController!.Path);
        Assert.Equal("tree session", snapshot.Name);
        Assert.Equal("openai", snapshot.Provider);
        Assert.Equal("gpt-5.4", snapshot.Model);
        var user = Assert.IsType<UserMessage>(Assert.Single(snapshot.Messages));
        Assert.Equal("tree prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public void Resolve_WithSessionIdPrefix_OpensMatchingTreeSession()
    {
        using var temp = TempDirectory.Create();
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessionsDirectory);
        var path = Path.Combine(sessionsDirectory, "session.jsonl");
        var summary = WriteTreeSession(path, "id prompt", "id session");
        var prefix = summary.SessionId[..Math.Min(20, summary.SessionId.Length)];

        var target = CodingAgentSessionTarget.Resolve(prefix, sessionDirectory: sessionsDirectory);
        var snapshot = target.LoadInitialSnapshot();

        Assert.Null(target.SessionStore);
        Assert.NotNull(target.TreeSessionController);
        Assert.True(target.PreferTreeSession);
        Assert.Equal(Path.GetFullPath(path), target.TreeSessionController!.Path);
        Assert.Equal("id session", snapshot.Name);
        var user = Assert.IsType<UserMessage>(Assert.Single(snapshot.Messages));
        Assert.Equal("id prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public void Resolve_WithSessionIdPrefixMissing_Throws()
    {
        using var temp = TempDirectory.Create();
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");

        var ex = Assert.Throws<IOException>(() => CodingAgentSessionTarget.Resolve("missing-prefix", sessionDirectory: sessionsDirectory));

        Assert.Equal("No session found matching 'missing-prefix'", ex.Message);
    }

    [Fact]
    public void LoadInitialSnapshot_DefaultTargetKeepsFlatSessionWhenTreeIsEmpty()
    {
        using var temp = TempDirectory.Create();
        var previousCwd = Environment.CurrentDirectory;
        var previousSessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        var previousTreeSessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE");
        try
        {
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", null);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", null);
            Environment.CurrentDirectory = temp.Path;
            var flatPath = Path.Combine(temp.Path, ".tau", "coding-agent-session.json");
            new CodingAgentSessionStore(flatPath).Save(
                [new UserMessage([new TextContent("default flat prompt")])],
                CreateModel(),
                "default flat");

            var target = CodingAgentSessionTarget.Resolve(null);
            var snapshot = target.LoadInitialSnapshot();

            Assert.NotNull(target.SessionStore);
            Assert.NotNull(target.TreeSessionController);
            Assert.False(target.PreferTreeSession);
            Assert.Equal("default flat", snapshot.Name);
            var user = Assert.IsType<UserMessage>(Assert.Single(snapshot.Messages));
            Assert.Equal("default flat prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", previousSessionFile);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", previousTreeSessionFile);
        }
    }

    [Fact]
    public void Resolve_WithNoSession_DoesNotCreatePersistentStores()
    {
        using var temp = TempDirectory.Create();
        var previousCwd = Environment.CurrentDirectory;
        var previousSessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        var previousTreeSessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE");
        try
        {
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", null);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", null);
            Environment.CurrentDirectory = temp.Path;

            var target = CodingAgentSessionTarget.Resolve(
                "ignored.jsonl",
                continueRecent: true,
                sessionDirectory: Path.Combine(temp.Path, "sessions"),
                noSession: true);
            var snapshot = target.LoadInitialSnapshot();

            Assert.Null(target.SessionStore);
            Assert.Null(target.TreeSessionController);
            Assert.False(target.PreferTreeSession);
            Assert.Empty(snapshot.Messages);
            Assert.False(File.Exists(Path.Combine(temp.Path, ".tau", "coding-agent-session.jsonl")));
            Assert.False(Directory.Exists(Path.Combine(temp.Path, "sessions")));
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", previousSessionFile);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", previousTreeSessionFile);
        }
    }

    [Fact]
    public void Resolve_WithSessionDirectory_CreatesNewTreeSessionInDirectory()
    {
        using var temp = TempDirectory.Create();
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");

        var target = CodingAgentSessionTarget.Resolve(null, sessionDirectory: sessionsDirectory);
        var snapshot = target.LoadInitialSnapshot();

        Assert.Null(target.SessionStore);
        Assert.NotNull(target.TreeSessionController);
        Assert.True(target.PreferTreeSession);
        Assert.StartsWith(Path.GetFullPath(sessionsDirectory), target.TreeSessionController!.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(".jsonl", Path.GetExtension(target.TreeSessionController.Path));
        Assert.Empty(snapshot.Messages);
    }

    [Fact]
    public void Resolve_WithSessionDirectoryAndContinue_UsesMostRecentTreeSession()
    {
        using var temp = TempDirectory.Create();
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessionsDirectory);
        var oldPath = Path.Combine(sessionsDirectory, "old.jsonl");
        var recentPath = Path.Combine(sessionsDirectory, "recent.jsonl");
        WriteTreeSession(oldPath, "old prompt", "old session");
        WriteTreeSession(recentPath, "recent prompt", "recent session");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(recentPath, DateTime.UtcNow);

        var target = CodingAgentSessionTarget.Resolve(null, continueRecent: true, sessionDirectory: sessionsDirectory);
        var snapshot = target.LoadInitialSnapshot();

        Assert.Null(target.SessionStore);
        Assert.NotNull(target.TreeSessionController);
        Assert.True(target.PreferTreeSession);
        Assert.Equal(Path.GetFullPath(recentPath), target.TreeSessionController!.Path);
        Assert.Equal("recent session", snapshot.Name);
        var user = Assert.IsType<UserMessage>(Assert.Single(snapshot.Messages));
        Assert.Equal("recent prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public void Resolve_WithSessionDirectoryAndContinue_CreatesNewTreeSessionWhenNoneExist()
    {
        using var temp = TempDirectory.Create();
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");

        var target = CodingAgentSessionTarget.Resolve(null, continueRecent: true, sessionDirectory: sessionsDirectory);
        var snapshot = target.LoadInitialSnapshot();

        Assert.Null(target.SessionStore);
        Assert.NotNull(target.TreeSessionController);
        Assert.True(target.PreferTreeSession);
        Assert.StartsWith(Path.GetFullPath(sessionsDirectory), target.TreeSessionController!.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(target.TreeSessionController.Path));
        Assert.Empty(snapshot.Messages);
    }

    [Fact]
    public void Resolve_WithForkPath_CopiesSourceBranchIntoNewTreeSession()
    {
        using var temp = TempDirectory.Create();
        var previousCwd = Environment.CurrentDirectory;
        var previousSessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        var previousTreeSessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE");
        try
        {
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", null);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", null);
            Environment.CurrentDirectory = temp.Path;
            var sourcePath = Path.Combine(temp.Path, "source.jsonl");
            WriteTreeSession(sourcePath, "fork prompt", "fork source");

            var target = CodingAgentSessionTarget.Resolve(null, forkSessionPath: sourcePath);
            var snapshot = target.LoadInitialSnapshot();

            Assert.Null(target.SessionStore);
            Assert.NotNull(target.TreeSessionController);
            Assert.True(target.PreferTreeSession);
            Assert.NotEqual(Path.GetFullPath(sourcePath), target.TreeSessionController!.Path);
            Assert.StartsWith(
                Path.Combine(temp.Path, ".tau", "coding-agent-sessions"),
                target.TreeSessionController.Path,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("fork source", snapshot.Name);
            var user = Assert.IsType<UserMessage>(Assert.Single(snapshot.Messages));
            Assert.Equal("fork prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);

            var summary = target.TreeSessionController.GetSummary();
            Assert.Equal(Path.GetFullPath(sourcePath), summary.ParentSession);
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", previousSessionFile);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", previousTreeSessionFile);
        }
    }

    [Fact]
    public void Resolve_WithForkPathAndSessionDirectory_CopiesIntoRequestedDirectory()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "source.jsonl");
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");
        WriteTreeSession(sourcePath, "fork prompt", "fork source");

        var target = CodingAgentSessionTarget.Resolve(null, sessionDirectory: sessionsDirectory, forkSessionPath: sourcePath);
        var snapshot = target.LoadInitialSnapshot();

        Assert.NotNull(target.TreeSessionController);
        Assert.StartsWith(Path.GetFullPath(sessionsDirectory), target.TreeSessionController!.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fork source", snapshot.Name);
        Assert.Equal(Path.GetFullPath(sourcePath), target.TreeSessionController.GetSummary().ParentSession);
    }

    [Fact]
    public void Resolve_WithForkPathMissingSource_Throws()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "missing.jsonl");

        var ex = Assert.Throws<IOException>(() => CodingAgentSessionTarget.Resolve(null, forkSessionPath: sourcePath));

        Assert.Equal($"session file not found: {Path.GetFullPath(sourcePath)}", ex.Message);
    }

    [Fact]
    public void Resolve_WithForkSessionIdPrefix_CopiesMatchedTreeSession()
    {
        using var temp = TempDirectory.Create();
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessionsDirectory);
        var sourcePath = Path.Combine(sessionsDirectory, "source.jsonl");
        var sourceSummary = WriteTreeSession(sourcePath, "fork id prompt", "fork id source");
        var prefix = sourceSummary.SessionId[..Math.Min(20, sourceSummary.SessionId.Length)];

        var target = CodingAgentSessionTarget.Resolve(null, sessionDirectory: sessionsDirectory, forkSessionPath: prefix);
        var snapshot = target.LoadInitialSnapshot();

        Assert.NotNull(target.TreeSessionController);
        Assert.StartsWith(Path.GetFullPath(sessionsDirectory), target.TreeSessionController!.Path, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(sourcePath), target.TreeSessionController.Path);
        Assert.Equal("fork id source", snapshot.Name);
        var user = Assert.IsType<UserMessage>(Assert.Single(snapshot.Messages));
        Assert.Equal("fork id prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
        Assert.Equal(Path.GetFullPath(sourcePath), target.TreeSessionController.GetSummary().ParentSession);
    }

    private static Model CreateModel() => new()
    {
        Provider = "openai",
        Id = "gpt-5.4",
        Name = "GPT-5.4",
        Api = "openai-responses"
    };

    private static CodingAgentTreeSessionSummary WriteTreeSession(string path, string prompt, string name)
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        runner.SessionName = name;
        runner.MutableMessages.Add(new UserMessage([new TextContent(prompt)]));
        var controller = CodingAgentTreeSessionController.OpenOrCreate(path);
        controller.SyncFromRunner(runner);
        return controller.GetSummary();
    }

    private static async IAsyncEnumerable<AgentEvent> EmptyEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-session-target-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
