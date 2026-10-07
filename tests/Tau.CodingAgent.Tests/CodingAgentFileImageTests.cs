// 作者：xxx
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Tau.Ai;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.CodingAgent.Tools;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【文件图片回归】验证文件魔数、格式转换及模型上下文隔离。</summary>
public sealed class CodingAgentFileImageTests
{
    /// <summary>【CodingAgent】【BMP 嗅探】合法核心头、扩展头及所有支持位深均识别为图片。</summary>
    /// <param name="headerSize">DIB 头大小。</param><param name="depth">像素位深。</param>
    [Theory] [InlineData(12, 1)] [InlineData(40, 4)] [InlineData(40, 8)] [InlineData(108, 16)] [InlineData(124, 24)] [InlineData(40, 32)]
    public void Mime_AcceptsBmpHeaders(int headerSize, int depth) =>
        Assert.Equal("image/bmp", CodingAgentImageMimeDetector.DetectSupportedImageMimeType(BmpHeader(headerSize, depth)));

    /// <summary>【CodingAgent】【BMP 拒绝】拒绝文件边界、头部结构和平面位深不一致的内容。</summary>
    /// <param name="field">损坏字段偏移。</param><param name="value">损坏值。</param>
    [Theory] [InlineData(2, 25)] [InlineData(10, 53)] [InlineData(10, 512)] [InlineData(14, 13)] [InlineData(26, 2)] [InlineData(28, 3)]
    public void Mime_RejectsMalformedBmp(int field, int value)
    {
        var bytes = BmpHeader(40, 24);
        if (field < 26) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(field), (uint)value);
        else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(field), (ushort)value);
        Assert.Null(CodingAgentImageMimeDetector.DetectSupportedImageMimeType(bytes));
    }

    /// <summary>【CodingAgent】【GIF 严格魔数】普通 GIF 前缀文本及短头部不能误判成图片。</summary>
    /// <param name="text">文件前缀。</param>
    [Theory] [InlineData("GIF")] [InlineData("GIF text document")] [InlineData("GIF90a")]
    public void Mime_RejectsNonGifPrefix(string text) =>
        Assert.Null(CodingAgentImageMimeDetector.DetectSupportedImageMimeType(System.Text.Encoding.ASCII.GetBytes(text)));

    /// <summary>【CodingAgent】【BMP 文件入口】read 与命令行附件都转换 BMP，即使关闭缩放也保留转换说明。</summary>
    /// <param name="resize">是否开启自动缩放。</param><returns>异步回归任务。</returns>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FileImages_ConvertBmpForReadAndAttachments(bool resize)
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-file-image-" + Guid.NewGuid().ToString("N") + ".bin");
        using var source = new Image<Rgba32>(40, 20); using var memory = new MemoryStream(); source.SaveAsBmp(memory);
        await File.WriteAllBytesAsync(path, memory.ToArray());
        try
        {
            var result = await ExecuteReadAsync(new ReadFileTool(resize, resizeOptions: new() { MaxWidth = 10, MaxHeight = 10 }), path);
            var image = Assert.Single(result.Content.OfType<ImageContent>());
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(new TuiImageDimensions(resize ? 10 : 40, resize ? 5 : 20), TuiTerminalImage.GetPngDimensions(image.Data));
            Assert.Contains("converted from image/bmp to image/png", Assert.Single(result.Content.OfType<TextContent>()).Text);
            var details = Assert.IsType<ReadFileToolDetails>(result.Details);
            Assert.Equal(memory.Length, details.ImageBytes); Assert.Equal(resize, details.ImageResized);
            var initial = await CodingAgentInitialMessageBuilder.BuildAsync([], [path], options: new(AutoResizeImages: resize));
            Assert.Equal("image/png", Assert.Single(initial!.Images).MimeType);
            Assert.Contains("converted from image/bmp to image/png", initial.Text);
            var blocked = await CodingAgentInitialMessageBuilder.BuildAsync([], [path], options: new(BlockImages: true));
            Assert.Empty(blocked!.Images); Assert.Contains("Image blocked by settings", blocked.Text);
        }
        finally { File.Delete(path); }
    }

    /// <summary>【CodingAgent】【读取上下文隔离】共用原始工具的会话使用独立模型策略，模型切换和设置变更即时生效。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task ReadImages_UseLiveModelAndSettingsWithoutRebindingSharedTool()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-read-profile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "image.png"); await File.WriteAllBytesAsync(path, ImageTestData.CreatePng(40, 20));
        try
        {
            var shared = new ReadFileTool(workingDirectory: directory);
            var catalog = new ModelCatalog();
            var small = new Model { Provider = "synthetic", Id = "small", Name = "Small", Api = "test",
                InputLimits = new() { Images = new() { Resize = new() { MaxWidth = 10, MaxHeight = 10 } } } };
            catalog.RegisterModel(small); catalog.RegisterModel(small with { Id = "large", InputModalities = ["text", "image"],
                InputLimits = new() { Images = new() { Resize = new() { MaxWidth = 20, MaxHeight = 20 } } } });
            var first = RuntimeCodingAgentRunner.Create("synthetic", "small", toolsOverride: [shared], modelCatalogOverride: catalog, workingDirectory: directory, apiKey: "synthetic");
            var second = RuntimeCodingAgentRunner.Create("synthetic", "large", toolsOverride: [shared], modelCatalogOverride: catalog, workingDirectory: directory, apiKey: "synthetic");
            var firstTool = Assert.IsType<ReadFileTool>(Assert.Single(first.GetRegisteredTools()));
            var secondTool = Assert.IsType<ReadFileTool>(Assert.Single(second.GetRegisteredTools()));
            Assert.NotSame(firstTool, secondTool); Assert.NotSame(shared, firstTool);
            var results = await Task.WhenAll(ExecuteReadAsync(firstTool, path), ExecuteReadAsync(secondTool, path));
            Assert.Equal(10, Assert.IsType<ReadFileToolDetails>(results[0].Details).Width);
            Assert.Equal(20, Assert.IsType<ReadFileToolDetails>(results[1].Details).Width);
            Assert.Contains("Current model does not support images", Assert.Single(results[0].Content.OfType<TextContent>()).Text);
            Assert.DoesNotContain("Current model does not support images", Assert.Single(results[1].Content.OfType<TextContent>()).Text);
            first.SelectModel("synthetic", "large");
            Assert.Equal(20, Assert.IsType<ReadFileToolDetails>((await ExecuteReadAsync(firstTool, path)).Details).Width);
            var settings = new CodingAgentSettingsStore(Path.Combine(directory, "settings.json"));
            settings.Save(new(null, null, ImagesAutoResize: false)); first.ConfigureSessionSettings(settings);
            Assert.Equal(40, Assert.IsType<ReadFileToolDetails>((await ExecuteReadAsync(firstTool, path)).Details).Width);
            Assert.Equal(40, Assert.IsType<ReadFileToolDetails>((await ExecuteReadAsync(shared, path)).Details).Width);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>【CodingAgent】【BMP 夹具】构造足够用于魔数检测的文件头。</summary>
    /// <param name="headerSize">DIB 大小。</param><param name="depth">位深。</param><returns>头部字节。</returns>
    private static byte[] BmpHeader(int headerSize, int depth)
    {
        var bytes = new byte[30]; bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)(14 + headerSize));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), (uint)headerSize);
        var offset = headerSize == 12 ? 22 : 26;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), (ushort)depth);
        return bytes;
    }

    /// <summary>【CodingAgent】【读取夹具】用真实工具解析器执行文件读取。</summary>
    /// <param name="tool">文件工具。</param><param name="path">绝对路径。</param><returns>工具结果。</returns>
    private static Task<Tau.AgentCore.ToolResult> ExecuteReadAsync(ReadFileTool tool, string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(new JsonObject { ["path"] = path }.ToJsonString());
        return tool.ExecuteAsync("read", document.RootElement.Clone(), default, null);
    }
}
