using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class MomSandboxAndToolsTests
{
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
}
