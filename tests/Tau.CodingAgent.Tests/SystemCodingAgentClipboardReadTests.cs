// 作者：xxx
using System.Text;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class SystemCodingAgentClipboardTests
{
    /// <summary>【CodingAgent】【文本工具回退】Linux 按环境依次尝试 Termux、Wayland、X11，保留成功输出的中文和空白。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ReadText_UsesEnvironmentPriorityAndPreservesWhitespace()
    {
        var runner = new FakeClipboardCommandRunner();
        runner.EnqueueFailure("termux-clipboard-get", []);
        runner.EnqueueFailure("wl-paste", ["--no-newline", "--type", "text"]);
        runner.EnqueueFailure("xclip", ["-selection", "clipboard", "-out"]);
        runner.Enqueue("xsel", ["--clipboard", "--output"], Encoding.UTF8.GetBytes(" 中文\r\n "));
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["TERMUX_VERSION"] = "1", ["WAYLAND_DISPLAY"] = "wayland", ["DISPLAY"] = ":0" }, CodingAgentClipboardPlatform.Linux);
        Assert.Equal(" 中文\r\n ", await clipboard.ReadTextAsync()); Assert.Equal(4, runner.Commands.Count);
    }

    /// <summary>【CodingAgent】【空剪贴板】工具成功但没有文本时不继续尝试其他桌面剪贴板。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task EmptyText_DoesNotFallThroughToAnotherClipboard()
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("wl-paste", ["--no-newline", "--type", "text"], []);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland", ["DISPLAY"] = ":0" }, CodingAgentClipboardPlatform.Linux);
        Assert.Null(await clipboard.ReadTextAsync()); Assert.Single(runner.Commands);
    }

    /// <summary>【CodingAgent】【Windows 中文】PowerShell 读取以 UTF-8 输出，不截断文本末尾换行。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task WindowsText_PreservesUnicodeAndUsesUtf8Output()
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("powershell.exe", [], Encoding.UTF8.GetBytes("中😀文\n"), true);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Windows);
        Assert.Equal("中😀文\n", await clipboard.ReadTextAsync());
        Assert.Contains("UTF8Encoding", runner.Commands.Single().Arguments.Last()); Assert.Contains("-STA", runner.Commands.Single().Arguments);
    }

    /// <summary>【CodingAgent】【macOS 文本】pbpaste 成功返回空输出时映射为无文本。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task MacText_EmptyMeansNoText()
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("pbpaste", [], []);
        Assert.Null(await CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.MacOS).ReadTextAsync());
    }

    /// <summary>【CodingAgent】【原生文件列表】桌面文件列表保留路径、顺序和空格。</summary>
    /// <param name="mac">是否 macOS。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFiles_ParseJsonArrayWithoutSplittingPaths(bool mac)
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue(mac ? "osascript" : "powershell.exe", [], Encoding.UTF8.GetBytes("""["/a/中文 x.txt","/b/o'quote.txt"]"""), true);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), mac ? CodingAgentClipboardPlatform.MacOS : CodingAgentClipboardPlatform.Windows);
        Assert.Equal(["/a/中文 x.txt", "/b/o'quote.txt"], await clipboard.ReadFilePathsAsync());
    }

    /// <summary>【CodingAgent】【WSL 文件】Windows 文件逐条转换为 Linux 路径，每条原路径作为独立命令参数。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task WslFiles_ConvertEachPathWithoutShellInterpolation()
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("powershell.exe", [], Encoding.UTF8.GetBytes("""["C:\\a b.txt","D:\\中文.txt"]"""), true);
        runner.Enqueue("wslpath", ["-u", "C:\\a b.txt"], Encoding.UTF8.GetBytes("/mnt/c/a b.txt\n"));
        runner.Enqueue("wslpath", ["-u", "D:\\中文.txt"], Encoding.UTF8.GetBytes("/mnt/d/中文.txt\n"));
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WSL_DISTRO_NAME"] = "Ubuntu" }, CodingAgentClipboardPlatform.Linux);
        Assert.Equal(["/mnt/c/a b.txt", "/mnt/d/中文.txt"], await clipboard.ReadFilePathsAsync());
    }

    /// <summary>【CodingAgent】【Linux 文件 URI】本地 URI 解码空格及中文，忽略注释和远程地址，Wayland 失败后回退 X11。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task LinuxFiles_DecodeLocalUrisAndFallbackToX11()
    {
        var runner = new FakeClipboardCommandRunner(); runner.EnqueueFailure("wl-paste", ["--no-newline", "--type", "text/uri-list"]);
        runner.Enqueue("xclip", ["-selection", "clipboard", "-t", "text/uri-list", "-o"], Encoding.UTF8.GetBytes(
            "# copied\r\nfile:///tmp/a%20b.txt\r\nhttps://example.test/a\r\nfile://remote/path\r\nfile://localhost/tmp/%E4%B8%AD.txt\r\n"));
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland", ["DISPLAY"] = ":0" }, CodingAgentClipboardPlatform.Linux);
        Assert.Equal(["/tmp/a b.txt", "/tmp/中.txt"], await clipboard.ReadFilePathsAsync());
    }

    /// <summary>【CodingAgent】【无效文件列表】平台返回非字符串数组或空结果时不插入伪造路径。</summary>
    /// <param name="json">平台输出。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("{}")]
    [InlineData("invalid")]
    public async Task InvalidNativeFileLists_ReturnNoFiles(string json)
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("powershell.exe", [], Encoding.UTF8.GetBytes(json), true);
        Assert.Null(await CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Windows).ReadFilePathsAsync());
    }

    /// <summary>【CodingAgent】【读取取消】读取前取消不启动命令，执行器返回失败时也不能吞掉调用者取消。</summary>
    /// <param name="cancelBefore">是否提前取消。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClipboardReads_PropagateCancellation(bool cancelBefore)
    {
        using var cancelled = new CancellationTokenSource();
        var runner = new FakeClipboardCommandRunner { OnRun = _ => cancelled.Cancel() };
        runner.EnqueueFailure("powershell.exe", [], true);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Windows);
        if (cancelBefore) cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clipboard.ReadTextAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clipboard.ReadFilePathsAsync(cancelled.Token));
        Assert.Equal(cancelBefore ? 0 : 1, runner.Commands.Count);
    }

    /// <summary>【CodingAgent】【无桌面读取】没有桌面环境和 WSL 时不盲目启动平台工具。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task HeadlessLinux_ReadsReturnNoDataWithoutCommands()
    {
        var runner = new FakeClipboardCommandRunner();
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Linux);
        Assert.Null(await clipboard.ReadTextAsync()); Assert.Null(await clipboard.ReadFilePathsAsync()); Assert.Empty(runner.Commands);
    }
}
