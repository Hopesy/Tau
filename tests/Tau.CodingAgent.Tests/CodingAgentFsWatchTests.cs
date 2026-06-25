using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentFsWatchTests
{
    [Fact]
    public void CloseWatcher_AllowsNull()
    {
        CodingAgentFsWatch.CloseWatcher(null);
    }

    [Fact]
    public void WatchWithErrorHandler_ReturnsNullAndReportsErrorWhenDirectoryIsMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "tau-missing-watch-" + Guid.NewGuid().ToString("N"), "file.txt");
        var errorCount = 0;

        var watcher = CodingAgentFsWatch.WatchWithErrorHandler(
            missingPath,
            (_, _) => { },
            () => errorCount++);

        Assert.Null(watcher);
        Assert.Equal(1, errorCount);
    }

    [Fact]
    public async Task WatchWithErrorHandler_WatchesSingleFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-fs-watch-" + Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(filePath, "{}");
        var changed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watcher = CodingAgentFsWatch.WatchWithErrorHandler(
            filePath,
            (_, fileName) => changed.TrySetResult(fileName),
            () => changed.TrySetException(new InvalidOperationException("watcher failed")));

        try
        {
            Assert.NotNull(watcher);
            await File.WriteAllTextAsync(filePath, "{\"updated\":true}");

            var fileName = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("settings.json", fileName);
        }
        finally
        {
            CodingAgentFsWatch.CloseWatcher(watcher);
            Directory.Delete(directory, recursive: true);
        }
    }
}
