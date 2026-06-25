using Tau.AgentCore.Harness.Env;

namespace Tau.AgentCore.Tests;

public sealed class AgentExecutionEnvTests
{
    [Fact]
    public void PathHelpers_ResolveRelativePathsFromWorkingDirectory()
    {
        using var temp = TempDirectory.Create();
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));

        var absolute = env.GetAbsolutePath(Path.Combine("nested", "file.txt"));
        var joined = env.JoinPath("one", "two", "three.txt");

        Assert.Equal(Path.Combine(temp.Path, "nested", "file.txt"), absolute);
        Assert.Equal(Path.Combine("one", "two", "three.txt"), joined);
        Assert.Equal(temp.Path, env.GetAbsolutePath(temp.Path));
    }

    [Fact]
    public async Task FileOperations_RoundTripTextBinaryDirectoriesAndTempEntries()
    {
        using var temp = TempDirectory.Create();
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));

        await env.WriteFileAsync("nested/sample.txt", "one\n");
        await env.AppendFileAsync("nested/sample.txt", "two\nthree\n");
        await env.WriteFileAsync("nested/blob.bin", [1, 2, 3]);
        await env.CreateDirectoryAsync("nested/child");

        Assert.Equal("one\ntwo\nthree\n", await env.ReadTextFileAsync("nested/sample.txt"));
        Assert.Equal(["one", "two"], await env.ReadTextLinesAsync("nested/sample.txt", maxLines: 2));
        Assert.Equal([1, 2, 3], await env.ReadBinaryFileAsync("nested/blob.bin"));
        Assert.True(await env.ExistsAsync("nested/sample.txt"));
        Assert.False(await env.ExistsAsync("nested/missing.txt"));

        var fileInfo = await env.GetFileInfoAsync("nested/sample.txt");
        Assert.Equal("sample.txt", fileInfo.Name);
        Assert.Equal(AgentFileKind.File, fileInfo.Kind);
        Assert.True(fileInfo.Size > 0);
        Assert.True(fileInfo.ModifiedTimeMilliseconds > 0);

        var entries = await env.ListDirectoryAsync("nested");
        Assert.Contains(entries, entry => entry.Name == "sample.txt" && entry.Kind == AgentFileKind.File);
        Assert.Contains(entries, entry => entry.Name == "child" && entry.Kind == AgentFileKind.Directory);

        var canonical = await env.GetCanonicalPathAsync("nested/sample.txt");
        Assert.Equal(Path.Combine(temp.Path, "nested", "sample.txt"), canonical);

        var tempDir = await env.CreateTempDirectoryAsync("tau-env-");
        var tempFile = await env.CreateTempFileAsync("prefix-", ".txt");
        Assert.True(Directory.Exists(tempDir));
        Assert.True(File.Exists(tempFile));
        Assert.StartsWith("prefix-", Path.GetFileName(tempFile), StringComparison.Ordinal);
        Assert.EndsWith(".txt", tempFile, StringComparison.Ordinal);

        await env.RemoveAsync("nested/blob.bin");
        await env.RemoveAsync("nested/child", recursive: true);
        await env.RemoveAsync("nested/missing.txt", force: true);
        Assert.False(await env.ExistsAsync("nested/blob.bin"));
        Assert.False(await env.ExistsAsync("nested/child"));

        Directory.Delete(tempDir, recursive: true);
        Directory.Delete(Path.GetDirectoryName(tempFile)!, recursive: true);
    }

    [Fact]
    public async Task FileOperations_MapMissingPathToFileError()
    {
        using var temp = TempDirectory.Create();
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));

        var error = await Assert.ThrowsAsync<AgentFileException>(() => env.ReadTextFileAsync("missing.txt"));

        Assert.Equal(AgentFileErrorCode.NotFound, error.Code);
        Assert.Equal(Path.Combine(temp.Path, "missing.txt"), error.FilePath);
    }

    [Fact]
    public async Task FileOperations_CanonicalPathRequiresExistingTarget()
    {
        using var temp = TempDirectory.Create();
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));

        var error = await Assert.ThrowsAsync<AgentFileException>(() => env.GetCanonicalPathAsync("missing.txt"));

        Assert.Equal(AgentFileErrorCode.NotFound, error.Code);
    }

    [Fact]
    public async Task CreateDirectoryAsync_WhenNotRecursiveRequiresExistingParent()
    {
        using var temp = TempDirectory.Create();
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));

        var error = await Assert.ThrowsAsync<AgentFileException>(() =>
            env.CreateDirectoryAsync(Path.Combine("missing-parent", "child"), recursive: false));

        Assert.Equal(AgentFileErrorCode.NotFound, error.Code);
    }

    [Fact]
    public async Task ExecAsync_RunsCommandAndStreamsStdoutAndStderr()
    {
        using var temp = TempDirectory.Create();
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));
        var stdout = new List<string>();
        var stderr = new List<string>();

        var result = await env.ExecAsync(
            "printf 'out'; printf 'err' >&2",
            new AgentExecutionOptions(OnStdout: stdout.Add, OnStderr: stderr.Add));

        Assert.Equal("out", result.Stdout);
        Assert.Equal("err", result.Stderr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out", string.Concat(stdout));
        Assert.Equal("err", string.Concat(stderr));
    }

    [Fact]
    public async Task ExecAsync_UsesRelativeWorkingDirectoryAndEnvironment()
    {
        using var temp = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, "work"));
        File.WriteAllText(Path.Combine(temp.Path, "work", "marker.txt"), "marker");
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));

        var result = await env.ExecAsync(
            "test -f marker.txt && printf '%s' \"$TAU_ENV_TEST\"",
            new AgentExecutionOptions(
                WorkingDirectory: "work",
                Environment: new Dictionary<string, string?> { ["TAU_ENV_TEST"] = "present" }));

        Assert.Equal("present", result.Stdout);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecAsync_ThrowsTimeoutError()
    {
        using var temp = TempDirectory.Create();
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));

        var error = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            env.ExecAsync("sleep 5", new AgentExecutionOptions(Timeout: TimeSpan.FromMilliseconds(100))));

        Assert.Equal(AgentExecutionErrorCode.Timeout, error.Code);
    }

    [Fact]
    public async Task ExecAsync_ThrowsCallbackError()
    {
        using var temp = TempDirectory.Create();
        var env = new SystemAgentExecutionEnv(new AgentExecutionEnvOptions(temp.Path));

        var error = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            env.ExecAsync("printf 'out'", new AgentExecutionOptions(OnStdout: _ => throw new InvalidOperationException("boom"))));

        Assert.Equal(AgentExecutionErrorCode.CallbackError, error.Code);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tau-agentcore-env-" + Guid.NewGuid().ToString("N"));
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
