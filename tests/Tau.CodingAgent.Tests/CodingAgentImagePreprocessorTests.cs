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
        Assert.Equal(2, GetPngColorType(result.Data));
    }

    [Fact]
    public void Process_EncodesOpaqueGrayscalePngAsGrayscale()
    {
        var bytes = ImageTestData.CreatePng(2501, 10, red: 0x80, green: 0x80, blue: 0x80);

        var result = CodingAgentImagePreprocessor.Process(bytes, "image/png", autoResizeImages: true);

        Assert.NotNull(result);
        Assert.True(result!.WasResized);
        Assert.Equal(0, GetPngColorType(result.Data));
    }

    [Fact]
    public void Process_PreservesAlphaWithRgbaPngEncoding()
    {
        var bytes = ImageTestData.CreatePng(2501, 10, alpha: 0x7f);

        var result = CodingAgentImagePreprocessor.Process(bytes, "image/png", autoResizeImages: true);

        Assert.NotNull(result);
        Assert.True(result!.WasResized);
        Assert.Equal(6, GetPngColorType(result.Data));
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

    private static int GetPngColorType(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        Assert.True(bytes.Length > 25);
        Assert.Equal((byte)'I', bytes[12]);
        Assert.Equal((byte)'H', bytes[13]);
        Assert.Equal((byte)'D', bytes[14]);
        Assert.Equal((byte)'R', bytes[15]);
        return bytes[25];
    }
}
