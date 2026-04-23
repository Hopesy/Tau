using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public class InteractiveConsoleSessionTests
{
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

        Assert.Contains("tau> hello\ntool> [read_file] (done)\n", output);
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.Assistant, Text: "hello" });
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.Tool, Text: "[read_file]" });
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
        Assert.Equal(2, session.Transcript.Count);
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
    public void ThinkingAndAssistantText_AreSplitIntoSeparateTranscriptEntries()
    {
        var terminal = new FakeTerminal();
        var session = new InteractiveConsoleSession(terminal);

        session.WriteAssistantThinking("plan");
        session.WriteAssistantText("answer");
        session.CompleteAssistantTurn();

        var output = terminal.FlattenedText();

        Assert.Contains("thinking> plan\ntau> answer\n", output);
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.Thinking, Text: "plan" });
        Assert.Contains(session.Transcript, entry => entry is { Kind: TranscriptEntryKind.Assistant, Text: "answer" });
    }
}
