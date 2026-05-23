using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class InteractiveConsoleSession
{
    private readonly ITerminal _terminal;
    private readonly InteractiveInputEditor? _editor;
    private readonly List<TranscriptEntry> _transcript = [];
    private bool _streamingLineOpen;
    private TranscriptEntryKind? _streamingKind;
    private string _streamingBuffer = string.Empty;

    public InputBuffer InputBuffer { get; } = new();
    public IReadOnlyList<TranscriptEntry> Transcript => _transcript;

    public InteractiveConsoleSession(ITerminal terminal, InteractiveInputEditor? editor = null)
    {
        _terminal = terminal;
        _editor = editor;
    }

    public void ShowWelcome(string title, string promptHint)
    {
        _terminal.WriteLine(title, ConsoleColor.Cyan);
        _terminal.WriteLine(promptHint);
        _terminal.WriteLine();
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, title));
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, promptHint));
    }

    public async Task<string?> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadInputResultAsync(cancellationToken).ConfigureAwait(false);
        return result.Kind == InputResultKind.Submitted ? result.Text : null;
    }

    public async Task<InputResult> ReadInputResultAsync(CancellationToken cancellationToken = default)
    {
        if (_editor is not null)
        {
            EnsureStreamingLineClosed();
            var result = await _editor.ReadLineAsync("> ", ConsoleColor.Green, cancellationToken).ConfigureAwait(false);
            if (result.Kind != InputResultKind.Submitted)
            {
                return result;
            }

            InputBuffer.SetDraft(result.Text);
            return InputResult.Submitted(InputBuffer.Commit());
        }

        var input = await _terminal.PromptAsync("> ", ConsoleColor.Green, cancellationToken).ConfigureAwait(false);
        InputBuffer.SetDraft(input);
        return InputResult.Submitted(InputBuffer.Commit());
    }

    public void WriteUserMessage(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("you> ", ConsoleColor.Green);
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.User, message));
    }

    public void WriteAssistantText(string delta)
    {
        EnsureStreamingMode(TranscriptEntryKind.Assistant, "tau> ", ConsoleColor.Cyan);
        _terminal.Write(delta);
        _streamingBuffer += delta;
    }

    public void WriteAssistantThinking(string delta)
    {
        EnsureStreamingMode(TranscriptEntryKind.Thinking, "thinking> ", ConsoleColor.DarkGray);
        _terminal.Write(delta, ConsoleColor.DarkGray);
        _streamingBuffer += delta;
    }

    public void WriteToolStart(string toolName)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("tool> ", ConsoleColor.Yellow);
        _terminal.Write($"[{toolName}] ", ConsoleColor.Yellow);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Tool, $"[{toolName}]"));
    }

    public void WriteToolEnd(bool isError)
    {
        var status = isError ? "(error)" : "(done)";
        _terminal.WriteLine(status, isError ? ConsoleColor.Red : ConsoleColor.DarkGreen);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Status, status));
    }


    public void WriteStatus(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("status> ", ConsoleColor.DarkGray);
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Status, message));
    }

    public void WriteRuntimeError(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("error> ", ConsoleColor.Red);
        _terminal.WriteLine(message, ConsoleColor.Red);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Error, message));
    }

    public void CompleteAssistantTurn()
    {
        EnsureStreamingLineClosed();
    }

    public void WriteCancelled()
    {
        EnsureStreamingLineClosed();
        _terminal.WriteLine("[Cancelled]", ConsoleColor.Yellow);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Status, "[Cancelled]"));
    }

    public void WriteShutdown(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, message));
    }

    public void ClearScreen()
    {
        EnsureStreamingLineClosed();
        // ANSI clear screen + move cursor home. Terminals that don't support ANSI
        // will just print the codes, which is acceptable for a /clear best-effort.
        _terminal.Write("\u001b[2J\u001b[H");
    }

    private void EnsureStreamingLineClosed()
    {
        if (!_streamingLineOpen)
        {
            return;
        }

        _terminal.WriteLine();
        _streamingLineOpen = false;
        if (_streamingKind is not null)
        {
            _transcript.Add(new TranscriptEntry(_streamingKind.Value, _streamingBuffer));
        }

        _streamingKind = null;
        _streamingBuffer = string.Empty;
    }

    private void EnsureStreamingMode(TranscriptEntryKind kind, string prefix, ConsoleColor prefixColor)
    {
        if (_streamingLineOpen && _streamingKind == kind)
        {
            return;
        }

        EnsureStreamingLineClosed();
        _terminal.Write(prefix, prefixColor);
        _streamingLineOpen = true;
        _streamingKind = kind;
        _streamingBuffer = string.Empty;
    }
}
