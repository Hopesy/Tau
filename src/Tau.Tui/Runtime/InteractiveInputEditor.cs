using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class InteractiveInputEditor
{
    private readonly IConsoleKeyReader _reader;
    private readonly IInteractiveRenderer _renderer;
    private readonly InputHistory _history;
    private readonly InputBuffer _buffer;
    private IKeyBindingMap _bindings;

    public InteractiveInputEditor(
        IConsoleKeyReader reader,
        IInteractiveRenderer renderer,
        InputBuffer? buffer = null,
        InputHistory? history = null,
        IKeyBindingMap? bindings = null)
    {
        _reader = reader;
        _renderer = renderer;
        _buffer = buffer ?? new InputBuffer();
        _history = history ?? new InputHistory();
        _bindings = bindings ?? KeyBindingMap.Default;
    }

    public InputBuffer Buffer => _buffer;
    public InputHistory History => _history;
    public IKeyBindingMap KeyBindings => _bindings;

    public void SetKeyBindings(IKeyBindingMap bindings)
    {
        _bindings = bindings;
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
        _renderer.Render(new string(chars.ToArray()), cursor);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await _reader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
            var action = _bindings.Resolve(key);

            switch (action)
            {
                case EditorAction.Cancel:
                    _renderer.Cancel();
                    _buffer.SetDraft(new string(chars.ToArray()));
                    return InputResult.Cancelled;
                case EditorAction.Submit:
                {
                    var committed = new string(chars.ToArray());
                    _renderer.Commit();
                    _buffer.SetDraft(committed);
                    _buffer.Commit();
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
                    if (searchOutcome.Submit)
                    {
                        var committed = new string(chars.ToArray());
                        _renderer.Commit();
                        _buffer.SetDraft(committed);
                        _buffer.Commit();
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
                case EditorAction.KillToLineEnd:
                    if (cursor < chars.Count)
                    {
                        chars.RemoveRange(cursor, chars.Count - cursor);
                    }
                    break;
                case EditorAction.KillToLineStart:
                    if (cursor > 0)
                    {
                        chars.RemoveRange(0, cursor);
                        cursor = 0;
                    }
                    break;
                case EditorAction.DeletePrevChar:
                    if (cursor > 0)
                    {
                        chars.RemoveAt(cursor - 1);
                        cursor--;
                    }
                    break;
                case EditorAction.DeletePrevWord:
                {
                    var newCursor = FindPreviousWordBoundary(chars, cursor);
                    if (newCursor < cursor)
                    {
                        chars.RemoveRange(newCursor, cursor - newCursor);
                        cursor = newCursor;
                    }
                    break;
                }
                case EditorAction.DeleteNextChar:
                    if (cursor < chars.Count)
                    {
                        chars.RemoveAt(cursor);
                    }
                    break;
                case EditorAction.DeleteNextWord:
                {
                    var nextBoundary = FindNextWordBoundary(chars, cursor);
                    if (nextBoundary > cursor)
                    {
                        chars.RemoveRange(cursor, nextBoundary - cursor);
                    }
                    break;
                }
                case EditorAction.CursorLeft:
                    if (cursor > 0) cursor--;
                    break;
                case EditorAction.CursorRight:
                    if (cursor < chars.Count) cursor++;
                    break;
                case EditorAction.CursorPrevWord:
                    cursor = FindPreviousWordBoundary(chars, cursor);
                    break;
                case EditorAction.CursorNextWord:
                    cursor = FindNextWordBoundary(chars, cursor);
                    break;
                case EditorAction.CursorLineStart:
                    cursor = 0;
                    break;
                case EditorAction.CursorLineEnd:
                    cursor = chars.Count;
                    break;
                case EditorAction.HistoryPrev:
                {
                    var snapshot = _history.Peek(historyOffset + 1);
                    if (snapshot is not null)
                    {
                        historyOffset++;
                        chars.Clear();
                        chars.AddRange(snapshot);
                        cursor = chars.Count;
                    }
                    break;
                }
                case EditorAction.HistoryNext:
                {
                    if (historyOffset > 0)
                    {
                        historyOffset--;
                        var snapshot = _history.Peek(historyOffset);
                        chars.Clear();
                        if (snapshot is not null)
                        {
                            chars.AddRange(snapshot);
                        }

                        cursor = chars.Count;
                    }
                    else if (historyOffset == 0)
                    {
                        historyOffset = -1;
                        chars.Clear();
                        cursor = 0;
                    }
                    break;
                }
                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) &&
                        (key.Modifiers & ConsoleModifiers.Control) == 0)
                    {
                        chars.Insert(cursor, key.KeyChar);
                        cursor++;
                    }
                    break;
            }

            _renderer.Render(new string(chars.ToArray()), cursor);
        }
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

public sealed record InputResult(InputResultKind Kind, string? Text)
{
    public static InputResult Submitted(string text) => new(InputResultKind.Submitted, text);
    public static InputResult Cancelled { get; } = new(InputResultKind.Cancelled, null);
}

public enum InputResultKind
{
    Submitted,
    Cancelled
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
