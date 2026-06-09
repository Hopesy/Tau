using Tau.CodingAgent.Runtime;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentImagePreprocessorTests
{
    [Fact]
    public void Process_ResizesPngPastMaxDimensions()
    {
        var bytes = ImageTestData.CreatePng(2501, 10);

        var result = CodingAgentImagePreprocessor.Process(bytes, "image/png", autoResizeImages: true);

        Assert.NotNull(result);
        Assert.True(result!.WasResized);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(2501, result.OriginalWidth);
        Assert.Equal(10, result.OriginalHeight);
        Assert.Equal(2000, result.Width);
        Assert.Equal(8, result.Height);
        Assert.True(result.EstimatedBase64Bytes < CodingAgentImagePreprocessor.DefaultMaxBase64Bytes);
        Assert.Equal(new TuiImageDimensions(2000, 8), TuiTerminalImage.GetPngDimensions(result.Data));
        Assert.Equal(
            "[Image: original 2501x10, displayed at 2000x8. Multiply coordinates by 1.25 to map to original image.]",
            CodingAgentImagePreprocessor.FormatDimensionNote(result));
    }

    [Fact]
    public void Process_WhenAutoResizeDisabledKeepsOriginalPngDimensions()
    {
        var bytes = ImageTestData.CreatePng(2501, 10);

        var result = CodingAgentImagePreprocessor.Process(bytes, "image/png", autoResizeImages: false);

        Assert.NotNull(result);
        Assert.False(result!.WasResized);
        Assert.Equal(2501, result.Width);
        Assert.Equal(10, result.Height);
        Assert.Equal(Convert.ToBase64String(bytes), result.Data);
    }

    [Fact]
    public void Process_UsesJpegExifOrientationForDimensionMetadata()
    {
        var bytes = ImageTestData.CreateJpegWithExifOrientation(width: 20, height: 10, orientation: 6);

        var result = CodingAgentImagePreprocessor.Process(bytes, "image/jpeg", autoResizeImages: true);

        Assert.NotNull(result);
        Assert.False(result!.WasResized);
        Assert.Equal(10, result.OriginalWidth);
        Assert.Equal(20, result.OriginalHeight);
        Assert.Equal(10, result.Width);
        Assert.Equal(20, result.Height);
        Assert.Equal(Convert.ToBase64String(bytes), result.Data);
    }
}
