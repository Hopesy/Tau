using Tau.Tui.Abstractions;
using Tau.Tui.Components;

namespace Tau.Tui.Runtime;

public sealed class InteractiveConsoleSession
{
    private const string DefaultPrompt = "> ";
    private const ConsoleColor DefaultPromptColor = ConsoleColor.Green;
    private const string PromptZoneStart = "\u001b]133;A\u0007";
    private const string PromptZoneEnd = "\u001b]133;B\u0007";
    private const string PromptZoneFinal = "\u001b]133;C\u0007";
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

    public void ShowWelcome(string title, string promptHint, IReadOnlyList<string>? customHeaderLines = null)
    {
        if (customHeaderLines is null)
        {
            _terminal.WriteLine(title, ConsoleColor.Cyan);
            _terminal.WriteLine(promptHint);
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, title));
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, promptHint));
        }
        else
        {
            foreach (var line in customHeaderLines)
            {
                _terminal.WriteLine(line);
                _transcript.Add(new TranscriptEntry(TranscriptEntryKind.System, line));
            }
        }

        _terminal.WriteLine();
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

    public string GetDraft() => _editor?.Buffer.Draft ?? InputBuffer.Draft;

    public void SetInputShortcutHandler(Func<ConsoleKeyInfo, CancellationToken, Task<bool>>? shortcutHandler)
    {
        _editor?.SetShortcutHandler(shortcutHandler);
    }

    public void WriteUserMessage(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write(PromptZoneStart);
        _terminal.Write("you> ", ConsoleColor.Green);
        _terminal.WriteLine(message);
        _terminal.Write(PromptZoneEnd + PromptZoneFinal);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.User, message));
        NotifyTranscriptChanged();
    }

    public void WriteCustomMessage(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("custom> ", ConsoleColor.Magenta);
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Custom, message));
        NotifyTranscriptChanged();
    }

    public void WriteSkillInvocation(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("skill> ", ConsoleColor.Magenta);
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Skill, message));
        NotifyTranscriptChanged();
    }

    public void WriteAssistantText(string delta)
    {
        EnsureStreamingMode(TranscriptEntryKind.Assistant, "tau> ", ConsoleColor.Cyan);
        _terminal.Write(delta);
        _streamingBuffer += delta;
        NotifyTranscriptChanged();
    }

    public void WriteAssistantThinking(string delta, string? label = null)
    {
        var thinkingLabel = string.IsNullOrWhiteSpace(label) ? "thinking" : label.Trim();
        EnsureStreamingMode(TranscriptEntryKind.Thinking, $"{thinkingLabel}> ", ConsoleColor.DarkGray);
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

    public void WriteToolComponent(ITuiComponent component, int width = 80, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        EnsureStreamingLineClosed();

        var text = RenderComponentText(component, width);
        if (text.Length == 0)
        {
            return;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i == 0)
            {
                _terminal.Write("tool> ", ConsoleColor.Yellow);
                _terminal.WriteLine(lines[i]);
            }
            else
            {
                _terminal.WriteLine("      " + lines[i]);
            }
        }

        UpsertTranscriptEntry(TranscriptEntryKind.Tool, text, key);
        NotifyTranscriptChanged();
    }

    public void WriteBranchSummary(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("branch> ", ConsoleColor.Magenta);
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.BranchSummary, message));
        NotifyTranscriptChanged();
    }

    public void WriteCompactionSummary(string message)
    {
        EnsureStreamingLineClosed();
        _terminal.Write("compaction> ", ConsoleColor.Magenta);
        _terminal.WriteLine(message);
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.CompactionSummary, message));
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
        if (_streamingKind == TranscriptEntryKind.Assistant)
        {
            _terminal.Write(PromptZoneEnd + PromptZoneFinal);
        }

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
        if (kind == TranscriptEntryKind.Assistant)
        {
            _terminal.Write(PromptZoneStart);
        }

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
            TranscriptEntryKind.BranchSummary => TuiMessageRole.BranchSummary,
            TranscriptEntryKind.CompactionSummary => TuiMessageRole.CompactionSummary,
            TranscriptEntryKind.Custom => TuiMessageRole.Custom,
            TranscriptEntryKind.Skill => TuiMessageRole.Skill,
            TranscriptEntryKind.Error => TuiMessageRole.Error,
            TranscriptEntryKind.Status => TuiMessageRole.Status,
            _ => TuiMessageRole.Status,
        };

    private static string RenderComponentText(ITuiComponent component, int width)
    {
        var lines = component.Render(Math.Max(1, width))
            .Select(static line => line.TrimEnd())
            .ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var commonIndent = lines
            .Where(static line => line.Length > 0)
            .Select(LeadingSpaceCount)
            .DefaultIfEmpty(0)
            .Min();
        if (commonIndent > 0)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i] = lines[i].Length >= commonIndent
                    ? lines[i][commonIndent..]
                    : string.Empty;
            }
        }

        return string.Join('\n', lines);
    }

    private static int LeadingSpaceCount(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private void UpsertTranscriptEntry(TranscriptEntryKind kind, string text, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var index = _transcript.FindIndex(entry =>
                entry.Kind == kind &&
                string.Equals(entry.Key, key, StringComparison.Ordinal));
            if (index >= 0)
            {
                _transcript[index] = new TranscriptEntry(kind, text, key);
                return;
            }
        }

        _transcript.Add(new TranscriptEntry(kind, text, string.IsNullOrWhiteSpace(key) ? null : key));
    }

    private void NotifyTranscriptChanged() => TranscriptChanged?.Invoke();
}
