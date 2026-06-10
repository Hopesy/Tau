using Tau.Tui.Abstractions;
using Tau.Tui.Components;

namespace Tau.Tui.Runtime;

public sealed class InteractiveConsoleSession
{
    private const string DefaultPrompt = "> ";
    private const ConsoleColor DefaultPromptColor = ConsoleColor.Green;
    private readonly ITerminal _terminal;
    private readonly InteractiveInputEditor? _editor;
    private readonly Action? _clearScreenAction;
    private readonly List<TranscriptEntry> _transcript = [];
    private int _visibleTranscriptStart;
    private bool _streamingLineOpen;
    private TranscriptEntryKind? _streamingKind;
    private string _streamingBuffer = string.Empty;

    public InputBuffer InputBuffer { get; } = new();
    public IReadOnlyList<TranscriptEntry> Transcript => _transcript;
    public IKeyBindingMap? InputKeyBindings => _editor?.KeyBindings;
    public event Action? TranscriptChanged;

    public InteractiveConsoleSession(
        ITerminal terminal,
        InteractiveInputEditor? editor = null,
        Action? clearScreenAction = null)
    {
        _terminal = terminal;
        _editor = editor;
        _clearScreenAction = clearScreenAction;
    }

    public void ShowWelcome(string title, string promptHint)
    {
        _terminal.WriteLine(title, ConsoleColor.Cyan);
        _terminal.WriteLine(promptHint);
        _terminal.WriteLine();
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, title));
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, promptHint));
        NotifyTranscriptChanged();
    }

    public IReadOnlyList<TuiMessage> SnapshotMessages()
    {
        var messages = _transcript
            .Skip(Math.Clamp(_visibleTranscriptStart, 0, _transcript.Count))
            .Select(static entry => new TuiMessage(ToMessageRole(entry.Kind), entry.Text))
            .ToList();

        if (_streamingLineOpen && _streamingKind is { } streamingKind)
        {
            messages.Add(new TuiMessage(ToMessageRole(streamingKind), _streamingBuffer));
        }

        return messages;
    }

    public async Task<string?> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadInputResultAsync(cancellationToken).ConfigureAwait(false);
        return result.Kind == InputResultKind.Submitted ? result.Text : null;
    }

    public async Task<string?> ReadInputAsync(
        string prompt,
        ConsoleColor? promptColor,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadInputResultAsync(prompt, promptColor, cancellationToken).ConfigureAwait(false);
        return result.Kind == InputResultKind.Submitted ? result.Text : null;
    }

    public async Task<InputResult> ReadInputResultAsync(CancellationToken cancellationToken = default)
    {
        return await ReadInputResultAsync(DefaultPrompt, DefaultPromptColor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InputResult> ReadInputResultAsync(
        string prompt,
        ConsoleColor? promptColor,
        CancellationToken cancellationToken = default)
    {
        if (_editor is not null)
        {
            EnsureStreamingLineClosed();
            var result = await _editor.ReadLineAsync(prompt, promptColor, cancellationToken).ConfigureAwait(false);
            if (result.Kind != InputResultKind.Submitted)
            {
                return result;
            }

            InputBuffer.SetDraft(result.Text);
            return InputResult.Submitted(InputBuffer.Commit());
        }

        var input = await _terminal.PromptAsync(prompt, promptColor, cancellationToken).ConfigureAwait(false);
        InputBuffer.SetDraft(input);
        return InputResult.Submitted(InputBuffer.Commit());
    }

    public void SetDraft(string? value)
    {
        InputBuffer.SetDraft(value);
        _editor?.Buffer.SetDraft(value);
    }

    public void SetInputShortcutHandler(Func<ConsoleKeyInfo, CancellationToken, Task<bool>>? shortcutHandler)
    {
        _editor?.SetShortcutHandler(shortcutHandler);
    }

    public void WriteUserMessage(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("you> ", ConsoleColor.Green);
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.User, message));
        NotifyTranscriptChanged();
    }

    public void WriteAssistantText(string delta)
    {
        EnsureStreamingMode(TranscriptEntryKind.Assistant, "tau> ", ConsoleColor.Cyan);
        _terminal.Write(delta);
        _streamingBuffer += delta;
        NotifyTranscriptChanged();
    }

    public void WriteAssistantThinking(string delta)
    {
        EnsureStreamingMode(TranscriptEntryKind.Thinking, "thinking> ", ConsoleColor.DarkGray);
        _terminal.Write(delta, ConsoleColor.DarkGray);
        _streamingBuffer += delta;
        NotifyTranscriptChanged();
    }

    public void WriteToolStart(string toolName)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("tool> ", ConsoleColor.Yellow);
        _terminal.Write($"[{toolName}] ", ConsoleColor.Yellow);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Tool, $"[{toolName}]"));
        NotifyTranscriptChanged();
    }

    public void WriteToolEnd(bool isError)
    {
        var status = isError ? "(error)" : "(done)";
        _terminal.WriteLine(status, isError ? ConsoleColor.Red : ConsoleColor.DarkGreen);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Status, status));
        NotifyTranscriptChanged();
    }


    public void WriteStatus(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("status> ", ConsoleColor.DarkGray);
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Status, message));
        NotifyTranscriptChanged();
    }

    public void WriteRuntimeError(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("error> ", ConsoleColor.Red);
        _terminal.WriteLine(message, ConsoleColor.Red);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Error, message));
        NotifyTranscriptChanged();
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
        NotifyTranscriptChanged();
    }

    public void WriteShutdown(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, message));
        NotifyTranscriptChanged();
    }

    public void ClearScreen()
    {
        EnsureStreamingLineClosed();
        if (_clearScreenAction is not null)
        {
            _visibleTranscriptStart = _transcript.Count;
            NotifyTranscriptChanged();
            _clearScreenAction();
            return;
        }

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
        NotifyTranscriptChanged();
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

    private static TuiMessageRole ToMessageRole(TranscriptEntryKind kind) =>
        kind switch
        {
            TranscriptEntryKind.System => TuiMessageRole.System,
            TranscriptEntryKind.User => TuiMessageRole.User,
            TranscriptEntryKind.Assistant => TuiMessageRole.Assistant,
            TranscriptEntryKind.Thinking => TuiMessageRole.Thinking,
            TranscriptEntryKind.Tool => TuiMessageRole.Tool,
            TranscriptEntryKind.Error => TuiMessageRole.Error,
            TranscriptEntryKind.Status => TuiMessageRole.Status,
            _ => TuiMessageRole.Status,
        };

    private void NotifyTranscriptChanged() => TranscriptChanged?.Invoke();
}
