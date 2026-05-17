using Tau.Tui.Abstractions;

namespace Tau.Tui.Runtime;

public sealed class InteractiveInputEditor
{
    private readonly IConsoleKeyReader _reader;
    private readonly IInteractiveRenderer _renderer;
    private readonly InputHistory _history;
    private readonly InputBuffer _buffer;

    public InteractiveInputEditor(
        IConsoleKeyReader reader,
        IInteractiveRenderer renderer,
        InputBuffer? buffer = null,
        InputHistory? history = null)
    {
        _reader = reader;
        _renderer = renderer;
        _buffer = buffer ?? new InputBuffer();
        _history = history ?? new InputHistory();
    }

    public InputBuffer Buffer => _buffer;
    public InputHistory History => _history;

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

            // Ctrl-C cancels the line.
            if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.C)
            {
                _renderer.Cancel();
                _buffer.SetDraft(new string(chars.ToArray()));
                return InputResult.Cancelled;
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
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
                case ConsoleKey.Backspace:
                    if (cursor > 0)
                    {
                        chars.RemoveAt(cursor - 1);
                        cursor--;
                    }
                    break;
                case ConsoleKey.Delete:
                    if (cursor < chars.Count)
                    {
                        chars.RemoveAt(cursor);
                    }
                    break;
                case ConsoleKey.LeftArrow:
                    if (cursor > 0)
                    {
                        cursor--;
                    }
                    break;
                case ConsoleKey.RightArrow:
                    if (cursor < chars.Count)
                    {
                        cursor++;
                    }
                    break;
                case ConsoleKey.Home:
                    cursor = 0;
                    break;
                case ConsoleKey.End:
                    cursor = chars.Count;
                    break;
                case ConsoleKey.UpArrow:
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
                case ConsoleKey.DownArrow:
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
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        chars.Insert(cursor, key.KeyChar);
                        cursor++;
                    }
                    break;
            }

            _renderer.Render(new string(chars.ToArray()), cursor);
        }
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

    public InputHistory(int capacity = 200)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int Count => _entries.Count;

    public void Add(string entry)
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
    }

    public string? Peek(int offsetFromEnd)
    {
        if (offsetFromEnd < 0 || offsetFromEnd >= _entries.Count)
        {
            return null;
        }

        return _entries[_entries.Count - 1 - offsetFromEnd];
    }
}
