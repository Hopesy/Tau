using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Tests;

public sealed class ReadFileToolTests
{
    [Fact]
    public async Task ExecuteAsync_UsesOneIndexedOffsetAndLimit()
    {
        var path = await CreateTempFileAsync(
            "one",
            "two",
            "three",
            "four");

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path, offset: 2, limit: 2);

            Assert.False(result.IsError);
            Assert.Equal("two\nthree\n\n[1 more lines in file. Use offset=4 to continue.]", Text(result));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithZeroOffset_StartsAtFirstLine()
    {
        var path = await CreateTempFileAsync("first", "second");

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path, offset: 0, limit: 1);

            Assert.False(result.IsError);
            Assert.Equal("first\n\n[1 more lines in file. Use offset=2 to continue.]", Text(result));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenOffsetIsBeyondEnd_ReturnsError()
    {
        var path = await CreateTempFileAsync("one", "two");

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path, offset: 3);

            Assert.True(result.IsError);
            Assert.Equal("Offset 3 is beyond end of file (2 lines total)", Text(result));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenFileMissing_KeepsExistingError()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-read-missing-" + Guid.NewGuid().ToString("N") + ".txt");

        var result = await ExecuteAsync(new ReadFileTool(), path);

        Assert.True(result.IsError);
        Assert.Equal($"File not found: {path}", Text(result));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLineLimitIsExceeded_AddsUpstreamContinuationAndDetails()
    {
        var path = await CreateTempFileAsync(Enumerable.Range(1, 2001).Select(static i => $"line {i}").ToArray());

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path);
            var output = Text(result);

            Assert.False(result.IsError);
            Assert.Contains("line 1", output, StringComparison.Ordinal);
            Assert.Contains("line 2000", output, StringComparison.Ordinal);
            Assert.DoesNotContain("line 2001\n", output, StringComparison.Ordinal);
            Assert.Contains("[Showing lines 1-2000 of 2001. Use offset=2001 to continue.]", output, StringComparison.Ordinal);
            var details = Assert.IsType<ReadFileToolDetails>(result.Details);
            var truncation = Assert.IsType<ToolOutputTruncationResult>(details.Truncation);
            Assert.True(truncation.Truncated);
            Assert.Equal("lines", truncation.TruncatedBy);
            Assert.Equal(2001, truncation.TotalLines);
            Assert.Equal(2000, truncation.OutputLines);
            Assert.Equal("text", details.Kind);
            Assert.Equal(1, details.StartLine);
            Assert.Equal(2000, details.EndLine);
            Assert.Equal(2001, details.TotalLines);
            Assert.True(details.HasMore);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenByteLimitIsExceeded_AddsUpstreamContinuationAndDetails()
    {
        var path = await CreateTempFileAsync(
            Enumerable.Range(1, 500)
                .Select(static i => $"line {i}: {new string('x', 200)}")
                .ToArray());

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path);
            var output = Text(result);

            Assert.False(result.IsError);
            Assert.Contains("line 1:", output, StringComparison.Ordinal);
            Assert.Contains("of 500 (50.0KB limit). Use offset=", output, StringComparison.Ordinal);
            var details = Assert.IsType<ReadFileToolDetails>(result.Details);
            var truncation = Assert.IsType<ToolOutputTruncationResult>(details.Truncation);
            Assert.True(truncation.Truncated);
            Assert.Equal("bytes", truncation.TruncatedBy);
            Assert.True(truncation.OutputBytes <= ToolOutputTruncator.DefaultMaxBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenFirstLineExceedsByteLimit_ReturnsBashFallbackNotice()
    {
        var path = await CreateTempFileAsync(new string('x', ToolOutputTruncator.DefaultMaxBytes + 1), "second");

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path);

            Assert.False(result.IsError);
            Assert.Contains("exceeds 50.0KB limit", Text(result), StringComparison.Ordinal);
            Assert.Contains("Use bash: sed -n '1p'", Text(result), StringComparison.Ordinal);
            var details = Assert.IsType<ReadFileToolDetails>(result.Details);
            var truncation = Assert.IsType<ToolOutputTruncationResult>(details.Truncation);
            Assert.True(truncation.FirstLineExceedsLimit);
            Assert.Equal(1, details.StartLine);
            Assert.Equal(1, details.EndLine);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenTextFileRead_AddsRenderMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-read-file-" + Guid.NewGuid().ToString("N") + ".cs");
        await File.WriteAllTextAsync(path, "namespace Demo;\npublic sealed class Example;\n");

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path);

            Assert.False(result.IsError);
            Assert.Equal("namespace Demo;\npublic sealed class Example;\n", Text(result));
            var details = Assert.IsType<ReadFileToolDetails>(result.Details);
            Assert.Equal(path, details.Path);
            Assert.Equal("text", details.Kind);
            Assert.Equal("csharp", details.Language);
            Assert.Equal(1, details.StartLine);
            Assert.Equal(3, details.EndLine);
            Assert.Equal(3, details.TotalLines);
            Assert.False(details.HasMore);
            Assert.Null(details.Truncation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AcceptsUpstreamFilePathAlias()
    {
        var path = await CreateTempFileAsync("alias");

        try
        {
            var result = await ExecuteWithFilePathAliasAsync(new ReadFileTool(), path);

            Assert.False(result.IsError);
            Assert.Equal("alias", Text(result));
            var details = Assert.IsType<ReadFileToolDetails>(result.Details);
            Assert.Equal(path, details.Path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenImageFileDetected_ReturnsImageAttachment()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
        var path = await CreateTempFileAsync(".txt", bytes);

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path);

            Assert.False(result.IsError);
            Assert.Equal("Read image file [image/png]", Text(result));
            var image = Assert.Single(result.Content.OfType<ImageContent>());
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(Convert.ToBase64String(bytes), image.Data);
            var details = Assert.IsType<ReadFileToolDetails>(result.Details);
            Assert.Equal("image", details.Kind);
            Assert.Equal("image/png", details.MimeType);
            Assert.False(details.ImageOmitted);
            Assert.True(details.ImageBytes > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenImageExceedsInlineLimit_OmitsAttachment()
    {
        var bytes = new byte[((ReadFileTool.DefaultMaxImageBase64Bytes + 3) / 4 * 3)];
        Convert.FromHexString("89504E470D0A1A0A").CopyTo(bytes);
        var path = await CreateTempFileAsync(".png", bytes);

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path);

            Assert.False(result.IsError);
            Assert.Contains("Read image file [image/png]", Text(result), StringComparison.Ordinal);
            Assert.Contains("inline image size limit", Text(result), StringComparison.Ordinal);
            Assert.Empty(result.Content.OfType<ImageContent>());
            var details = Assert.IsType<ReadFileToolDetails>(result.Details);
            Assert.Equal("image", details.Kind);
            Assert.Equal("image/png", details.MimeType);
            Assert.True(details.ImageOmitted);
            Assert.True(details.EstimatedBase64Bytes >= ReadFileTool.DefaultMaxImageBase64Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenImageExtensionHasTextContent_TreatsFileAsText()
    {
        var path = await CreateTempFileAsync(".png", System.Text.Encoding.UTF8.GetBytes("not an image"));

        try
        {
            var result = await ExecuteAsync(new ReadFileTool(), path);

            Assert.False(result.IsError);
            Assert.Equal("not an image", Text(result));
            Assert.Empty(result.Content.OfType<ImageContent>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> CreateTempFileAsync(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-read-file-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines));
        return path;
    }

    private static async Task<string> CreateTempFileAsync(string extension, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-read-file-" + Guid.NewGuid().ToString("N") + extension);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static async Task<ToolResult> ExecuteAsync(
        ReadFileTool tool,
        string path,
        int? offset = null,
        int? limit = null)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            if (offset.HasValue)
            {
                writer.WriteNumber("offset", offset.Value);
            }

            if (limit.HasValue)
            {
                writer.WriteNumber("limit", limit.Value);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return await tool.ExecuteAsync("tool-1", document.RootElement, CancellationToken.None, null);
    }

    private static async Task<ToolResult> ExecuteWithFilePathAliasAsync(
        ReadFileTool tool,
        string path)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("file_path", path);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return await tool.ExecuteAsync("tool-1", document.RootElement, CancellationToken.None, null);
    }

    private static string Text(ToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(static text => text.Text));
}
