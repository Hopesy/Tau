using Tau.Ai;
using Tau.AgentCore;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

[Collection(nameof(CodingAgentSessionTargetEnvironmentCollection))]
public sealed class CodingAgentStartupResumeResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithoutResume_DoesNotSelectSession()
    {
        var result = await CodingAgentStartupResumeResolver.ResolveAsync(
            resume: false,
            explicitSessionPath: null,
            printMode: false,
            rpcMode: false,
            sessionDirectory: null,
            currentSessionPath: null,
            selector: (_, _) => throw new InvalidOperationException("selector should not run"));

        Assert.Null(result.SelectedPath);
        Assert.Null(result.ExitCode);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ResolveAsync_WithExplicitSession_DoesNotSelectSession()
    {
        var result = await CodingAgentStartupResumeResolver.ResolveAsync(
            resume: true,
            explicitSessionPath: "session.jsonl",
            printMode: false,
            rpcMode: false,
            sessionDirectory: null,
            currentSessionPath: null,
            selector: (_, _) => throw new InvalidOperationException("selector should not run"));

        Assert.Null(result.SelectedPath);
        Assert.Null(result.ExitCode);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ResolveAsync_InPrintMode_ReturnsInteractiveError()
    {
        var result = await CodingAgentStartupResumeResolver.ResolveAsync(
            resume: true,
            explicitSessionPath: null,
            printMode: true,
            rpcMode: false,
            sessionDirectory: null,
            currentSessionPath: null,
            selector: (_, _) => throw new InvalidOperationException("selector should not run"));

        Assert.Null(result.SelectedPath);
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.IsError);
        Assert.Equal("error: --resume requires an interactive terminal; use --session <path> or --continue.", result.Message);
    }

    [Fact]
    public async Task ResolveAsync_WithoutSessions_ExitsWithoutSelection()
    {
        using var temp = TempDirectory.Create();
        var previousCwd = Environment.CurrentDirectory;
        var previousSessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        var previousTreeSessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE");
        try
        {
            Environment.CurrentDirectory = temp.Path;
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", null);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", null);

            var result = await CodingAgentStartupResumeResolver.ResolveAsync(
                resume: true,
                explicitSessionPath: null,
                printMode: false,
                rpcMode: false,
                sessionDirectory: Path.Combine(temp.Path, "sessions"),
                currentSessionPath: Path.Combine(temp.Path, "current.jsonl"),
                selector: (_, _) => throw new InvalidOperationException("selector should not run"));

            Assert.Null(result.SelectedPath);
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.IsError);
            Assert.Equal("No session selected", result.Message);
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", previousSessionFile);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", previousTreeSessionFile);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenSelectorCancels_ExitsWithoutSelection()
    {
        using var temp = TempDirectory.Create();
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");
        var path = Path.Combine(sessionsDirectory, "session.jsonl");
        Directory.CreateDirectory(sessionsDirectory);
        WriteTreeSession(path, "resume prompt", "resume session");

        var result = await CodingAgentStartupResumeResolver.ResolveAsync(
            resume: true,
            explicitSessionPath: null,
            printMode: false,
            rpcMode: false,
            sessionDirectory: sessionsDirectory,
            currentSessionPath: Path.Combine(temp.Path, "current.jsonl"),
            selector: (_, _) => Task.FromResult(new CodingAgentResumeSelectionResult(null)));

        Assert.Null(result.SelectedPath);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("No session selected", result.Message);
    }

    [Fact]
    public async Task ResolveAsync_SelectsSessionFromExplicitSessionDirectory()
    {
        using var temp = TempDirectory.Create();
        var sessionsDirectory = Path.Combine(temp.Path, "sessions");
        var path = Path.Combine(sessionsDirectory, "session.jsonl");
        Directory.CreateDirectory(sessionsDirectory);
        WriteTreeSession(path, "resume prompt", "resume session");
        CodingAgentResumeSelectorState? capturedState = null;

        var result = await CodingAgentStartupResumeResolver.ResolveAsync(
            resume: true,
            explicitSessionPath: null,
            printMode: false,
            rpcMode: false,
            sessionDirectory: sessionsDirectory,
            currentSessionPath: Path.Combine(temp.Path, "current.jsonl"),
            selector: (state, _) =>
            {
                capturedState = state;
                return Task.FromResult(new CodingAgentResumeSelectionResult(path));
            });

        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(result.SelectedPath!));
        Assert.Null(result.ExitCode);
        Assert.NotNull(capturedState);
        Assert.Contains(capturedState!.Sessions, session => session.FilePath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteTreeSession(string path, string prompt, string name)
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        runner.SessionName = name;
        runner.MutableMessages.Add(new UserMessage([new TextContent(prompt)]));
        var controller = CodingAgentTreeSessionController.OpenOrCreate(path);
        controller.SyncFromRunner(runner);
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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-startup-resume-{Guid.NewGuid():N}");
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
