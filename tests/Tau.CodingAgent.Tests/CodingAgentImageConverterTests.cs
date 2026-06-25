using Tau.CodingAgent.Runtime;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentImageConverterTests
{
    [Fact]
    public void ConvertToPng_WhenAlreadyPngReturnsOriginalData()
    {
        var bytes = ImageTestData.CreatePng(3, 5);
        var base64 = Convert.ToBase64String(bytes);

        var converted = CodingAgentImageConverter.ConvertToPng(base64, "image/png");

        Assert.NotNull(converted);
        Assert.Equal("image/png", converted!.MimeType);
        Assert.Equal(base64, converted.Data);
    }

    [Fact]
    public void ConvertToPng_ConvertsJpegToPng()
    {
        var bytes = ImageTestData.CreateJpeg(11, 7);

        var converted = CodingAgentImageConverter.ConvertToPng(Convert.ToBase64String(bytes), "image/jpeg");

        Assert.NotNull(converted);
        Assert.Equal("image/png", converted!.MimeType);
        Assert.Equal(new TuiImageDimensions(11, 7), TuiTerminalImage.GetPngDimensions(converted.Data));
    }

    [Fact]
    public void ConvertToPng_ConvertsWebpToPng()
    {
        var bytes = ImageTestData.CreateWebp(13, 17);

        var converted = CodingAgentImageConverter.ConvertToPng(Convert.ToBase64String(bytes), "image/webp");

        Assert.NotNull(converted);
        Assert.Equal("image/png", converted!.MimeType);
        Assert.Equal(new TuiImageDimensions(13, 17), TuiTerminalImage.GetPngDimensions(converted.Data));
    }

    [Fact]
    public void ConvertToPng_WhenBase64OrImageIsInvalidReturnsNull()
    {
        Assert.Null(CodingAgentImageConverter.ConvertToPng("not-base64", "image/jpeg"));
        Assert.Null(CodingAgentImageConverter.ConvertToPng(Convert.ToBase64String([1, 2, 3]), "image/jpeg"));
    }
}
