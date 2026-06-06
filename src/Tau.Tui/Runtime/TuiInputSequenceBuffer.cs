using System.Text;
using System.Text.RegularExpressions;

namespace Tau.Tui.Runtime;

public sealed class TuiInputSequenceBuffer : IDisposable
{
    private const string Escape = "\u001b";
    private const string BracketedPasteStart = "\u001b[200~";
    private const string BracketedPasteEnd = "\u001b[201~";

    private static readonly Regex SgrMousePattern =
        new("^<\\d+;\\d+;\\d+[Mm]$", RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly TimeSpan? _timeout;
    private string _buffer = string.Empty;
    private string _pasteBuffer = string.Empty;
    private bool _pasteMode;
    private Timer? _timer;
    private bool _disposed;

    public TuiInputSequenceBuffer(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMilliseconds(10);
    }

    public event Action<string>? Data;
    public event Action<string>? Paste;

    public string Pending
    {
        get
        {
            lock (_sync)
            {
                return _buffer;
            }
        }
    }

    public bool IsPasteMode
    {
        get
        {
            lock (_sync)
            {
                return _pasteMode;
            }
        }
    }

    public void Process(string data)
    {
        ThrowIfDisposed();

        List<BufferedEvent> events;
        lock (_sync)
        {
            CancelTimerCore();
            events = ProcessCore(data ?? string.Empty);
            ScheduleTimerCore();
        }

        Emit(events);
    }

    public void Process(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();

        var text = data.Length == 1 && data[0] > 127
            ? $"{Escape}{(char)(data[0] - 128)}"
            : Encoding.UTF8.GetString(data);

        Process(text);
    }

    public IReadOnlyList<string> Flush()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            CancelTimerCore();
            return FlushCore();
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            CancelTimerCore();
            ClearCore();
        }
    }

    public void Destroy() => Dispose();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            CancelTimerCore();
            ClearCore();
            _disposed = true;
        }
    }

    private List<BufferedEvent> ProcessCore(string data)
    {
        var events = new List<BufferedEvent>();
        if (data.Length == 0 && _buffer.Length == 0)
        {
            events.Add(BufferedEvent.Data(string.Empty));
            return events;
        }

        _buffer += data;

        if (_pasteMode)
        {
            _pasteBuffer += _buffer;
            _buffer = string.Empty;
            TryCompletePaste(events);
            return events;
        }

        var startIndex = _buffer.IndexOf(BracketedPasteStart, StringComparison.Ordinal);
        if (startIndex != -1)
        {
            if (startIndex > 0)
            {
                var beforePaste = _buffer[..startIndex];
                var result = ExtractCompleteSequences(beforePaste);
                events.AddRange(result.Sequences.Select(BufferedEvent.Data));
            }

            _buffer = _buffer[(startIndex + BracketedPasteStart.Length)..];
            _pasteMode = true;
            _pasteBuffer = _buffer;
            _buffer = string.Empty;
            TryCompletePaste(events);
            return events;
        }

        var extracted = ExtractCompleteSequences(_buffer);
        _buffer = extracted.Remainder;
        events.AddRange(extracted.Sequences.Select(BufferedEvent.Data));
        return events;
    }

    private void TryCompletePaste(List<BufferedEvent> events)
    {
        var endIndex = _pasteBuffer.IndexOf(BracketedPasteEnd, StringComparison.Ordinal);
        if (endIndex == -1)
        {
            return;
        }

        var pastedContent = _pasteBuffer[..endIndex];
        var remaining = _pasteBuffer[(endIndex + BracketedPasteEnd.Length)..];
        _pasteMode = false;
        _pasteBuffer = string.Empty;
        events.Add(BufferedEvent.Paste(pastedContent));

        if (remaining.Length > 0)
        {
            events.AddRange(ProcessCore(remaining));
        }
    }

    private IReadOnlyList<string> FlushCore()
    {
        if (_buffer.Length == 0)
        {
            return [];
        }

        var sequences = new[] { _buffer };
        _buffer = string.Empty;
        return sequences;
    }

    private void ClearCore()
    {
        _buffer = string.Empty;
        _pasteMode = false;
        _pasteBuffer = string.Empty;
    }

    private void ScheduleTimerCore()
    {
        if (_timeout is null || _buffer.Length == 0)
        {
            return;
        }

        _timer = new Timer(OnTimeout, null, _timeout.Value, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimerCore()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTimeout(object? state)
    {
        List<BufferedEvent> events;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            CancelTimerCore();
            events = FlushCore().Select(BufferedEvent.Data).ToList();
        }

        Emit(events);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TuiInputSequenceBuffer));
        }
    }

    private void Emit(IEnumerable<BufferedEvent> events)
    {
        foreach (var bufferedEvent in events)
        {
            if (bufferedEvent.Kind == BufferedEventKind.Paste)
            {
                Paste?.Invoke(bufferedEvent.Value);
            }
            else
            {
                Data?.Invoke(bufferedEvent.Value);
            }
        }
    }

    private static SequenceExtractionResult ExtractCompleteSequences(string buffer)
    {
        var sequences = new List<string>();
        var position = 0;

        while (position < buffer.Length)
        {
            var remaining = buffer[position..];
            if (remaining.StartsWith(Escape, StringComparison.Ordinal))
            {
                var sequenceEnd = 1;
                while (sequenceEnd <= remaining.Length)
                {
                    var candidate = remaining[..sequenceEnd];
                    var status = GetSequenceStatus(candidate);
                    if (status == SequenceStatus.Complete)
                    {
                        sequences.Add(candidate);
                        position += sequenceEnd;
                        break;
                    }

                    if (status == SequenceStatus.Incomplete)
                    {
                        sequenceEnd++;
                        continue;
                    }

                    sequences.Add(candidate);
                    position += sequenceEnd;
                    break;
                }

                if (sequenceEnd > remaining.Length)
                {
                    return new SequenceExtractionResult(sequences, remaining);
                }
            }
            else
            {
                sequences.Add(remaining[0].ToString());
                position++;
            }
        }

        return new SequenceExtractionResult(sequences, string.Empty);
    }

    private static SequenceStatus GetSequenceStatus(string data)
    {
        if (!data.StartsWith(Escape, StringComparison.Ordinal))
        {
            return SequenceStatus.NotEscape;
        }

        if (data.Length == 1)
        {
            return SequenceStatus.Incomplete;
        }

        var afterEscape = data[1..];
        if (afterEscape.StartsWith("[", StringComparison.Ordinal))
        {
            if (afterEscape.StartsWith("[M", StringComparison.Ordinal))
            {
                return data.Length >= 6 ? SequenceStatus.Complete : SequenceStatus.Incomplete;
            }

            return GetCsiSequenceStatus(data);
        }

        if (afterEscape.StartsWith("]", StringComparison.Ordinal))
        {
            return GetStringSequenceStatus(data, "\u001b]");
        }

        if (afterEscape.StartsWith("P", StringComparison.Ordinal))
        {
            return GetStringSequenceStatus(data, "\u001bP");
        }

        if (afterEscape.StartsWith("_", StringComparison.Ordinal))
        {
            return GetStringSequenceStatus(data, "\u001b_");
        }

        if (afterEscape.StartsWith("O", StringComparison.Ordinal))
        {
            return afterEscape.Length >= 2 ? SequenceStatus.Complete : SequenceStatus.Incomplete;
        }

        return SequenceStatus.Complete;
    }

    private static SequenceStatus GetCsiSequenceStatus(string data)
    {
        if (!data.StartsWith("\u001b[", StringComparison.Ordinal))
        {
            return SequenceStatus.Complete;
        }

        if (data.Length < 3)
        {
            return SequenceStatus.Incomplete;
        }

        var payload = data[2..];
        var lastChar = payload[^1];
        if (lastChar < 0x40 || lastChar > 0x7e)
        {
            return SequenceStatus.Incomplete;
        }

        if (payload.StartsWith("<", StringComparison.Ordinal))
        {
            if (SgrMousePattern.IsMatch(payload))
            {
                return SequenceStatus.Complete;
            }

            if (lastChar is 'M' or 'm')
            {
                var parts = payload[1..^1].Split(';');
                if (parts.Length == 3 && parts.All(static part => part.Length > 0 && part.All(char.IsDigit)))
                {
                    return SequenceStatus.Complete;
                }
            }

            return SequenceStatus.Incomplete;
        }

        return SequenceStatus.Complete;
    }

    private static SequenceStatus GetStringSequenceStatus(string data, string prefix)
    {
        if (!data.StartsWith(prefix, StringComparison.Ordinal))
        {
            return SequenceStatus.Complete;
        }

        return data.EndsWith("\u001b\\", StringComparison.Ordinal) || data.EndsWith('\u0007')
            ? SequenceStatus.Complete
            : SequenceStatus.Incomplete;
    }

    private readonly record struct SequenceExtractionResult(IReadOnlyList<string> Sequences, string Remainder);

    private readonly record struct BufferedEvent(BufferedEventKind Kind, string Value)
    {
        public static BufferedEvent Data(string value) => new(BufferedEventKind.Data, value);

        public static BufferedEvent Paste(string value) => new(BufferedEventKind.Paste, value);
    }

    private enum BufferedEventKind
    {
        Data,
        Paste,
    }

    private enum SequenceStatus
    {
        Complete,
        Incomplete,
        NotEscape,
    }
}
