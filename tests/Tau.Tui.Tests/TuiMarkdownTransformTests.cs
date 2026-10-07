// 作者：xxx
using Tau.Tui.Components;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiMarkdownTransformTests
{
    /// <summary>【TUI】【Markdown 转换】转换发生在排版之前，使用扣除留白的宽度，缓存随正文或宽度变化失效。</summary>
    [Fact]
    public void Markdown_TransformsBeforeParsingAndCachesBySourceAndWidth()
    {
        var calls = new List<(string, int)>();
        var markdown = new TuiMarkdown("source", paddingX: 2, transform: (text, width) =>
        { calls.Add((text, width)); return "# transformed " + width; });
        Assert.Contains("transformed 36", string.Join("\n", markdown.Render(40)));
        markdown.Render(40);
        Assert.Single(calls);
        markdown.Render(50);
        Assert.Equal(("source", 46), calls[1]);
        markdown.SetText("updated");
        markdown.Render(50);
        Assert.Equal(("updated", 46), calls[2]);
        markdown.Invalidate();
        markdown.Render(50);
        Assert.Equal(4, calls.Count);
        Assert.Equal("updated", markdown.Text);
    }

    /// <summary>【TUI】【Markdown 空正文】转换器既可生成空输入的显示内容，也可隐藏非空原文。</summary>
    [Fact]
    public void Markdown_TransformsBeforeEmptyCheck()
    {
        Assert.Contains("generated", string.Join("\n", new TuiMarkdown("", transform: (_, _) => "generated").Render(30)));
        Assert.Empty(new TuiMarkdown("hidden", transform: (_, _) => "").Render(30));
    }

    /// <summary>【TUI】【流式转换】每次传入完整原文，结束后切换最终标记，消息正文与 transcript 不保存显示转换结果。</summary>
    [Fact]
    public void Session_StreamsWholeSourceAndPreservesRawTranscript()
    {
        var terminal = new FakeTerminal();
        var session = new InteractiveConsoleSession(terminal);
        var calls = new List<(string Text, TuiMarkdownTransformContext Context)>();
        session.SetMarkdownTransform((text, context) => { calls.Add((text, context)); return "shown " + text; });
        session.WriteAssistantText("raw ");
        Assert.Contains("shown raw", Render(session, 50));
        session.WriteAssistantText("answer");
        Assert.Contains("shown raw answer", Render(session, 50));
        session.CompleteAssistantTurn();
        Assert.Contains("shown raw answer", Render(session, 60));
        Assert.Contains(calls, item => item == ("raw ", new TuiMarkdownTransformContext("assistant", true, 45)));
        Assert.Contains(calls, item => item == ("raw answer", new TuiMarkdownTransformContext("assistant", true, 45)));
        Assert.Contains(calls, item => item == ("raw answer", new TuiMarkdownTransformContext("assistant", false, 55)));
        Assert.Equal("raw answer", Assert.Single(session.Transcript).Text);
        Assert.Equal("raw answer", Assert.Single(session.SnapshotMessages()).Text);
        Assert.Contains("shown raw answer", terminal.FlattenedText());
    }

    /// <summary>【TUI】【转换范围】隐藏思考标签及非对话消息不调用转换器，恢复正文始终是最终状态。</summary>
    [Fact]
    public void Session_SkipsHiddenThinkingAndNonConversationMessages()
    {
        var session = new InteractiveConsoleSession(new FakeTerminal());
        var calls = new List<TuiMarkdownTransformContext>();
        session.SetMarkdownTransform((text, context) => { calls.Add(context); return "changed " + text; });
        session.WriteAssistantThinking("private label", applyMarkdownTransform: false);
        Render(session, 80);
        session.CompleteAssistantTurn();
        session.WriteCustomMessage("custom");
        session.WriteStatus("status");
        Render(session, 80);
        Assert.Empty(calls);
        session.WriteAssistantText("history", isStreaming: false);
        Render(session, 80);
        Assert.All(calls, context => Assert.False(context.IsStreaming));
        Assert.Contains(session.SnapshotMessages(), message => message.Role == TuiMessageRole.Thinking && message.MarkdownTransform is null);
    }

    /// <summary>【TUI】【转换失败】宿主转换函数抛出异常时保持原文，替换转换链后旧记录可以重绘。</summary>
    [Fact]
    public void Session_FailureAndReloadPreserveSource()
    {
        var session = new InteractiveConsoleSession(new FakeTerminal());
        session.SetMarkdownTransform((_, _) => throw new InvalidOperationException("expected"));
        session.WriteUserMessage("original");
        Assert.Contains("original", Render(session, 50));
        session.SetMarkdownTransform((_, _) => "replacement");
        Assert.Contains("replacement", Render(session, 50));
        session.SetMarkdownTransform(null);
        Assert.Contains("original", Render(session, 50));
        Assert.Equal("original", Assert.Single(session.Transcript).Text);
    }

    /// <summary>【TUI】【测试渲染】用实际消息区域按指定宽度排版当前快照。</summary>
    /// <param name="session">会话。</param><param name="width">终端列数。</param><returns>显示文本。</returns>
    private static string Render(InteractiveConsoleSession session, int width) =>
        string.Join("\n", TuiMessageArea.RenderMessages(session.SnapshotMessages(), width, displayOptions: new TuiMessageDisplayOptions(renderMarkdown: true)));
}
