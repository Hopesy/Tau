// 作者：xxx
using System.Text;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class SystemCodingAgentClipboardTests
{
    /// <summary>【CodingAgent】【Windows 文本复制】中文和特殊字符通过 stdin 的 ASCII 封装传递，不进入命令参数。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task WindowsCopy_PreservesUnicodeWithoutPuttingTextInArguments()
    {
        const string text = "中文😀\n' $() `";
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("powershell.exe", [], [], true);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Windows);
        clipboard.TerminalOutput = new StringWriter();
        await clipboard.SetTextAsync(text);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(text, Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(command.Input!))));
        Assert.DoesNotContain(text, string.Join(" ", command.Arguments)); Assert.Equal(5000, command.TimeoutMs);
        Assert.Equal(string.Empty, clipboard.TerminalOutput.ToString());
    }

    /// <summary>【CodingAgent】【Windows 备用复制】原生写入失败时使用带 UTF-16 BOM 的 clip 输入。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task WindowsCopy_FallsBackToUnicodeClipInput()
    {
        var runner = new FakeClipboardCommandRunner(); runner.EnqueueFailure("powershell.exe", [], true); runner.Enqueue("clip.exe", [], []);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Windows);
        clipboard.TerminalOutput = new StringWriter();
        await clipboard.SetTextAsync("中文"); Assert.Equal([0xff, 0xfe, .. Encoding.Unicode.GetBytes("中文")], runner.Commands[1].Input);
    }

    /// <summary>【CodingAgent】【macOS 复制】pbcopy 接收完整 UTF-8 文本。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task MacCopy_UsesUtf8Pbcopy()
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("pbcopy", [], []);
        await CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.MacOS).SetTextAsync("中文\n");
        Assert.Equal(Encoding.UTF8.GetBytes("中文\n"), Assert.Single(runner.Commands).Input);
    }

    /// <summary>【CodingAgent】【Linux 复制回退】按环境顺序尝试工具，成功后停止并保持原始 UTF-8 字节。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task LinuxCopy_UsesEnvironmentOrderAndStopsOnSuccess()
    {
        var runner = new FakeClipboardCommandRunner(); runner.EnqueueFailure("termux-clipboard-set", []); runner.EnqueueFailure("wl-copy", []);
        runner.EnqueueFailure("xclip", ["-selection", "clipboard"]); runner.Enqueue("xsel", ["--clipboard", "--input"], []);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["TERMUX_VERSION"] = "1", ["WAYLAND_DISPLAY"] = "wayland", ["DISPLAY"] = ":0" }, CodingAgentClipboardPlatform.Linux);
        clipboard.TerminalOutput = new StringWriter();
        await clipboard.SetTextAsync("a\n中"); Assert.Equal(4, runner.Commands.Count);
        Assert.All(runner.Commands, command => Assert.Equal(Encoding.UTF8.GetBytes("a\n中"), command.Input));
        Assert.Equal(string.Empty, clipboard.TerminalOutput.ToString());
    }

    /// <summary>【CodingAgent】【远程复制】先更新本机剪贴板，再向远程客户端发 OSC 52。</summary>
    /// <param name="remoteVariable">远程会话标识。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("SSH_CONNECTION")]
    [InlineData("SSH_CLIENT")]
    [InlineData("MOSH_CONNECTION")]
    public async Task RemoteCopy_EmitsOsc52AfterSuccessfulNativeWrite(string remoteVariable)
    {
        using var output = new StringWriter();
        var runner = new FakeClipboardCommandRunner { OnRun = _ => Assert.Equal(string.Empty, output.ToString()) }; runner.Enqueue("wl-copy", [], []);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland", [remoteVariable] = "remote" }, CodingAgentClipboardPlatform.Linux);
        clipboard.TerminalOutput = output; await clipboard.SetTextAsync("中");
        Assert.Equal("\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes("中")) + "\u0007", output.ToString());
    }

    /// <summary>【CodingAgent】【无桌面复制】没有桌面工具时使用终端复制，包括空字符串。</summary>
    /// <param name="text">待复制文本。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("中😀")]
    public async Task HeadlessCopy_UsesOsc52WithoutStartingCommands(string text)
    {
        var runner = new FakeClipboardCommandRunner(); var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Linux);
        clipboard.TerminalOutput = new StringWriter(); await clipboard.SetTextAsync(text); Assert.Empty(runner.Commands);
        Assert.Equal("\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\u0007", clipboard.TerminalOutput.ToString());
    }

    /// <summary>【CodingAgent】【桌面复制失败】存在桌面环境时失败必须报告对应依赖，不能伪装成终端复制成功。</summary>
    /// <param name="environment">桌面环境标识。</param><param name="hint">期望错误提示。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("TERMUX_VERSION", "Termux:API")]
    [InlineData("WAYLAND_DISPLAY", "wl-clipboard")]
    [InlineData("DISPLAY", "xclip")]
    public async Task DesktopCopyFailure_ReportsToolsWithoutOsc52(string environment, string hint)
    {
        var runner = new FakeClipboardCommandRunner();
        if (environment == "TERMUX_VERSION") runner.EnqueueFailure("termux-clipboard-set", []);
        else if (environment == "WAYLAND_DISPLAY") runner.EnqueueFailure("wl-copy", []);
        else { runner.EnqueueFailure("xclip", ["-selection", "clipboard"]); runner.EnqueueFailure("xsel", ["--clipboard", "--input"]); }
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { [environment] = "set" }, CodingAgentClipboardPlatform.Linux);
        clipboard.TerminalOutput = new StringWriter();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => clipboard.SetTextAsync("text"));
        Assert.Contains(hint, error.Message); Assert.Equal(string.Empty, clipboard.TerminalOutput.ToString());
    }

    /// <summary>【CodingAgent】【终端复制上限】上限按 UTF-8 的 Base64 长度计算，超限前不写出任何序列。</summary>
    /// <param name="count">中文字符数量。</param><param name="success">是否位于上限内。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(25000, true)]
    [InlineData(25001, false)]
    public async Task Osc52_EnforcesEncodedByteBoundary(int count, bool success)
    {
        var clipboard = CreateClipboard(new(), new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Linux);
        clipboard.TerminalOutput = new StringWriter(); var text = new string('中', count);
        if (success) { await clipboard.SetTextAsync(text); Assert.Equal(100008, clipboard.TerminalOutput.ToString()!.Length); }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => clipboard.SetTextAsync(text));
            Assert.Contains("OSC 52 size limit", error.Message); Assert.Equal(string.Empty, clipboard.TerminalOutput.ToString());
        }
    }

    /// <summary>【CodingAgent】【远程超限回退】本机已成功复制时，远程 OSC 52 超限不撤销已成功结果。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task OversizedRemoteCopy_PreservesSuccessfulNativeResult()
    {
        var runner = new FakeClipboardCommandRunner(); runner.Enqueue("wl-copy", [], []);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland", ["SSH_CLIENT"] = "remote" }, CodingAgentClipboardPlatform.Linux);
        clipboard.TerminalOutput = new StringWriter(); await clipboard.SetTextAsync(new string('中', 25001));
        Assert.Equal(string.Empty, clipboard.TerminalOutput.ToString());
    }

    /// <summary>【CodingAgent】【WSL 终端优先】内核识别到 WSL 后，Windows Terminal 优先 OSC 52 且只发送一次。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task WslWindowsTerminal_UsesKernelDetectionAndEmitsOnlyOnce()
    {
        var runner = new FakeClipboardCommandRunner(); var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WT_SESSION"] = "terminal" }, CodingAgentClipboardPlatform.Linux);
        clipboard.KernelVersionReader = () => "Linux MICROSOFT WSL2"; clipboard.TerminalOutput = new StringWriter();
        await clipboard.SetTextAsync("text"); Assert.Empty(runner.Commands); Assert.Single(clipboard.TerminalOutput.ToString()!, character => character == '\u001b');
    }

    /// <summary>【CodingAgent】【WSL 文件传递】UTF-8 文件在命令期间可读，路径引号转义，完成后清理。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task WslCopy_UsesUtf8FileAndRemovesItAfterSuccess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-wsl-copy-test-{Guid.NewGuid():N}.txt");
        var runner = new FakeClipboardCommandRunner { OnRun = _ => Assert.Equal("中文😀", File.ReadAllText(path, Encoding.UTF8)) };
        runner.Enqueue("wslpath", ["-w", path], Encoding.UTF8.GetBytes("C:\\Temp\\o'quote.txt\n")); runner.Enqueue("powershell.exe", [], [], true);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WSLENV"] = "1" }, CodingAgentClipboardPlatform.Linux);
        clipboard.TempTextPathFactory = () => path; clipboard.TerminalOutput = new StringWriter();
        await clipboard.SetTextAsync("中文😀");
        Assert.False(File.Exists(path)); Assert.Contains("o''quote.txt", runner.Commands[1].Arguments.Last()); Assert.Null(runner.Commands[1].Input);
        Assert.Equal(string.Empty, clipboard.TerminalOutput.ToString());
    }

    /// <summary>【CodingAgent】【WSL 取消清理】命令取消向上传播，临时文件删除且不继续发送 OSC 52。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task WslCopy_CancellationCleansTemporaryFileWithoutFallback()
    {
        using var caller = new CancellationTokenSource();
        var path = Path.Combine(Path.GetTempPath(), $"tau-wsl-cancel-test-{Guid.NewGuid():N}.txt");
        var runner = new FakeClipboardCommandRunner { OnRun = _ => caller.Cancel() }; runner.EnqueueFailure("wslpath", ["-w", path]);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?> { ["WSLENV"] = "1" }, CodingAgentClipboardPlatform.Linux);
        clipboard.TempTextPathFactory = () => path; clipboard.TerminalOutput = new StringWriter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clipboard.SetTextAsync("text", caller.Token));
        Assert.False(File.Exists(path)); Assert.Equal(string.Empty, clipboard.TerminalOutput.ToString()); Assert.Single(runner.Commands);
    }

    /// <summary>【CodingAgent】【预取消复制】调用前取消不运行命令或写终端序列。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Copy_PrecancellationHasNoSideEffects()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var runner = new FakeClipboardCommandRunner(); var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Linux);
        clipboard.TerminalOutput = new StringWriter(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clipboard.SetTextAsync("text", caller.Token));
        Assert.Empty(runner.Commands); Assert.Equal(string.Empty, clipboard.TerminalOutput.ToString());
    }
}
