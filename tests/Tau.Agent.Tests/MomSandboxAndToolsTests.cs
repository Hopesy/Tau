using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class MomSandboxAndToolsTests
{
    [Fact]
    public void CommandLine_ParseStripsMomSwitchesAndPreservesHostArgs()
    {
        var parsed = MomCommandLine.Parse([
            "--once",
            "--Mom:Sandbox",
            "docker:mom-sandbox",
            "--validate-sandbox",
            "--urls",
            "http://127.0.0.1:5005"
        ]);

        Assert.True(parsed.RunOnce);
        Assert.True(parsed.ValidateSandbox);
        Assert.Equal([
            "--Mom:Sandbox",
            "docker:mom-sandbox",
            "--urls",
            "http://127.0.0.1:5005"
        ], parsed.HostArgs);
    }

    [Fact]
    public void Parse_MapsHostAndDockerWorkspacePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-sandbox-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "scratch", "note.txt");

        var host = MomSandboxExecutorFactory.Create(MomSandboxConfig.Parse("host"), root);
        Assert.Equal(MomSandboxKind.Host, host.Config.Kind);
        Assert.Equal(Path.GetFullPath(root), host.WorkspacePath);
        Assert.Equal(Path.GetFullPath(child), host.ToWorkspacePath(child));

        var docker = MomSandboxExecutorFactory.Create(MomSandboxConfig.Parse("docker:mom-sandbox"), root);
        Assert.Equal(MomSandboxKind.Docker, docker.Config.Kind);
        Assert.Equal("docker:mom-sandbox", docker.Config.DisplayName);
        Assert.Equal("/workspace", docker.WorkspacePath);
        Assert.Equal("/workspace/scratch/note.txt", docker.ToWorkspacePath(child));
        Assert.Equal(Path.GetFullPath(child), docker.ToHostPath("/workspace/scratch/note.txt"));

        Assert.Throws<ArgumentException>(() => MomSandboxConfig.Parse("docker:"));
        Assert.Throws<ArgumentException>(() => MomSandboxConfig.Parse("vm"));
    }

    [Fact]
    public async Task SandboxValidator_HostSucceedsWithoutProcessChecks()
    {
        var runner = new RecordingMomProcessRunner();

        var result = await MomSandboxValidator.ValidateAsync(new MomOptions { Sandbox = "host" }, runner);

        Assert.True(result.Succeeded);
        Assert.Equal("host", result.Sandbox);
        Assert.Contains("No Docker checks", result.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task SandboxValidator_DockerUsesValidationRunner()
    {
        var runner = new RecordingMomProcessRunner(
            new MomSandboxExecResult("Docker version 27.0.0", string.Empty, 0),
            new MomSandboxExecResult("true\n", string.Empty, 0));

        var result = await MomSandboxValidator.ValidateAsync(new MomOptions { Sandbox = "docker:mom-sandbox" }, runner);

        Assert.True(result.Succeeded);
        Assert.Equal("docker:mom-sandbox", result.Sandbox);
        Assert.Contains("container is running", result.Message, StringComparison.Ordinal);
        Assert.Collection(
            runner.Invocations,
            version => Assert.Equal(["--version"], version.Arguments),
            inspect => Assert.Equal(["inspect", "-f", "{{.State.Running}}", "mom-sandbox"], inspect.Arguments));
    }

    [Fact]
    public async Task SandboxValidator_ReturnsFailureForInvalidOrUnavailableDocker()
    {
        var invalid = await MomSandboxValidator.ValidateAsync(new MomOptions { Sandbox = "docker:" });

        Assert.False(invalid.Succeeded);
        Assert.Equal("docker:", invalid.Sandbox);
        Assert.Contains("requires a container name", invalid.Message, StringComparison.Ordinal);

        var runner = new RecordingMomProcessRunner(new MomSandboxExecResult(string.Empty, "missing", -1));
        var missingDocker = await MomSandboxValidator.ValidateAsync(new MomOptions { Sandbox = "docker:mom-sandbox" }, runner);

        Assert.False(missingDocker.Succeeded);
        Assert.Equal("docker:mom-sandbox", missingDocker.Sandbox);
        Assert.Contains("not installed or not in PATH", missingDocker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_DockerChecksVersionAndRunningContainer()
    {
        var runner = new RecordingMomProcessRunner(
            new MomSandboxExecResult("Docker version 27.0.0", string.Empty, 0),
            new MomSandboxExecResult("true\n", string.Empty, 0));

        await MomSandboxExecutorFactory.ValidateAsync(MomSandboxConfig.Parse("docker:mom-sandbox"), runner);

        Assert.Collection(
            runner.Invocations,
            version =>
            {
                Assert.Equal("docker", version.FileName);
                Assert.Equal(["--version"], version.Arguments);
                Assert.Null(version.WorkingDirectory);
                Assert.Null(version.TimeoutSeconds);
            },
            inspect =>
            {
                Assert.Equal("docker", inspect.FileName);
                Assert.Equal(["inspect", "-f", "{{.State.Running}}", "mom-sandbox"], inspect.Arguments);
                Assert.Null(inspect.WorkingDirectory);
                Assert.Null(inspect.TimeoutSeconds);
            });
    }

    [Fact]
    public async Task ValidateAsync_DockerRejectsStoppedContainer()
    {
        var runner = new RecordingMomProcessRunner(
            new MomSandboxExecResult("Docker version 27.0.0", string.Empty, 0),
            new MomSandboxExecResult("false\n", string.Empty, 0));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MomSandboxExecutorFactory.ValidateAsync(MomSandboxConfig.Parse("docker:mom-sandbox"), runner));

        Assert.Contains("is not running", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task ValidateAsync_DockerRejectsMissingContainer()
    {
        var runner = new RecordingMomProcessRunner(
            new MomSandboxExecResult("Docker version 27.0.0", string.Empty, 0),
            new MomSandboxExecResult(string.Empty, "No such object", 1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MomSandboxExecutorFactory.ValidateAsync(MomSandboxConfig.Parse("docker:mom-sandbox"), runner));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task ValidateAsync_DockerRejectsMissingDocker()
    {
        var runner = new RecordingMomProcessRunner(
            new MomSandboxExecResult(string.Empty, "Failed to start process: docker", -1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MomSandboxExecutorFactory.ValidateAsync(MomSandboxConfig.Parse("docker:mom-sandbox"), runner));

        Assert.Contains("not installed or not in PATH", error.Message, StringComparison.Ordinal);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("docker", invocation.FileName);
        Assert.Equal(["--version"], invocation.Arguments);
    }

    [Fact]
    public async Task DockerExecutor_ExecAsync_ConstructsDockerExecCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-docker-{Guid.NewGuid():N}");
        var runner = new RecordingMomProcessRunner(new MomSandboxExecResult("ok\n", string.Empty, 0));
        var executor = new DockerMomSandboxExecutor(MomSandboxConfig.Parse("docker:mom-sandbox"), root, runner);

        var result = await executor.ExecAsync("printf hi", new MomSandboxExecOptions(12));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok\n", result.Stdout);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("docker", invocation.FileName);
        Assert.Equal(["exec", "-w", "/workspace", "mom-sandbox", "sh", "-c", "printf hi"], invocation.Arguments);
        Assert.Null(invocation.WorkingDirectory);
        Assert.Equal(12, invocation.TimeoutSeconds);
    }

    [Fact]
    public async Task HostExecutor_ExecutesInWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var executor = MomSandboxExecutorFactory.Create(MomSandboxConfig.Parse("host"), root);
            var result = await executor.ExecAsync("echo mom-host", new MomSandboxExecOptions(10));

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("mom-host", result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.False(result.TimedOut);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MomTools_ReadWriteEditAndAttachInsideWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var attached = new List<(string Path, string? Title)>();
        try
        {
            var executor = MomSandboxExecutorFactory.Create(MomSandboxConfig.Parse("host"), root);
            var tools = MomToolSet.Create(executor, (path, title) => attached.Add((path, title)));

            Assert.Equal(["read", "bash", "edit", "write", "attach"], tools.Select(static tool => tool.Name));

            var write = tools.OfType<MomWriteTool>().Single();
            var read = tools.OfType<MomReadTool>().Single();
            var edit = tools.OfType<MomEditTool>().Single();
            var attach = tools.OfType<MomAttachTool>().Single();

            var writeResult = await ExecuteAsync(write, """
            {"label":"write note","path":"scratch/note.txt","content":"alpha beta"}
            """);
            Assert.False(writeResult.IsError);

            var readResult = await ExecuteAsync(read, """
            {"label":"read note","path":"scratch/note.txt","offset":1,"limit":1}
            """);
            Assert.False(readResult.IsError);
            Assert.Contains("alpha beta", Text(readResult), StringComparison.Ordinal);

            var editResult = await ExecuteAsync(edit, """
            {"label":"edit note","path":"scratch/note.txt","oldText":"alpha","newText":"gamma"}
            """);
            Assert.False(editResult.IsError);
            Assert.Equal("gamma beta", await File.ReadAllTextAsync(Path.Combine(root, "scratch", "note.txt")));

            var attachResult = await ExecuteAsync(attach, """
            {"label":"attach note","path":"scratch/note.txt","title":"note"}
            """);
            Assert.False(attachResult.IsError);
            var uploaded = Assert.Single(attached);
            Assert.Equal(Path.Combine(root, "scratch", "note.txt"), uploaded.Path);
            Assert.Equal("note", uploaded.Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MomTools_RejectPathsOutsideWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-tools-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"tau-mom-outside-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(outside, "outside");
        try
        {
            var executor = MomSandboxExecutorFactory.Create(MomSandboxConfig.Parse("host"), root);
            var read = new MomReadTool(executor);
            var result = await ExecuteAsync(read, $$"""
            {"label":"read outside","path":"{{outside.Replace("\\", "\\\\", StringComparison.Ordinal)}}"}
            """);

            Assert.True(result.IsError);
            Assert.Contains("outside the Mom workspace", Text(result), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public void TruncateTail_KeepsTailAndReportsTruncation()
    {
        var content = string.Join("\n", Enumerable.Range(1, 10).Select(static i => $"line-{i}"));

        var result = MomToolOutputTruncator.TruncateTail(content, maxLines: 3, maxBytes: 10_000);

        Assert.True(result.Truncated);
        Assert.Equal("lines", result.TruncatedBy);
        Assert.Equal(10, result.TotalLines);
        Assert.Equal(3, result.OutputLines);
        Assert.Equal("line-8\nline-9\nline-10", result.Content);
    }

    private static async Task<ToolResult> ExecuteAsync(IAgentTool tool, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await tool.ExecuteAsync("tool-1", document.RootElement, CancellationToken.None, null);
    }

    private static string Text(ToolResult result)
    {
        return string.Join("\n", result.Content.OfType<TextContent>().Select(static text => text.Text));
    }

    private sealed class RecordingMomProcessRunner : IMomSandboxProcessRunner
    {
        private readonly Queue<MomSandboxExecResult> _results;

        public RecordingMomProcessRunner(params MomSandboxExecResult[] results)
        {
            _results = new Queue<MomSandboxExecResult>(results);
        }

        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<MomSandboxExecResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            int? timeoutSeconds,
            CancellationToken cancellationToken)
        {
            Invocations.Add(new ProcessInvocation(fileName, arguments.ToArray(), workingDirectory, timeoutSeconds));
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No recorded process result is available.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed record ProcessInvocation(
        string FileName,
        string[] Arguments,
        string? WorkingDirectory,
        int? TimeoutSeconds);
}
