// 作者：xxx
using System.Text;
using SixLabors.ImageSharp;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class SystemCodingAgentClipboardTests
{
    /// <summary>【CodingAgent】【空 Wayland 图片】后端成功但没有图片时，不读取 X11 或原生后端的旧图片。</summary>
    /// <param name="types">可用 MIME 类型。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("")]
    [InlineData("text/plain\n")]
    public async Task EmptyWaylandImage_DoesNotReadStaleFallbacks(string types)
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("wl-paste", ["--list-types"], Encoding.UTF8.GetBytes(types));
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland" }, CodingAgentClipboardPlatform.Linux);
        clipboard.NativeImageReader = _ => throw new InvalidOperationException("native backend must not be read");
        Assert.Null(await clipboard.ReadImageAsync()); Assert.Single(runner.Commands);
    }

    /// <summary>【CodingAgent】【图片空字节】声明图片类型但读取成功为空时仍表示空剪贴板。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task EmptyWaylandImageData_DoesNotFallback()
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("wl-paste", ["--list-types"], Encoding.UTF8.GetBytes("image/png\n"));
        runner.Enqueue("wl-paste", ["--type", "image/png", "--no-newline"], []);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["XDG_SESSION_TYPE"] = "wayland" }, CodingAgentClipboardPlatform.Linux);
        Assert.Null(await clipboard.ReadImageAsync()); Assert.Equal(2, runner.Commands.Count);
    }

    /// <summary>【CodingAgent】【后端失败回退】Wayland 命令失败时才读取 X11 声明的图片类型。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task UnavailableWayland_FallsBackToX11()
    {
        var bytes = ImageTestData.CreatePng(2, 3);
        var runner = new FakeClipboardCommandRunner(); runner.EnqueueFailure("wl-paste", ["--list-types"]);
        runner.Enqueue("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"], Encoding.UTF8.GetBytes("image/png\n"));
        runner.Enqueue("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"], bytes);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland" }, CodingAgentClipboardPlatform.Linux);
        Assert.Equal(bytes, (await clipboard.ReadImageAsync())!.Bytes); Assert.Equal(3, runner.Commands.Count);
    }

    /// <summary>【CodingAgent】【X11 空图片】没有声明图片类型时不探测未声明类型，也不触发原生回退。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task EmptyX11Image_DoesNotProbeOtherFormatsOrNativeBackend()
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"], Encoding.UTF8.GetBytes("text/plain"));
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Linux);
        clipboard.NativeImageReader = _ => throw new InvalidOperationException("native backend must not be read");
        Assert.Null(await clipboard.ReadImageAsync()); Assert.Single(runner.Commands);
    }

    /// <summary>【CodingAgent】【原生图片回退】声明类型读取失败时进入原生后端，不盲目尝试其他 MIME。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task FailedX11ImageData_UsesNativeFallback()
    {
        var bytes = ImageTestData.CreateJpeg(2, 3); var nativeCalls = 0;
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"], Encoding.UTF8.GetBytes("image/png\nimage/jpeg"));
        runner.EnqueueFailure("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Linux);
        clipboard.NativeImageReader = _ => { nativeCalls++; return Task.FromResult<byte[]?>(bytes); };
        var image = await clipboard.ReadImageAsync(); Assert.Equal("image/jpeg", image!.MimeType); Assert.Equal(bytes, image.Bytes);
        Assert.Equal(1, nativeCalls); Assert.Equal(2, runner.Commands.Count);
    }

    /// <summary>【CodingAgent】【WSL 空状态】Windows 没有图片时保留 Linux 的空状态，不额外读 X11 或原生后端。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task WslEmptyImage_PreservesLinuxEmptyStateAfterWindowsFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-wsl-empty-{Guid.NewGuid():N}.png");
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("wl-paste", ["--list-types"], Encoding.UTF8.GetBytes("text/plain"));
        runner.Enqueue("wslpath", ["-w", path], Encoding.UTF8.GetBytes("C:\\Temp\\image.png")); runner.Enqueue("powershell.exe", [], Encoding.UTF8.GetBytes("empty"), true);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WSLENV"] = "1" }, CodingAgentClipboardPlatform.Linux, () => path);
        clipboard.NativeImageReader = _ => throw new InvalidOperationException("native backend must not be read");
        Assert.Null(await clipboard.ReadImageAsync()); Assert.Equal(3, runner.Commands.Count);
    }

    /// <summary>【CodingAgent】【WSL 不可用状态】两个 Linux 后端与 Windows 读取均不可用后，仍允许原生回退。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task WslUnavailableImage_ReachesNativeFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-wsl-unavailable-{Guid.NewGuid():N}.png");
        var bytes = ImageTestData.CreatePng(3, 4);
        var runner = new FakeClipboardCommandRunner(); runner.EnqueueFailure("wl-paste", ["--list-types"]);
        runner.EnqueueFailure("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"]); runner.EnqueueFailure("wslpath", ["-w", path]);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WSLENV"] = "1" }, CodingAgentClipboardPlatform.Linux, () => path);
        clipboard.NativeImageReader = _ => Task.FromResult<byte[]?>(bytes);
        Assert.Equal(bytes, (await clipboard.ReadImageAsync())!.Bytes); Assert.Equal(3, runner.Commands.Count);
    }

    /// <summary>【CodingAgent】【macOS 图片】AppKit 路径返回 Base64 图片，解码后按真实字节识别 MIME。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task MacImage_DecodesAppKitResult()
    {
        var bytes = ImageTestData.CreatePng(3, 4);
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("osascript", [], Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes) + "\n"), true);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.MacOS);
        var image = await clipboard.ReadImageAsync(); Assert.Equal(bytes, image!.Bytes); Assert.Equal("image/png", image.MimeType);
        Assert.Contains("NSPasteboardTypePNG", runner.Commands.Single().Arguments.Last()); Assert.Contains("TIFFRepresentation", runner.Commands.Single().Arguments.Last());
    }

    /// <summary>【CodingAgent】【macOS 空结果】无图片或无效输出不产生伪图片。</summary>
    /// <param name="output">平台输出。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("not-base64")]
    public async Task MacImage_EmptyOrInvalidOutputMeansNoImage(string output)
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("osascript", [], Encoding.UTF8.GetBytes(output), true);
        Assert.Null(await CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.MacOS).ReadImageAsync());
    }

    /// <summary>【CodingAgent】【未知 MIME 图片】原生 TIFF 无支持 MIME 时仍尝试转换 PNG。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task NativeUnknownImageMime_ConvertsToPng()
    {
        using var image = Image.Load(ImageTestData.CreatePng(3, 4)); using var encoded = new MemoryStream(); image.SaveAsTiff(encoded);
        var clipboard = CreateClipboard(new(), new Dictionary<string, string?>(), CodingAgentClipboardPlatform.MacOS);
        clipboard.NativeImageReader = _ => Task.FromResult<byte[]?>(encoded.ToArray());
        var result = await clipboard.ReadImageAsync(); Assert.Equal("image/png", result!.MimeType);
        Assert.Equal("image/png", CodingAgentImageMimeDetector.DetectSupportedImageMimeType(result.Bytes));
    }

    /// <summary>【CodingAgent】【图片取消】平台调用返回后再次检查取消，不进入其他后端。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ImageCancellation_DoesNotStartFallbacks()
    {
        using var caller = new CancellationTokenSource();
        var runner = new FakeClipboardCommandRunner { OnRun = _ => caller.Cancel() }; runner.EnqueueFailure("wl-paste", ["--list-types"]);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland" }, CodingAgentClipboardPlatform.Linux);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clipboard.ReadImageAsync(caller.Token)); Assert.Single(runner.Commands);
    }
}
