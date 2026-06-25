using System.Globalization;
using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class InteractiveInputEditor
{
    private readonly IConsoleKeyReader _reader;
    private readonly IInteractiveRenderer _renderer;
    private readonly InputHistory _history;
    private readonly InputBuffer _buffer;
    private readonly KillRing _killRing = new();
    private IKeyBindingMap _bindings;
    private ITuiAutocompleteProvider? _autocompleteProvider;
    private AutocompleteSession? _autocompleteSession;
    private Func<ConsoleKeyInfo, CancellationToken, Task<bool>>? _shortcutHandler;
    private LastEditAction _lastEditAction = LastEditAction.None;
    private int _lastYankStart = -1;
    private string _lastYankedText = string.Empty;

    public InteractiveInputEditor(
        IConsoleKeyReader reader,
        IInteractiveRenderer renderer,
        InputBuffer? buffer = null,
        InputHistory? history = null,
        IKeyBindingMap? bindings = null,
        ITuiAutocompleteProvider? autocompleteProvider = null)
    {
        _reader = reader;
        _renderer = renderer;
        _buffer = buffer ?? new InputBuffer();
        _history = history ?? new InputHistory();
        _bindings = bindings ?? KeyBindingMap.Default;
        _autocompleteProvider = autocompleteProvider;
    }

    public InputBuffer Buffer => _buffer;
    public InputHistory History => _history;
    public IKeyBindingMap KeyBindings => _bindings;

    public void SetKeyBindings(IKeyBindingMap bindings)
    {
        _bindings = bindings;
    }

    public void SetAutocompleteProvider(ITuiAutocompleteProvider? autocompleteProvider)
    {
        _autocompleteProvider = autocompleteProvider;
        _autocompleteSession = null;
    }

    public void SetShortcutHandler(Func<ConsoleKeyInfo, CancellationToken, Task<bool>>? shortcutHandler)
    {
        _shortcutHandler = shortcutHandler;
    }

    public async Task<InputResult> ReadLineAsync(
        string prompt,
        ConsoleColor? promptColor = null,
        CancellationToken cancellationToken = default)
    {
        _renderer.WritePrompt(prompt, promptColor);
        var chars = new List<char>(_buffer.Draft);
        var cursor = chars.Count;
        var historyOffset = -1;
        int? preferredVerticalColumn = null;
        var undoStack = new UndoStack();
        ResetTransientEditAction();
        _renderer.Render(new string(chars.ToArray()), cursor);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await _reader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
            if (_shortcutHandler is not null &&
                await _shortcutHandler(key, cancellationToken).ConfigureAwait(false))
            {
                _renderer.Render(new string(chars.ToArray()), cursor);
                continue;
            }

            var action = _bindings.Resolve(key);

            if (ShouldInsertNewLine(key, action))
            {
                var undoState = CaptureUndoState(chars, cursor);
                InsertNewLine(chars, ref cursor);
                PushUndoIfChanged(undoStack, undoState, chars, cursor);
                historyOffset = -1;
                preferredVerticalColumn = null;
                ResetTransientEditAction();
                _renderer.Render(new string(chars.ToArray()), cursor);
                continue;
            }

            if (action == EditorAction.None && TryHandleUndoShortcut(key, undoStack, ref chars, ref cursor))
            {
                historyOffset = -1;
                preferredVerticalColumn = null;
                ResetTransientEditAction();
                _renderer.Render(new string(chars.ToArray()), cursor);
                continue;
            }

            if (action == EditorAction.None)
            {
                var undoState = CaptureUndoState(chars, cursor);
                if (TryHandleYankShortcut(key, chars, ref cursor))
                {
                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    historyOffset = -1;
                    preferredVerticalColumn = null;
                    _renderer.Render(new string(chars.ToArray()), cursor);
                    continue;
                }
            }

            switch (action)
            {
                case EditorAction.Cancel:
                    _renderer.Cancel();
                    _buffer.SetDraft(new string(chars.ToArray()));
                    ResetTransientEditAction();
                    return InputResult.Cancelled;
                case EditorAction.Submit:
                {
                    if (key.Key == ConsoleKey.Enter &&
                        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control | ConsoleModifiers.Shift)) == 0)
                    {
                        var undoState = CaptureUndoState(chars, cursor);
                        if (TryInsertNewLineForSubmitFallback(chars, ref cursor))
                        {
                            PushUndoIfChanged(undoStack, undoState, chars, cursor);
                            historyOffset = -1;
                            preferredVerticalColumn = null;
                            ResetTransientEditAction();
                            break;
                        }
                    }

                    var committed = new string(chars.ToArray());
                    _renderer.Commit();
                    _buffer.SetDraft(committed);
                    _buffer.Commit();
                    ResetTransientEditAction();
                    if (!string.IsNullOrWhiteSpace(committed))
                    {
                        _history.Add(committed);
                    }
                    return InputResult.Submitted(committed);
                }
                case EditorAction.ReverseSearch:
                {
                    var searchOutcome = await RunReverseSearchAsync(chars, cancellationToken).ConfigureAwait(false);
                    chars = new List<char>(searchOutcome.Buffer);
                    cursor = chars.Count;
                    historyOffset = -1;
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    if (searchOutcome.Submit)
                    {
                        var committed = new string(chars.ToArray());
                        _renderer.Commit();
                        _buffer.SetDraft(committed);
                        _buffer.Commit();
                        ResetTransientEditAction();
                        if (!string.IsNullOrWhiteSpace(committed))
                        {
                            _history.Add(committed);
                        }
                        return InputResult.Submitted(committed);
                    }

                    _renderer.WritePrompt(prompt, promptColor);
                    _renderer.Render(new string(chars.ToArray()), cursor);
                    continue;
                }
                case EditorAction.Complete:
                case EditorAction.CompletePrevious:
                {
                    var undoState = CaptureUndoState(chars, cursor);
                    var completed = await TryApplyAutocompleteAsync(
                            chars,
                            cursor,
                            action == EditorAction.CompletePrevious ? -1 : 1,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (completed is not null)
                    {
                        chars = new List<char>(completed.Text);
                        cursor = completed.CursorIndex;
                        if (completed.StartedSession)
                        {
                            PushUndoIfChanged(undoStack, undoState, chars, cursor);
                        }

                        historyOffset = -1;
                        preferredVerticalColumn = null;
                        ResetTransientEditAction(preserveAutocomplete: true);
                    }

                    break;
                }
                case EditorAction.CycleModelForward:
                case EditorAction.CycleModelBackward:
                case EditorAction.SelectModel:
                {
                    var draft = new string(chars.ToArray());
                    _renderer.Commit();
                    _buffer.SetDraft(draft);
                    ResetTransientEditAction();
                    return InputResult.ForAction(action);
                }
                case EditorAction.KillToLineEnd:
                {
                    preferredVerticalColumn = null;
                    var undoState = CaptureUndoState(chars, cursor);
                    var lineEnd = FindLineEnd(chars, cursor);
                    var changed = false;
                    if (cursor < lineEnd)
                    {
                        var killedText = SliceChars(chars, cursor, lineEnd - cursor);
                        chars.RemoveRange(cursor, lineEnd - cursor);
                        PushKill(killedText, prepend: false);
                        changed = true;
                    }
                    else if (cursor < chars.Count && chars[cursor] == '\n')
                    {
                        chars.RemoveAt(cursor);
                        PushKill("\n", prepend: false);
                        changed = true;
                    }

                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    if (changed)
                    {
                        ClearAutocompleteSession();
                    }
                    break;
                }
                case EditorAction.KillToLineStart:
                {
                    preferredVerticalColumn = null;
                    var undoState = CaptureUndoState(chars, cursor);
                    var lineStart = FindLineStart(chars, cursor);
                    var changed = false;
                    if (cursor > lineStart)
                    {
                        var killedText = SliceChars(chars, lineStart, cursor - lineStart);
                        chars.RemoveRange(lineStart, cursor - lineStart);
                        cursor = lineStart;
                        PushKill(killedText, prepend: true);
                        changed = true;
                    }
                    else if (cursor > 0 && chars[cursor - 1] == '\n')
                    {
                        chars.RemoveAt(cursor - 1);
                        cursor--;
                        PushKill("\n", prepend: true);
                        changed = true;
                    }

                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    if (changed)
                    {
                        ClearAutocompleteSession();
                    }
                    break;
                }
                case EditorAction.DeletePrevChar:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    var deletePrevCharUndoState = CaptureUndoState(chars, cursor);
                    if (cursor > 0)
                    {
                        var previousStart = FindPreviousTextElementStart(chars, cursor);
                        chars.RemoveRange(previousStart, cursor - previousStart);
                        cursor = previousStart;
                    }
                    PushUndoIfChanged(undoStack, deletePrevCharUndoState, chars, cursor);
                    break;
                case EditorAction.DeletePrevWord:
                {
                    preferredVerticalColumn = null;
                    var undoState = CaptureUndoState(chars, cursor);
                    var newCursor = cursor > 0 && chars[cursor - 1] == '\n'
                        ? cursor - 1
                        : FindPreviousWordBoundary(chars, cursor);
                    var changed = false;
                    if (newCursor < cursor)
                    {
                        var killedText = SliceChars(chars, newCursor, cursor - newCursor);
                        chars.RemoveRange(newCursor, cursor - newCursor);
                        cursor = newCursor;
                        PushKill(killedText, prepend: true);
                        changed = true;
                    }
                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    if (changed)
                    {
                        ClearAutocompleteSession();
                    }
                    break;
                }
                case EditorAction.DeleteNextChar:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    var deleteNextCharUndoState = CaptureUndoState(chars, cursor);
                    if (cursor < chars.Count)
                    {
                        var nextEnd = FindNextTextElementEnd(chars, cursor);
                        chars.RemoveRange(cursor, nextEnd - cursor);
                    }
                    PushUndoIfChanged(undoStack, deleteNextCharUndoState, chars, cursor);
                    break;
                case EditorAction.DeleteNextWord:
                {
                    preferredVerticalColumn = null;
                    var undoState = CaptureUndoState(chars, cursor);
                    var nextBoundary = cursor < chars.Count && chars[cursor] == '\n'
                        ? cursor + 1
                        : FindNextWordBoundary(chars, cursor);
                    var changed = false;
                    if (nextBoundary > cursor)
                    {
                        var killedText = SliceChars(chars, cursor, nextBoundary - cursor);
                        chars.RemoveRange(cursor, nextBoundary - cursor);
                        PushKill(killedText, prepend: false);
                        changed = true;
                    }
                    PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    if (changed)
                    {
                        ClearAutocompleteSession();
                    }
                    break;
                }
                case EditorAction.CursorLeft:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    if (cursor > 0) cursor = FindPreviousTextElementStart(chars, cursor);
                    break;
                case EditorAction.CursorRight:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    if (cursor < chars.Count) cursor = FindNextTextElementEnd(chars, cursor);
                    break;
                case EditorAction.CursorPrevWord:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    cursor = cursor > 0 && chars[cursor - 1] == '\n'
                        ? cursor - 1
                        : FindPreviousWordBoundary(chars, cursor);
                    break;
                case EditorAction.CursorNextWord:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    cursor = cursor < chars.Count && chars[cursor] == '\n'
                        ? cursor + 1
                        : FindNextWordBoundary(chars, cursor);
                    break;
                case EditorAction.CursorLineStart:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    cursor = FindLineStart(chars, cursor);
                    break;
                case EditorAction.CursorLineEnd:
                    preferredVerticalColumn = null;
                    ResetTransientEditAction();
                    cursor = FindLineEnd(chars, cursor);
                    break;
                case EditorAction.HistoryPrev:
                {
                    ResetTransientEditAction();
                    if (ContainsLineBreak(chars))
                    {
                        if (TryMoveCursorVertically(chars, cursor, -1, ref preferredVerticalColumn, out var verticalCursor))
                        {
                            cursor = verticalCursor;
                        }
                        break;
                    }

                    preferredVerticalColumn = null;
                    var snapshot = _history.Peek(historyOffset + 1);
                    if (snapshot is not null)
                    {
                        var undoState = CaptureUndoState(chars, cursor);
                        historyOffset++;
                        chars.Clear();
                        chars.AddRange(snapshot);
                        cursor = chars.Count;
                        PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    }
                    break;
                }
                case EditorAction.HistoryNext:
                {
                    ResetTransientEditAction();
                    if (ContainsLineBreak(chars))
                    {
                        if (TryMoveCursorVertically(chars, cursor, 1, ref preferredVerticalColumn, out var verticalCursor))
                        {
                            cursor = verticalCursor;
                        }
                        break;
                    }

                    preferredVerticalColumn = null;
                    if (historyOffset > 0)
                    {
                        var undoState = CaptureUndoState(chars, cursor);
                        historyOffset--;
                        var snapshot = _history.Peek(historyOffset);
                        chars.Clear();
                        if (snapshot is not null)
                        {
                            chars.AddRange(snapshot);
                        }

                        cursor = chars.Count;
                        PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    }
                    else if (historyOffset == 0)
                    {
                        var undoState = CaptureUndoState(chars, cursor);
                        historyOffset = -1;
                        chars.Clear();
                        cursor = 0;
                        PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    }
                    break;
                }
                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) &&
                        (key.Modifiers & ConsoleModifiers.Control) == 0)
                    {
                        preferredVerticalColumn = null;
                        ResetTransientEditAction();
                        var undoState = CaptureUndoState(chars, cursor);
                        chars.Insert(cursor, key.KeyChar);
                        cursor++;
                        PushUndoIfChanged(undoStack, undoState, chars, cursor);
                    }
                    break;
            }

            _renderer.Render(new string(chars.ToArray()), cursor);
        }
    }

    private static bool TryHandleUndoShortcut(
        ConsoleKeyInfo key,
        UndoStack undoStack,
        ref List<char> chars,
        ref int cursor)
    {
        if (!IsUndoKey(key))
        {
            return false;
        }

        if (undoStack.TryPop(out var state) && state is not null)
        {
            chars = new List<char>(state.Text);
            cursor = Math.Clamp(state.Cursor, 0, chars.Count);
        }

        return true;
    }

    private async ValueTask<AutocompleteApplyResult?> TryApplyAutocompleteAsync(
        IReadOnlyList<char> chars,
        int cursor,
        int direction,
        CancellationToken cancellationToken)
    {
        if (_autocompleteProvider is null)
        {
            return null;
        }

        if (_autocompleteSession is { } activeSession && activeSession.Items.Count > 0)
        {
            var nextIndex = WrapAutocompleteIndex(activeSession.SelectedIndex + direction, activeSession.Items.Count);
            return ApplyAutocompleteSession(activeSession with { SelectedIndex = nextIndex }, startedSession: false);
        }

        var text = new string(chars.ToArray());
        var suggestions = await _autocompleteProvider
            .GetSuggestionsAsync(text, cursor, force: false, cancellationToken)
            .ConfigureAwait(false);
        suggestions ??= await _autocompleteProvider
            .GetSuggestionsAsync(text, cursor, force: true, cancellationToken)
            .ConfigureAwait(false);
        if (suggestions is null || suggestions.Items.Count == 0)
        {
            return null;
        }

        var selectedIndex = direction < 0 ? suggestions.Items.Count - 1 : 0;
        var session = new AutocompleteSession(
            text,
            Math.Clamp(cursor, 0, text.Length),
            suggestions.Prefix,
            suggestions.Items.ToArray(),
            SelectedIndex: selectedIndex);
        return ApplyAutocompleteSession(session, startedSession: true);
    }

    private static int WrapAutocompleteIndex(int index, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var wrapped = index % count;
        return wrapped < 0 ? wrapped + count : wrapped;
    }

    private AutocompleteApplyResult ApplyAutocompleteSession(
        AutocompleteSession session,
        bool startedSession)
    {
        var item = session.Items[session.SelectedIndex];
        var completed = _autocompleteProvider!.ApplyCompletion(
            session.OriginalText,
            session.OriginalCursor,
            item,
            session.Prefix);
        _autocompleteSession = session;
        return new AutocompleteApplyResult(
            completed.Text,
            Math.Clamp(completed.CursorIndex, 0, completed.Text.Length),
            startedSession);
    }

    private static bool IsUndoKey(ConsoleKeyInfo key)
    {
        var modifiers = key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift);
        if (key.Key == ConsoleKey.Z && modifiers == ConsoleModifiers.Control)
        {
            return true;
        }

        if (key.KeyChar == '\x1F' && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            return true;
        }

        return key.Key == ConsoleKey.OemMinus &&
            modifiers == (ConsoleModifiers.Control | ConsoleModifiers.Shift);
    }

    private static InputEditorState CaptureUndoState(List<char> chars, int cursor) =>
        new(new string(chars.ToArray()), Math.Clamp(cursor, 0, chars.Count));

    private static void PushUndoIfChanged(
        UndoStack undoStack,
        InputEditorState previous,
        List<char> chars,
        int cursor)
    {
        undoStack.PushIfChanged(previous, new string(chars.ToArray()), Math.Clamp(cursor, 0, chars.Count));
    }

    private bool TryHandleYankShortcut(ConsoleKeyInfo key, List<char> chars, ref int cursor)
    {
        if (IsYankKey(key))
        {
            var text = _killRing.Peek();
            if (text is null)
            {
                return false;
            }

            InsertYankedText(chars, ref cursor, text);
            return true;
        }

        if (!IsYankPopKey(key))
        {
            return false;
        }

        if (_lastEditAction != LastEditAction.Yank || _killRing.Count <= 1 || _lastYankedText.Length == 0)
        {
            return true;
        }

        if (_lastYankStart < 0 || _lastYankStart + _lastYankedText.Length > chars.Count)
        {
            ResetTransientEditAction();
            return true;
        }

        chars.RemoveRange(_lastYankStart, _lastYankedText.Length);
        cursor = _lastYankStart;
        _killRing.Rotate();

        var replacement = _killRing.Peek();
        if (replacement is null)
        {
            ResetTransientEditAction();
            return true;
        }

        InsertYankedText(chars, ref cursor, replacement);
        return true;
    }

    private static bool IsYankKey(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Y &&
        (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift))
            == ConsoleModifiers.Control;

    private static bool IsYankPopKey(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Y &&
        (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift))
            == ConsoleModifiers.Alt;

    private void PushKill(string text, bool prepend)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _killRing.Push(text, prepend: prepend, accumulate: _lastEditAction == LastEditAction.Kill);
        _lastEditAction = LastEditAction.Kill;
        _lastYankStart = -1;
        _lastYankedText = string.Empty;
    }

    private void InsertYankedText(List<char> chars, ref int cursor, string text)
    {
        var start = cursor;
        chars.InsertRange(cursor, text);
        cursor += text.Length;
        ClearAutocompleteSession();
        _lastEditAction = LastEditAction.Yank;
        _lastYankStart = start;
        _lastYankedText = text;
    }

    private void ClearAutocompleteSession()
    {
        _autocompleteSession = null;
    }

    private void ResetTransientEditAction(bool preserveAutocomplete = false)
    {
        _lastEditAction = LastEditAction.None;
        _lastYankStart = -1;
        _lastYankedText = string.Empty;
        if (!preserveAutocomplete)
        {
            ClearAutocompleteSession();
        }
    }

    private bool ShouldInsertNewLine(ConsoleKeyInfo key, EditorAction action)
    {
        if (key.Key != ConsoleKey.Enter)
        {
            return false;
        }

        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            return false;
        }

        if ((key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Shift)) == 0)
        {
            return false;
        }

        if (action != EditorAction.None)
        {
            return false;
        }

        return _bindings.Resolve(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: false, alt: false, control: false))
            == EditorAction.Submit;
    }

    private static void InsertNewLine(List<char> chars, ref int cursor)
    {
        chars.Insert(cursor, '\n');
        cursor++;
    }

    private static bool TryInsertNewLineForSubmitFallback(List<char> chars, ref int cursor)
    {
        if (cursor <= 0 || chars[cursor - 1] != '\\')
        {
            return false;
        }

        chars.RemoveAt(cursor - 1);
        cursor--;
        InsertNewLine(chars, ref cursor);
        return true;
    }

    private static string SliceChars(List<char> chars, int start, int length) =>
        length <= 0 ? string.Empty : new string(chars.GetRange(start, length).ToArray());

    private static int FindPreviousTextElementStart(IReadOnlyList<char> chars, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        if (cursor <= 0)
        {
            return 0;
        }

        var text = new string(chars.ToArray());
        var previousStart = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var elementStart = enumerator.ElementIndex;
            if (elementStart >= cursor)
            {
                break;
            }

            previousStart = elementStart;
        }

        return previousStart;
    }

    private static int FindNextTextElementEnd(IReadOnlyList<char> chars, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        if (cursor >= chars.Count)
        {
            return chars.Count;
        }

        var text = new string(chars.ToArray());
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var elementStart = enumerator.ElementIndex;
            var element = enumerator.GetTextElement();
            var elementEnd = elementStart + element.Length;
            if (cursor <= elementStart || cursor < elementEnd)
            {
                return Math.Min(chars.Count, elementEnd);
            }
        }

        return chars.Count;
    }

    private async Task<ReverseSearchOutcome> RunReverseSearchAsync(
        List<char> startingBuffer,
        CancellationToken cancellationToken)
    {
        var originalBuffer = startingBuffer.ToArray();
        var pattern = new List<char>();
        var offset = 0;
        var currentMatch = (Match: (string?)null, Offset: 0, Index: 0);

        _renderer.RenderSearch(string.Empty, null, 0);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await _reader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);

            if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.G)
            {
                return new ReverseSearchOutcome(originalBuffer, Submit: false);
            }

            if (key.Key == ConsoleKey.Escape)
            {
                return new ReverseSearchOutcome(originalBuffer, Submit: false);
            }

            if (key.Key == ConsoleKey.Enter)
            {
                var buffer = currentMatch.Match is null ? originalBuffer : currentMatch.Match.ToCharArray();
                return new ReverseSearchOutcome(buffer, Submit: true);
            }

            if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.R)
            {
                if (currentMatch.Match is not null)
                {
                    offset = currentMatch.Offset + 1;
                }
                ApplyMatch();
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (pattern.Count > 0)
                {
                    pattern.RemoveAt(pattern.Count - 1);
                }
                offset = 0;
                ApplyMatch();
                continue;
            }

            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                pattern.Add(key.KeyChar);
                offset = 0;
                ApplyMatch();
                continue;
            }
        }

        void ApplyMatch()
        {
            var query = new string(pattern.ToArray());
            if (query.Length == 0)
            {
                currentMatch = (null, 0, 0);
                _renderer.RenderSearch(query, null, 0);
                return;
            }

            var found = _history.FindContaining(query, offset);
            if (found is null)
            {
                currentMatch = (null, offset, 0);
                _renderer.RenderSearch(query, null, 0);
                return;
            }

            currentMatch = (found.Value.Match, found.Value.OffsetFromEnd, found.Value.MatchIndex);
            _renderer.RenderSearch(query, found.Value.Match, found.Value.MatchIndex);
        }
    }

    private readonly record struct ReverseSearchOutcome(char[] Buffer, bool Submit);

    private sealed record AutocompleteApplyResult(string Text, int CursorIndex, bool StartedSession);

    private sealed record AutocompleteSession(
        string OriginalText,
        int OriginalCursor,
        string Prefix,
        IReadOnlyList<TuiAutocompleteItem> Items,
        int SelectedIndex);

    private sealed record InputEditorState(string Text, int Cursor);

    private sealed class UndoStack
    {
        private readonly List<InputEditorState> _entries = [];

        public void PushIfChanged(InputEditorState previous, string currentText, int currentCursor)
        {
            if (string.Equals(previous.Text, currentText, StringComparison.Ordinal) &&
                previous.Cursor == currentCursor)
            {
                return;
            }

            _entries.Add(previous);
        }

        public bool TryPop(out InputEditorState? state)
        {
            if (_entries.Count == 0)
            {
                state = null;
                return false;
            }

            state = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            return true;
        }
    }

    private enum LastEditAction
    {
        None,
        Kill,
        Yank,
    }

    private sealed class KillRing
    {
        private readonly List<string> _entries = [];

        public int Count => _entries.Count;

        public void Push(string text, bool prepend, bool accumulate)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (accumulate && _entries.Count > 0)
            {
                _entries[^1] = prepend ? text + _entries[^1] : _entries[^1] + text;
                return;
            }

            _entries.Add(text);
        }

        public string? Peek() => _entries.Count == 0 ? null : _entries[^1];

        public void Rotate()
        {
            if (_entries.Count <= 1)
            {
                return;
            }

            var newest = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            _entries.Insert(0, newest);
        }
    }

    internal static int FindLineStart(IReadOnlyList<char> chars, int cursor)
    {
        var index = Math.Clamp(cursor, 0, chars.Count);
        while (index > 0 && chars[index - 1] != '\n')
        {
            index--;
        }

        return index;
    }

    internal static int FindLineEnd(IReadOnlyList<char> chars, int cursor)
    {
        var index = Math.Clamp(cursor, 0, chars.Count);
        while (index < chars.Count && chars[index] != '\n')
        {
            index++;
        }

        return index;
    }

    private static bool ContainsLineBreak(IReadOnlyList<char> chars)
    {
        for (var i = 0; i < chars.Count; i++)
        {
            if (chars[i] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMoveCursorVertically(
        IReadOnlyList<char> chars,
        int cursor,
        int direction,
        ref int? preferredColumn,
        out int newCursor)
    {
        cursor = Math.Clamp(cursor, 0, chars.Count);
        var currentStart = FindLineStart(chars, cursor);
        var currentEnd = FindLineEnd(chars, cursor);
        var column = cursor - currentStart;
        var desiredColumn = preferredColumn ?? column;

        if (direction < 0)
        {
            if (currentStart == 0)
            {
                newCursor = cursor;
                return false;
            }

            var previousEnd = currentStart - 1;
            var previousStart = FindLineStart(chars, previousEnd);
            var previousLength = previousEnd - previousStart;
            if (previousLength < desiredColumn)
            {
                preferredColumn ??= column;
                newCursor = previousEnd;
            }
            else
            {
                preferredColumn = null;
                newCursor = previousStart + desiredColumn;
            }
            return true;
        }

        if (currentEnd >= chars.Count)
        {
            newCursor = cursor;
            return false;
        }

        var nextStart = currentEnd + 1;
        var nextEnd = FindLineEnd(chars, nextStart);
        var nextLength = nextEnd - nextStart;
        if (nextLength < desiredColumn)
        {
            preferredColumn ??= column;
            newCursor = nextEnd;
        }
        else
        {
            preferredColumn = null;
            newCursor = nextStart + desiredColumn;
        }
        return true;
    }

    internal static int FindPreviousWordBoundary(IReadOnlyList<char> chars, int cursor)
    {
        if (cursor <= 0)
        {
            return 0;
        }

        var index = cursor;
        // Skip trailing whitespace before the cursor.
        while (index > 0 && char.IsWhiteSpace(chars[index - 1]))
        {
            index--;
        }

        // Skip the previous run of non-whitespace characters.
        while (index > 0 && !char.IsWhiteSpace(chars[index - 1]))
        {
            index--;
        }

        return index;
    }

    internal static int FindNextWordBoundary(IReadOnlyList<char> chars, int cursor)
    {
        if (cursor >= chars.Count)
        {
            return chars.Count;
        }

        var index = cursor;
        // Skip leading whitespace at the cursor.
        while (index < chars.Count && char.IsWhiteSpace(chars[index]))
        {
            index++;
        }

        // Skip the next run of non-whitespace characters.
        while (index < chars.Count && !char.IsWhiteSpace(chars[index]))
        {
            index++;
        }

        return index;
    }
}

public sealed record InputResult(InputResultKind Kind, string? Text, EditorAction Action = EditorAction.None)
{
    public static InputResult Submitted(string text) => new(InputResultKind.Submitted, text);
    public static InputResult Cancelled { get; } = new(InputResultKind.Cancelled, null);
    public static InputResult ForAction(EditorAction action) => new(InputResultKind.Action, null, action);
}

public enum InputResultKind
{
    Submitted,
    Cancelled,
    Action
}

public sealed class InputHistory
{
    private readonly List<string> _entries = [];
    private readonly int _capacity;
    private readonly IInputHistoryStore? _store;

    public InputHistory(int capacity = 200)
        : this(capacity, store: null)
    {
    }

    public InputHistory(IInputHistoryStore store, int capacity = 200)
        : this(capacity, store)
    {
    }

    private InputHistory(int capacity, IInputHistoryStore? store)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _store = store;

        if (_store is not null)
        {
            foreach (var entry in _store.Load())
            {
                AddInternal(entry, persist: false);
            }
        }
    }

    public int Count => _entries.Count;

    public void Add(string entry) => AddInternal(entry, persist: true);

    private void AddInternal(string entry, bool persist)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return;
        }

        if (_entries.Count > 0 && string.Equals(_entries[^1], entry, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Add(entry);
        if (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
        }

        if (persist)
        {
            _store?.Append(entry);
        }
    }

    public string? Peek(int offsetFromEnd)
    {
        if (offsetFromEnd < 0 || offsetFromEnd >= _entries.Count)
        {
            return null;
        }

        return _entries[_entries.Count - 1 - offsetFromEnd];
    }

    public (string Match, int OffsetFromEnd, int MatchIndex)? FindContaining(string pattern, int startOffsetFromEnd)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var start = Math.Max(0, startOffsetFromEnd);
        for (var offset = start; offset < _entries.Count; offset++)
        {
            var entry = _entries[_entries.Count - 1 - offset];
            var index = entry.IndexOf(pattern, StringComparison.Ordinal);
            if (index >= 0)
            {
                return (entry, offset, index);
            }
        }

        return null;
    }

    public IReadOnlyList<string> Snapshot(int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<string>();
        }

        if (_entries.Count == 0)
        {
            return Array.Empty<string>();
        }

        var count = Math.Min(limit, _entries.Count);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            // Newest first, so index 0 = most recent.
            result[i] = _entries[_entries.Count - 1 - i];
        }

        return result;
    }
}
