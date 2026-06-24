using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Tests;

public sealed class ListDirectoryToolTests
{
    [Fact]
    public async Task ExecuteAsync_FormatsEntriesLikeUpstreamLsTool()
    {
        var directory = CreateTempDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "beta"));
            await File.WriteAllTextAsync(Path.Combine(directory, "Alpha.txt"), "alpha");
            await File.WriteAllTextAsync(Path.Combine(directory, ".env"), "secret");

            var result = await ExecuteAsync(new ListDirectoryTool(), directory);

            Assert.False(result.IsError);
            Assert.Equal(".env\nAlpha.txt\nbeta/", Text(result));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenLimitIsReached_AddsContinuationNotice()
    {
        var directory = CreateTempDirectory();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "a.txt"), "a");
            await File.WriteAllTextAsync(Path.Combine(directory, "b.txt"), "b");
            await File.WriteAllTextAsync(Path.Combine(directory, "c.txt"), "c");

            var result = await ExecuteAsync(new ListDirectoryTool(), directory, limit: 2);

            Assert.False(result.IsError);
            Assert.Equal("a.txt\nb.txt\n\n[2 entries limit reached. Use limit=4 for more]", Text(result));
            var details = Assert.IsType<ListDirectoryToolDetails>(result.Details);
            Assert.Equal(2, details.EntryLimitReached);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenDirectoryIsEmpty_ReturnsUpstreamEmptyText()
    {
        var directory = CreateTempDirectory();

        try
        {
            var result = await ExecuteAsync(new ListDirectoryTool(), directory);

            Assert.False(result.IsError);
            Assert.Equal("(empty directory)", Text(result));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathIsFile_ReturnsNotDirectoryError()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-ls-file-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "content");

        try
        {
            var result = await ExecuteAsync(new ListDirectoryTool(), path);

            Assert.True(result.IsError);
            Assert.Equal($"Not a directory: {path}", Text(result));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathIsMissing_ReturnsPathNotFoundError()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-ls-missing-" + Guid.NewGuid().ToString("N"));

        var result = await ExecuteAsync(new ListDirectoryTool(), path);

        Assert.True(result.IsError);
        Assert.Equal($"Path not found: {path}", Text(result));
    }

    [Fact]
    public async Task ExecuteAsync_WhenByteLimitIsExceeded_AddsTruncationNoticeAndDetails()
    {
        var directory = CreateTempDirectory();

        try
        {
            var longSegment = new string('x', 96);
            for (var i = 0; i < 500; i++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(directory, $"{i:000}-{longSegment}.txt"),
                    "content");
            }

            var result = await ExecuteAsync(new ListDirectoryTool(), directory);
            var output = Text(result);

            Assert.False(result.IsError);
            Assert.Contains("[50.0KB limit reached]", output, StringComparison.Ordinal);
            var details = Assert.IsType<ListDirectoryToolDetails>(result.Details);
            Assert.NotNull(details.Truncation);
            Assert.True(details.Truncation.Truncated);
            Assert.Equal("bytes", details.Truncation.TruncatedBy);
            Assert.True(details.Truncation.OutputBytes <= ToolOutputTruncator.DefaultMaxBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-ls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<ToolResult> ExecuteAsync(
        ListDirectoryTool tool,
        string path,
        int? limit = null)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            if (limit.HasValue)
            {
                writer.WriteNumber("limit", limit.Value);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return await tool.ExecuteAsync("tool-1", document.RootElement, CancellationToken.None, null);
    }

    private static string Text(ToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(static text => text.Text));
}
