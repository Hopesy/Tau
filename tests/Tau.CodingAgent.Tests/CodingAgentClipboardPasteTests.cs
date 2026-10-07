// 作者：xxx
using Tau.AgentCore;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public partial class CodingAgentHostTests
{
    /// <summary>【CodingAgent】【文件粘贴】文件优先于图片和文本，插入当前光标并补足分隔符，bash 路径正确引用。</summary>
    /// <param name="bash">是否 bash 草稿。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClipboardFiles_InsertAtCursorBeforeImagesOrText(bool bash)
    {
        var clipboard = new FakeCodingAgentClipboard { Files = ["/a b", "/o'quote"], Image = new([1], "image/png"), Text = "ignored" };
        var (host, ui, runner, _) = PasteHost(clipboard, bash ? "!catTAIL" : "beforeafter", bash ? 4 : 5);
        await host.RunAsync();
        Assert.Equal(bash ? "!cat '/a b' '/o'\\''quote' TAIL" : "before /a b\n/o'quote after", ui.GetDraft());
        Assert.Equal(["files"], clipboard.Reads); Assert.Empty(runner.Inputs); Assert.Empty(runner.ContentInputs);
    }

    /// <summary>【CodingAgent】【文本粘贴】没有文件或图片时读取文本，保持插入光标并规范化换行和制表符。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ClipboardText_FallsBackAndPreservesInsertionPosition()
    {
        var clipboard = new FakeCodingAgentClipboard { Text = "中文\r\n\ttext" };
        var (host, ui, runner, _) = PasteHost(clipboard, "ab", 1);
        await host.RunAsync(); Assert.Equal("a中文\n    textb", ui.GetDraft());
        Assert.Equal(["files", "image", "text"], clipboard.Reads); Assert.Empty(runner.Inputs);
    }

    /// <summary>【CodingAgent】【空内容粘贴】剪贴板没有可粘贴内容时保留原草稿且不触发模型。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task EmptyClipboard_PreservesDraft()
    {
        var (host, ui, runner, _) = PasteHost(new FakeCodingAgentClipboard(), "unchanged", 0);
        await host.RunAsync(); Assert.Equal("unchanged", ui.GetDraft()); Assert.Empty(runner.Inputs);
    }

    /// <summary>【CodingAgent】【非法路径粘贴】文件路径含控制字符时显示错误，不修改草稿，也不回退粘贴其他内容。</summary>
    /// <param name="path">含控制字符的路径。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("/a\nb")]
    [InlineData("/a\0b")]
    public async Task ClipboardFiles_RejectControlCharactersWithoutChangingDraft(string path)
    {
        var clipboard = new FakeCodingAgentClipboard { Files = [path], Text = "must not insert" };
        var (host, ui, runner, terminal) = PasteHost(clipboard, "original", 2);
        await host.RunAsync(); Assert.Equal("original", ui.GetDraft()); Assert.Empty(runner.Inputs);
        Assert.Equal(["files"], clipboard.Reads); Assert.Contains("Clipboard file path contains control characters", terminal.FlattenedText());
    }

    /// <summary>【CodingAgent】【图片提交边界】粘贴只插入保存后的图片路径，用户按 Enter 才发送该文本。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ClipboardImage_SendsPathOnlyAfterExplicitSubmission()
    {
        var bytes = ImageTestData.CreateJpeg(3, 5);
        var clipboard = new FakeCodingAgentClipboard { Image = new(bytes, "image/jpeg") };
        var (host, _, runner, _) = PasteHost(clipboard, "", 0, submit: true);
        var path = Path.Combine(Path.GetTempPath(), $"tau-clipboard-submit-{Guid.NewGuid():N}.jpg");
        host.ClipboardImagePathFactory = extension => { Assert.Equal("jpg", extension); Assert.Empty(runner.Inputs); return path; };
        try
        {
            await host.RunAsync(); Assert.Equal([path], runner.Inputs); Assert.Empty(runner.ContentInputs);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>【CodingAgent】【图片写入失败】临时目标已存在时不能覆盖，失败后保留草稿并报告错误。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ClipboardImage_WriteFailurePreservesDraftAndExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-clipboard-existing-{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "existing");
        var (host, ui, runner, terminal) = PasteHost(new FakeCodingAgentClipboard { Image = new([1], "image/png") }, "draft", 2);
        host.ClipboardImagePathFactory = _ => path;
        try
        {
            await host.RunAsync(); Assert.Equal("draft", ui.GetDraft()); Assert.Equal("existing", File.ReadAllText(path));
            Assert.Empty(runner.Inputs); Assert.Contains("Failed to paste from clipboard", terminal.FlattenedText());
        }
        finally { File.Delete(path); }
    }

    /// <summary>【CodingAgent】【粘贴宿主夹具】定位光标、执行粘贴，可选提交，最后退出输入循环。</summary>
    /// <param name="clipboard">剪贴板内容。</param><param name="draft">初始草稿。</param><param name="moveLeft">从末尾向左移动次数。</param>
    /// <param name="submit">是否按 Enter 提交。</param><returns>宿主及验证状态。</returns>
    private static (CodingAgentHost Host, InteractiveConsoleSession Ui, FakeCodingAgentRunner Runner, FakeTerminal Terminal)
        PasteHost(ICodingAgentClipboard clipboard, string draft, int moveLeft, bool submit = false)
    {
        var reader = new ScriptedKeyReader();
        for (var index = 0; index < moveLeft; index++) reader.EnqueueRaw(new('\0', ConsoleKey.LeftArrow, false, false, false));
        reader.EnqueueRaw(new('\u0016', ConsoleKey.V, false, false, true));
        if (submit) reader.EnqueueRaw(new('\r', ConsoleKey.Enter, false, false, false));
        reader.EnqueueRaw(new('\u0003', ConsoleKey.C, false, false, true));
        var editor = new InteractiveInputEditor(reader, new CapturingRenderer());
        var terminal = new FakeTerminal(); var ui = new InteractiveConsoleSession(terminal, editor); ui.SetDraft(draft);
        var runner = new FakeCodingAgentRunner((_, _) => AsyncEnumerable.Empty<AgentEvent>());
        return (new(ui, runner, clipboard: clipboard), ui, runner, terminal);
    }
}
