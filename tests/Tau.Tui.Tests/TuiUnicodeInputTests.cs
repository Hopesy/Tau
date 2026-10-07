// 作者：xxx
using System.Text;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

[Collection(TuiTestCollections.TuiKeyDecoderState)]
public sealed class TuiUnicodeInputTests
{
    /// <summary>【Tui】【完整 Unicode 输入】原始、Kitty 和 modifyOtherKeys 输入保留非 BMP 标量及一次提交的组合文本。</summary>
    /// <param name="raw">原始输入。</param><param name="expected">期望文本。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("😀", "😀")]
    [InlineData("\u001b[128512u", "😀")]
    [InlineData("\u001b[27;2;128512~", "😀")]
    [InlineData("e\u0301", "e\u0301")]
    [InlineData("👩🏽‍💻中文", "👩🏽‍💻中文")]
    public async Task Editor_InsertsCompleteTextAsOneInput(string raw, string expected)
    {
        var renderer = new UnicodeRenderer();
        using var reader = new TuiDecodedKeyReader(new UnicodeRawReader(raw, "\r"));
        var editor = new InteractiveInputEditor(reader, renderer);
        Assert.Equal(expected, (await editor.ReadLineAsync("> ")).Text);
        Assert.All(renderer.Frames, frame => Assert.True(IsValidUtf16(frame)));
    }

    /// <summary>【Tui】【字节分包解码】原始终端流在任意 UTF-8 边界读取时仍保持中文、表情和粘贴完整。</summary>
    /// <param name="bufferSize">每次读取字节数。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task RawStream_DecodesUtf8AcrossReadBoundaries(int bufferSize)
    {
        const string expected = "中文😀👩🏽‍💻粘贴𠮷";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("中文😀👩🏽‍💻\u001b[200~粘贴𠮷\u001b[201~\r"));
        using var reader = new TuiDecodedKeyReader(new TuiStreamRawInputReader(stream, bufferSize: bufferSize));
        var renderer = new UnicodeRenderer(); var editor = new InteractiveInputEditor(reader, renderer);
        Assert.Equal(expected, (await editor.ReadLineAsync("> ")).Text);
        Assert.All(renderer.Frames, frame => Assert.True(IsValidUtf16(frame)));
    }

    /// <summary>【Tui】【代理对分帧】分开的字符串输入必须等待低代理字符，完整代理对只发布一次。</summary>
    [Fact]
    public void SequenceBuffer_KeepsSurrogatePairInOneEvent()
    {
        using var buffer = new TuiInputSequenceBuffer(Timeout.InfiniteTimeSpan); var events = new List<string>(); buffer.Data += events.Add;
        buffer.Process("\ud83d"); Assert.Empty(events);
        buffer.Process("\ude00x"); Assert.Equal(["😀", "x"], events); Assert.Equal(string.Empty, buffer.Pending);
    }

    /// <summary>【Tui】【Unicode 撤销】一次输入法提交形成一个撤销单元。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task UnicodeText_UndoRemovesEntireInputEvent()
    {
        using var reader = new TuiDecodedKeyReader(new UnicodeRawReader("a", "👩🏽‍💻中文", "\u001f", "\r"));
        var editor = new InteractiveInputEditor(reader, new UnicodeRenderer());
        Assert.Equal("a", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【字素删除】光标移动和向前向后删除保持表情组合完整。</summary>
    /// <param name="operation">删除操作的原始序列。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("\u007f")]
    [InlineData("\u001b[3~")]
    public async Task UnicodeEditing_DeletesWholeGrapheme(string operation)
    {
        var events = operation == "\u007f" ? new[] { "a", "👩🏽‍💻", "b", "\u001b[D", operation, "\r" }
            : new[] { "a", "👩🏽‍💻", "b", "\u001b[D", "\u001b[D", operation, "\r" };
        using var reader = new TuiDecodedKeyReader(new UnicodeRawReader(events));
        var renderer = new UnicodeRenderer(); var editor = new InteractiveInputEditor(reader, renderer);
        Assert.Equal("ab", (await editor.ReadLineAsync("> ")).Text);
        Assert.All(renderer.Frames, frame => Assert.True(IsValidUtf16(frame)));
    }

    /// <summary>【Tui】【非法文本隔离】代理残片、释放事件、未知控制序列和带控制修饰的表情不能作为普通文本插入。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task InvalidOrModifiedUnicode_DoesNotLeakIntoDraft()
    {
        using var reader = new TuiDecodedKeyReader(new UnicodeRawReader("\ud83d", "\ude00", "\u001b[128512;5u", "\u001b[128512;9u",
            "\u001b[128512;1:3u", "\u001b]0;title\a", "x", "\r"));
        var editor = new InteractiveInputEditor(reader, new UnicodeRenderer());
        Assert.Equal("x", (await editor.ReadLineAsync("> ")).Text);
        Assert.False(TuiConsoleKeyInfoMapper.TryMapInput("\u001b[128512u", out _));
    }

    /// <summary>【Tui】【历史搜索文本】搜索接收完整表情，退格删除整个字素后可继续输入匹配关键字。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ReverseSearch_UsesCompleteTextAndGraphemeBackspace()
    {
        var history = new InputHistory(); history.Add("选择中文"); history.Add("选择👩🏽‍💻");
        using var reader = new TuiDecodedKeyReader(new UnicodeRawReader("\u0012", "👩🏽‍💻", "\u007f", "中文", "\r"));
        var editor = new InteractiveInputEditor(reader, new UnicodeRenderer(), history: history);
        Assert.Equal("选择中文", (await editor.ReadLineAsync("> ")).Text);
    }

    /// <summary>【Tui】【旧字符读取】旧按键消费者仍可顺序获得代理对，不能丢弃非 BMP 文本。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task LegacyKeyReader_ReturnsBothSurrogates()
    {
        using var reader = new TuiDecodedKeyReader(new UnicodeRawReader("😀", "x"));
        var first = await reader.ReadKeyAsync(); var second = await reader.ReadKeyAsync();
        Assert.Equal("😀", new string([first.KeyChar, second.KeyChar])); Assert.Equal('x', (await reader.ReadKeyAsync()).KeyChar);
    }

    /// <summary>【Tui】【代理字符校验】验证每个高代理字符都有低代理字符配对。</summary>
    /// <param name="text">显示文本。</param><returns>是否为有效 UTF-16。</returns>
    private static bool IsValidUtf16(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (index + 1 >= text.Length || !char.IsSurrogatePair(text[index], text[index + 1])) return false;
            index++;
        }
        return true;
    }

    /// <summary>【Tui】【Unicode 输入夹具】按顺序返回完整终端输入。</summary><param name="events">输入事件。</param>
    private sealed class UnicodeRawReader(params string[] events) : ITuiRawInputReader
    {
        private readonly Queue<string> _events = new(events);
        /// <summary>【Tui】【输入读取】取下一个事件。</summary><param name="cancellationToken">取消信号。</param><returns>原始输入。</returns>
        public ValueTask<string> ReadInputAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_events.Dequeue()); }
    }

    /// <summary>【Tui】【Unicode 渲染夹具】保存编辑和搜索帧，用于检查是否出现半个代理字符。</summary>
    private sealed class UnicodeRenderer : IInteractiveRenderer
    {
        public int WindowWidth => 80;
        public List<string> Frames { get; } = [];
        /// <summary>【Tui】【提示渲染】忽略提示。</summary><param name="prompt">提示文本。</param><param name="color">提示颜色。</param>
        public void WritePrompt(string prompt, ConsoleColor? color = null) { }
        /// <summary>【Tui】【文本渲染】记录完整草稿。</summary><param name="buffer">草稿。</param><param name="cursorIndex">光标。</param>
        public void Render(string buffer, int cursorIndex) => Frames.Add(buffer);
        /// <summary>【Tui】【搜索渲染】记录搜索关键字。</summary><param name="pattern">关键字。</param><param name="match">匹配文本。</param><param name="cursorInMatch">光标。</param>
        public void RenderSearch(string pattern, string? match, int cursorInMatch) => Frames.Add(pattern);
        /// <summary>【Tui】【提交渲染】忽略提交。</summary>
        public void Commit() { }
        /// <summary>【Tui】【取消渲染】忽略取消。</summary>
        public void Cancel() { }
    }
}
