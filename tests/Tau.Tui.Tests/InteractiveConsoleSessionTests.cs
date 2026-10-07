using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public class InteractiveConsoleSessionTests
{
    /// <summary>【TUI】【条目渲染】同一自定义条目重绘替换正文，返回空组件时可以删除且不影响普通消息。</summary>
    [Fact]
    public void KeyedCustomEntriesUpdateAndCanBeHidden()
    {
        var session = new InteractiveConsoleSession(new FakeTerminal());
        session.WriteCustomMessage("unkeyed message");
        session.WriteCustomMessage("collapsed", "entry:one");
        session.WriteCustomMessage("expanded", "entry:one");
        Assert.Equal(2, session.Transcript.Count);
        Assert.Equal("expanded", Assert.Single(session.Transcript, entry => entry.Key == "entry:one").Text);
        session.RemoveTranscriptEntry(TranscriptEntryKind.Custom, "entry:one");
        Assert.Equal("unkeyed message", Assert.Single(session.Transcript).Text);
    }
    /// <summary>【TUI】【显示重置】清除旧流式行及键控工具，保留草稿并允许新会话重用工具标识。</summary>
    /// <param name="customClear">是否使用宿主清屏回调。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetTranscriptClearsStreamingAndKeyedEntriesButPreservesDraft(bool customClear)
    {
        var terminal = new FakeTerminal();
        var cleared = 0;
        var session = new InteractiveConsoleSession(terminal, clearScreenAction: customClear ? () => cleared++ : null);
        session.SetDraft("unfinished input");
        session.WriteToolComponent(new TuiBashExecution("old command"), key: "shared");
        session.WriteAssistantText("old partial answer");
        session.ResetTranscript();
        Assert.Empty(session.Transcript);
        Assert.Empty(session.SnapshotMessages());
        Assert.Equal("unfinished input", session.GetDraft());
        Assert.Equal(customClear ? 1 : 0, cleared);
        session.WriteToolComponent(new TuiBashExecution("new command"), key: "shared");
        session.WriteAssistantText("new answer");
        session.CompleteAssistantTurn();
        Assert.Equal(2, session.Transcript.Count);
        Assert.DoesNotContain(session.Transcript, entry => entry.Text.Contains("old", StringComparison.Ordinal));
    }
    private const string PromptZoneStart = "\u001b]133;A\u0007";
    private const string PromptZoneEnd = "\u001b]133;B\u0007";
    private const string PromptZoneFinal = "\u001b]133;C\u0007";

    [Fact]
    public void ToolStart_ClosesStreamingLine_BeforeWritingToolName()
    {
        var terminal = new FakeTerminal();
        var session = new InteractiveConsoleSession(terminal);

        session.WriteAssistantText("hello");
        session.WriteToolStart("read_file");
        session.WriteToolEnd(isError: false);
        session.CompleteAssistantTurn();

        var output = terminal.FlattenedText();

        Assert.Contains($"{PromptZoneStart}tau> hello\n{PromptZoneEnd}{PromptZoneFinal}tool> [read_file] (done)\n", output);
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.Assistant, Text: "hello" });
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.Tool, Text: "[read_file]" });
    }

    [Fact]
    public void ToolComponent_WritesRenderedComponentAndTranscript()
    {
        var terminal = new FakeTerminal();
        var session = new InteractiveConsoleSession(terminal);
        var component = new TuiToolExecution("read_file", "call-1", """{"path":"README.md"}""");
        component.UpdateResult(new TuiToolExecutionResult([new TuiToolTextBlock("done")]));

        session.WriteToolComponent(component, width: 32);

        var output = terminal.FlattenedText();

        Assert.Contains("tool> read_file\n", output);
        Assert.Contains("      \"path\": \"README.md\"\n", output);
        Assert.Contains("      done\n", output);
        Assert.Contains(
            session.Transcript,
            entry => entry.Kind == TranscriptEntryKind.Tool &&
                entry.Text.Contains("read_file", StringComparison.Ordinal) &&
                entry.Text.Contains("done", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolComponent_WithSameKeyUpdatesExistingTranscriptEntry()
    {
        var terminal = new FakeTerminal();
        var session = new InteractiveConsoleSession(terminal);
        var component = new TuiToolExecution("read_file", "call-1", """{"path":"README.md"}""");

        session.WriteToolComponent(component, width: 32, key: "call-1");
        component.UpdateResult(new TuiToolExecutionResult([new TuiToolTextBlock("done")]));
        session.WriteToolComponent(component, width: 32, key: "call-1");

        var entry = Assert.Single(session.Transcript, entry => entry.Kind == TranscriptEntryKind.Tool);
        Assert.Equal("call-1", entry.Key);
        Assert.Contains("done", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            session.SnapshotMessages(),
            message => message.Role == TuiMessageRole.Tool &&
                message.Text.Contains("\"path\": \"README.md\"", StringComparison.Ordinal) &&
                !message.Text.Contains("done", StringComparison.Ordinal));
    }

    [Fact]
    public void UserMessage_WritesPromptZone()
    {
        var terminal = new FakeTerminal();
        var session = new InteractiveConsoleSession(terminal);

        session.WriteUserMessage("hello");

        var output = terminal.FlattenedText();

        Assert.Contains($"{PromptZoneStart}you> hello\n{PromptZoneEnd}{PromptZoneFinal}", output);
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.User, Text: "hello" });
    }

    [Fact]
    public async Task ReadInputAsync_PrefersEditorWhenProvided()
    {
        var terminal = new FakeTerminal();
        var keyReader = new ScriptedKeyReader();
        keyReader.Enqueue('h');
        keyReader.Enqueue('i');
        keyReader.EnqueueKey(ConsoleKey.Enter);
        var renderer = new CapturingRenderer();
        var editor = new InteractiveInputEditor(keyReader, renderer);
        var session = new InteractiveConsoleSession(terminal, editor);

        var input = await session.ReadInputAsync();

        Assert.Equal("hi", input);
        Assert.Empty(terminal.Writes);
        Assert.Single(renderer.Prompts);
    }

    [Fact]
    public async Task ReadInputAsync_FallsBackToTerminalPromptWhenNoEditor()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("hello");
        var session = new InteractiveConsoleSession(terminal);

        var input = await session.ReadInputAsync();

        Assert.Equal("hello", input);
        Assert.Contains(terminal.Writes, w => w.Text == ">> ");
    }

    [Fact]
    public async Task ReadInputAsync_TreatsEditorCancellationAsNullInput()
    {
        var terminal = new FakeTerminal();
        var keyReader = new ScriptedKeyReader();
        keyReader.EnqueueRaw(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true));
        var renderer = new CapturingRenderer();
        var editor = new InteractiveInputEditor(keyReader, renderer);
        var session = new InteractiveConsoleSession(terminal, editor);

        var input = await session.ReadInputAsync();

        Assert.Null(input);
    }

    [Fact]
    public async Task ReadInputResultAsync_ReturnsEditorAction()
    {
        var terminal = new FakeTerminal();
        var keyReader = new ScriptedKeyReader();
        keyReader.EnqueueRaw(new ConsoleKeyInfo('\x10', ConsoleKey.P, shift: false, alt: false, control: true));
        var renderer = new CapturingRenderer();
        var editor = new InteractiveInputEditor(keyReader, renderer);
        var session = new InteractiveConsoleSession(terminal, editor);

        var result = await session.ReadInputResultAsync();

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.CycleModelForward, result.Action);
        Assert.Empty(terminal.Writes);
        Assert.Single(renderer.Prompts);
    }

    [Fact]
    public async Task ReadInputResultAsync_ReturnsModelSelectEditorAction()
    {
        var terminal = new FakeTerminal();
        var keyReader = new ScriptedKeyReader();
        keyReader.EnqueueRaw(new ConsoleKeyInfo('\x0C', ConsoleKey.L, shift: false, alt: false, control: true));
        var renderer = new CapturingRenderer();
        var editor = new InteractiveInputEditor(keyReader, renderer);
        var session = new InteractiveConsoleSession(terminal, editor);

        var result = await session.ReadInputResultAsync();

        Assert.Equal(InputResultKind.Action, result.Kind);
        Assert.Equal(EditorAction.SelectModel, result.Action);
        Assert.Empty(terminal.Writes);
        Assert.Single(renderer.Prompts);
    }

    [Fact]
    public void ShowWelcome_WritesTitleHintAndBlankLine()
    {
        var terminal = new FakeTerminal();
        var session = new InteractiveConsoleSession(terminal);

        session.ShowWelcome("Tau — Coding Agent", "Type your message, or 'exit' to quit.");

        var output = terminal.FlattenedText();

        Assert.Contains("Tau — Coding Agent\n", output);
        Assert.Contains("Type your message, or 'exit' to quit.\n", output);
        Assert.EndsWith("\n\n", output);
        Assert.Equal(TuiWelcomeBanner.Lines.Count + 2, session.Transcript.Count);
        Assert.All(TuiWelcomeBanner.Lines, line => Assert.Contains(line + "\n", output, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadInputAsync_UsesInputBuffer_AndClearsDraftAfterCommit()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("hello");
        var session = new InteractiveConsoleSession(terminal);

        var input = await session.ReadInputAsync();

        Assert.Equal("hello", input);
        Assert.False(session.InputBuffer.HasDraft);
    }

    [Fact]
    public async Task ReadInputAsync_CustomPromptUsesProvidedPromptAndColor()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("code");
        var session = new InteractiveConsoleSession(terminal);

        var input = await session.ReadInputAsync("oauth> ", ConsoleColor.Cyan);

        Assert.Equal("code", input);
        Assert.Contains(terminal.Writes, write => write.Text == "oauth> " && write.Color == ConsoleColor.Cyan && !write.IsLine);
    }

    [Fact]
    public void ThinkingAndAssistantText_AreSplitIntoSeparateTranscriptEntries()
    {
        var terminal = new FakeTerminal();
        var session = new InteractiveConsoleSession(terminal);

        session.WriteAssistantThinking("plan");
        session.WriteAssistantText("answer");
        session.CompleteAssistantTurn();

        var output = terminal.FlattenedText();

        Assert.Contains($"thinking> plan\n{PromptZoneStart}tau> answer\n{PromptZoneEnd}{PromptZoneFinal}", output);
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.Thinking, Text: "plan" });
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.Assistant, Text: "answer" });
    }

    private sealed class ScriptedKeyReader : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();

        public void Enqueue(char ch) =>
            _keys.Enqueue(new ConsoleKeyInfo(ch, ConsoleKey.NoName, shift: false, alt: false, control: false));

        public void EnqueueKey(ConsoleKey key) =>
            _keys.Enqueue(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: false));

        public void EnqueueRaw(ConsoleKeyInfo key) => _keys.Enqueue(key);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_keys.Dequeue());
    }

    private sealed class CapturingRenderer : IInteractiveRenderer
    {
        public int WindowWidth => 80;
        public List<string> Prompts { get; } = [];
        public List<(string Buffer, int Cursor)> Renders { get; } = [];
        public int CommitCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public void WritePrompt(string prompt, ConsoleColor? color = null) => Prompts.Add(prompt);
        public void Render(string buffer, int cursorIndex) => Renders.Add((buffer, cursorIndex));
        public void RenderSearch(string pattern, string? match, int cursorInMatch) { }
        public void Commit() => CommitCalls++;
        public void Cancel() => CancelCalls++;
    }
}
