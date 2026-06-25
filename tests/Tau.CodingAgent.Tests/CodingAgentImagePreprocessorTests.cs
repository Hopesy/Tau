using Tau.CodingAgent.Runtime;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentImagePreprocessorTests
{
    [Fact]
    public async Task ResizeWorker_ProcessAsync_ResizesWithoutChangingImageSemantics()
    {
        var bytes = ImageTestData.CreatePng(2501, 10);

        var result = await CodingAgentImageResizeWorker.Default.ProcessAsync(bytes, "image/png", autoResizeImages: true);

        Assert.NotNull(result);
        Assert.True(result!.WasResized);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(2501, result.OriginalWidth);
        Assert.Equal(10, result.OriginalHeight);
        Assert.Equal(2000, result.Width);
        Assert.Equal(8, result.Height);
        Assert.Equal(new TuiImageDimensions(2000, 8), TuiTerminalImage.GetPngDimensions(result.Data));
    }

    [Fact]
    public async Task ResizeWorker_ProcessAsync_CopiesInputBytesBeforeWorkerRuns()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new CodingAgentImageResizeWorker((bytes, _, _, _, _, _) =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test worker was not released.");
            }

            Assert.Equal([1, 2, 3], bytes);
            return new CodingAgentImagePreprocessResult(
                "copy",
                "image/png",
                OriginalWidth: 1,
                OriginalHeight: 1,
                Width: 1,
                Height: 1,
                WasResized: false,
                OriginalBytes: 3,
                EstimatedBase64Bytes: 4);
        });
        var input = new byte[] { 1, 2, 3 };

        var task = worker.ProcessAsync(input, "image/png", autoResizeImages: false);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        input[0] = 9;
        release.Set();
        var result = await task;

        Assert.NotNull(result);
        Assert.Equal("copy", result!.Data);
    }

    [Fact]
    public async Task ResizeWorker_ProcessAsync_WhenCanceledBeforeStartDoesNotRunWorker()
    {
        var called = false;
        var worker = new CodingAgentImageResizeWorker((_, _, _, _, _, _) =>
        {
            called = true;
            return null;
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => worker.ProcessAsync([1, 2, 3], "image/png", autoResizeImages: true, cancellationToken: cts.Token));

        Assert.False(called);
    }

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
    public void Process_ResizesLargeJpegImages()
    {
        var bytes = ImageTestData.CreateJpeg(2501, 10);

        var result = CodingAgentImagePreprocessor.Process(
            bytes,
            "image/jpeg",
            autoResizeImages: true,
            maxWidth: 100,
            maxHeight: 100);

        Assert.NotNull(result);
        Assert.True(result!.WasResized);
        Assert.Equal(2501, result.OriginalWidth);
        Assert.Equal(10, result.OriginalHeight);
        Assert.Equal(100, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Contains(result.MimeType, new[] { "image/png", "image/jpeg" });
        Assert.Equal(new TuiImageDimensions(100, 1), TuiTerminalImage.GetImageDimensions(result.Data, result.MimeType));
    }

    [Fact]
    public void Process_ResizesLargeWebpImages()
    {
        var bytes = ImageTestData.CreateWebp(400, 300);

        var result = CodingAgentImagePreprocessor.Process(
            bytes,
            "image/webp",
            autoResizeImages: true,
            maxWidth: 100,
            maxHeight: 100);

        Assert.NotNull(result);
        Assert.True(result!.WasResized);
        Assert.Equal(400, result.OriginalWidth);
        Assert.Equal(300, result.OriginalHeight);
        Assert.Equal(100, result.Width);
        Assert.Equal(75, result.Height);
        Assert.Contains(result.MimeType, new[] { "image/png", "image/jpeg" });
        Assert.Equal(new TuiImageDimensions(100, 75), TuiTerminalImage.GetImageDimensions(result.Data, result.MimeType));
    }

    [Fact]
    public void Process_UsesJpegFallbackWhenPngCandidateExceedsLimit()
    {
        var bytes = ImageTestData.CreatePng(512, 512, noisy: true);

        var result = CodingAgentImagePreprocessor.Process(
            bytes,
            "image/png",
            autoResizeImages: true,
            maxWidth: 128,
            maxHeight: 128,
            maxBase64Bytes: 20_000);

        Assert.NotNull(result);
        Assert.True(result!.WasResized);
        Assert.Equal("image/jpeg", result.MimeType);
        Assert.Equal(128, result.Width);
        Assert.Equal(128, result.Height);
        Assert.True(result.EstimatedBase64Bytes < 20_000);
        Assert.Equal(new TuiImageDimensions(128, 128), TuiTerminalImage.GetJpegDimensions(result.Data));
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
