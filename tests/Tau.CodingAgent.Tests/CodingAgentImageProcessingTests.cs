// 作者：xxx
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【内联图片回归】验证格式、预算、失败保留与内容身份。</summary>
public sealed class CodingAgentImageProcessingTests
{
    /// <summary>【CodingAgent】【格式归一】MIME 别名和参数正确处理，受支持图片保持原始编码。</summary>
    /// <param name="mime">原 MIME。</param><param name="expected">规范化 MIME。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData("IMAGE/PNG; charset=binary", "image/png")] [InlineData("image/jpg", "image/jpeg")]
    public async Task Process_NormalizesMimeWithoutChangingImageBytes(string mime, string expected)
    {
        var bytes = expected == "image/png" ? ImageTestData.CreatePng(4, 2) : ImageTestData.CreateJpeg(4, 2);
        var image = new ImageContent(Convert.ToBase64String(bytes), mime);
        var result = await CodingAgentImageProcessing.ProcessAsync(image, true, null, default);
        Assert.Equal(expected, result.Image!.MimeType); Assert.Equal(image.Data, result.Image.Data); Assert.Empty(result.Hints);
    }

    /// <summary>【CodingAgent】【格式转换】关闭自动缩放仍将 BMP 转换为 PNG，并提示原始格式。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Process_ConvertsUnsupportedInlineFormatWithResizeDisabled()
    {
        using var source = new Image<Rgba32>(12, 6); using var bytes = new MemoryStream(); source.SaveAsBmp(bytes);
        var result = await CodingAgentImageProcessing.ProcessAsync(new(Convert.ToBase64String(bytes.ToArray()), "image/bmp"), false, new() { MaxWidth = 2 }, default);
        Assert.Equal("image/png", result.Image!.MimeType);
        Assert.Equal(new TuiImageDimensions(12, 6), TuiTerminalImage.GetPngDimensions(result.Image.Data));
        Assert.Equal(["[Image converted from image/bmp to image/png.]"], result.Hints);
    }

    /// <summary>【CodingAgent】【GIF 缩放】超出模型尺寸的 GIF 可转换为受限的内联图片。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Process_ResizesGifUsingModelProfile()
    {
        using var source = new Image<Rgba32>(40, 20); using var bytes = new MemoryStream(); source.SaveAsGif(bytes);
        var result = await CodingAgentImageProcessing.ProcessAsync(new(Convert.ToBase64String(bytes.ToArray()), "image/gif"), true, new() { MaxWidth = 10, MaxHeight = 10 }, default);
        Assert.NotNull(result.Image);
        Assert.Equal(new TuiImageDimensions(10, 5), TuiTerminalImage.GetImageDimensions(result.Image.Data, result.Image.MimeType));
        Assert.Contains("original 40x20, displayed at 10x5", Assert.Single(result.Hints));
    }

    /// <summary>【CodingAgent】【JPEG 质量】模型首选质量实际参与编码，预算约束下不会固定为默认质量。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task Process_AppliesJpegQualityAndEncodedByteBudget()
    {
        var image = new ImageContent(Convert.ToBase64String(ImageTestData.CreatePng(512, 512, noisy: true)), "image/png");
        var profile = new ModelImageResizeOptions { MaxWidth = 128, MaxHeight = 128, MaxBytes = 20000, JpegQuality = 15 };
        var low = await CodingAgentImageProcessing.ProcessAsync(image, true, profile, default);
        var high = await CodingAgentImageProcessing.ProcessAsync(image, true, profile with { JpegQuality = 80 }, default);
        Assert.Equal("image/jpeg", low.Image!.MimeType); Assert.Equal("image/jpeg", high.Image!.MimeType);
        Assert.True(low.Image.Data.Length < high.Image.Data.Length); Assert.True(high.Image.Data.Length < 20000);
        Assert.Equal(new TuiImageDimensions(128, 128), TuiTerminalImage.GetJpegDimensions(low.Image.Data));
    }

    /// <summary>【CodingAgent】【失败策略】坏图片在提示中省略，工具结果保留原始图片和列表身份。</summary>
    /// <param name="mime">坏图片 MIME。</param><param name="hint">期望提示。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData("image/png", "resized")] [InlineData("image/unknown", "converted")]
    public async Task Process_OmitsBadPromptImageAndPreservesBadToolImage(string mime, string hint)
    {
        var image = new ImageContent("YWJj", mime);
        var result = await CodingAgentImageProcessing.ProcessAsync(image, true, null, default);
        Assert.Null(result.Image); Assert.Contains(hint, Assert.Single(result.Hints));
        ContentBlock[] content = [new TextContent("caption"), image];
        Assert.Same(content, await CodingAgentImageProcessing.NormalizeToolResultAsync(content, true, null, default));
    }

    /// <summary>【CodingAgent】【稳定内容】合法小图片不重写内容列表，转换提示紧随图片且只生成一次。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task ToolImages_KeepUnchangedIdentityAndDoNotRepeatHints()
    {
        ContentBlock[] content = [new TextContent("before"), new ImageContent(Convert.ToBase64String(ImageTestData.CreatePng(40, 20)), "image/png"), new TextContent("after")];
        var profile = new ModelImageResizeOptions { MaxWidth = 10, MaxHeight = 10 };
        var once = await CodingAgentImageProcessing.NormalizeToolResultAsync(content, true, profile, default);
        Assert.Equal(4, once.Count); Assert.IsType<ImageContent>(once[1]); Assert.Contains("displayed at 10x5", Assert.IsType<TextContent>(once[2]).Text);
        Assert.Same(once, await CodingAgentImageProcessing.NormalizeToolResultAsync(once, true, profile, default));
        Assert.Same(content[0], once[0]); Assert.Same(content[2], once[3]);
    }

    /// <summary>【CodingAgent】【工作器取消】处理已开始后取消等待，后台完成不会篡改调用方字节。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task ResizeWorker_CancellationEndsWaitAfterProcessingStarted()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var worker = new CodingAgentImageResizeWorker((_, _, _, _, _, _) =>
        { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); return null; });
        using var cancellation = new CancellationTokenSource();
        try
        {
            var running = worker.ProcessAsync([1, 2, 3], "image/png", true, cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); }
    }
}
