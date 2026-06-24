using System.Text;
using Tau.AgentCore.Harness;

namespace Tau.AgentCore.Tests;

public sealed class ToolOutputTruncatorTests
{
    [Fact]
    public void TruncateHead_CountsUtf8Bytes()
    {
        const string content = "a\u00E9\U0001F642\nb";

        var result = ToolOutputTruncator.TruncateHead(content, maxBytes: 100, maxLines: 10);

        Assert.False(result.Truncated);
        Assert.Equal(ByteLength(content), result.TotalBytes);
        Assert.Equal(ByteLength(content), result.OutputBytes);
        Assert.Equal(9, result.TotalBytes);
    }

    [Fact]
    public void TruncateHead_WithByteLimit_DoesNotReturnPartialLines()
    {
        var result = ToolOutputTruncator.TruncateHead("\u00E9\u00E9\nabc", maxBytes: 4, maxLines: 10);

        Assert.Equal("\u00E9\u00E9", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.Equal(4, result.OutputBytes);
        Assert.False(result.FirstLineExceedsLimit);
    }

    [Fact]
    public void TruncateHead_WhenFirstLineExceedsByteLimit_ReturnsEmptyContentWithFlag()
    {
        var result = ToolOutputTruncator.TruncateHead("\u00E9\u00E9\nabc", maxBytes: 3, maxLines: 10);

        Assert.Equal(string.Empty, result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.True(result.FirstLineExceedsLimit);
    }

    [Fact]
    public void TruncateTail_WithByteLimit_KeepsUtf8CharacterBoundaries()
    {
        var result = ToolOutputTruncator.TruncateTail("a\u00E9\U0001F642b", maxBytes: 5, maxLines: 10);

        Assert.Equal("\U0001F642b", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.True(result.LastLinePartial);
        Assert.Equal(5, result.OutputBytes);
    }

    [Fact]
    public void TruncateTail_WhenTrailingCharacterCannotFit_DropsIt()
    {
        var result = ToolOutputTruncator.TruncateTail("abc\U0001F642", maxBytes: 3, maxLines: 10);

        Assert.Equal(string.Empty, result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.True(result.LastLinePartial);
        Assert.Equal(0, result.OutputBytes);
    }

    [Fact]
    public void TruncateTail_WhenUnpairedSurrogateFits_ReplacesIt()
    {
        var result = ToolOutputTruncator.TruncateTail("a\uD83D", maxBytes: 3, maxLines: 10);

        Assert.Equal("\uFFFD", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.True(result.LastLinePartial);
        Assert.Equal(3, result.OutputBytes);
    }

    [Fact]
    public void TruncateLine_AddsUpstreamSuffix()
    {
        var result = ToolOutputTruncator.TruncateLine("abcdef", maxChars: 3);

        Assert.True(result.WasTruncated);
        Assert.Equal("abc... [truncated]", result.Text);
    }

    private static int ByteLength(string content) =>
        Encoding.UTF8.GetByteCount(content);
}
