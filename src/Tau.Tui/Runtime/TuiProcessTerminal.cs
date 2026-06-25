using System.Text;
using System.Text.RegularExpressions;

namespace Tau.Tui.Runtime;

public interface ITuiProcessTerminalTransport
{
    bool IsRawMode { get; }
    bool IsWindows { get; }
    int Columns { get; }
    int Rows { get; }
    void SetRawMode(bool enabled);
    void SetInputEncoding(Encoding encoding);
    void ResumeInput();
    void PauseInput();
    void RefreshDimensions();
    void EnableVirtualTerminalInput();
    IDisposable SubscribeInput(Action<string> handler);
    IDisposable SubscribeResize(Action handler);
    void Write(string data);
}

public interface ITuiTerminalTimer
{
    IDisposable Schedule(TimeSpan dueTime, Action callback);
}

public sealed class TuiTerminalWriteLog
{
    private readonly object _sync = new();

    private TuiTerminalWriteLog(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static TuiTerminalWriteLog? FromEnvironment(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<DateTimeOffset>? clock = null,
        int? processId = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var configuredPath =
            getEnvironmentVariable("TAU_TUI_WRITE_LOG")
            ?? getEnvironmentVariable("PI_TUI_WRITE_LOG");

        return FromPath(configuredPath, clock, processId);
    }

    public static TuiTerminalWriteLog? FromPath(
        string? configuredPath,
        Func<DateTimeOffset>? clock = null,
        int? processId = null)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var filePath = ResolveFilePath(configuredPath, clock, processId);
        return new TuiTerminalWriteLog(filePath);
    }

    public void Append(string data)
    {
        if (data.Length == 0)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                File.AppendAllText(FilePath, data, Encoding.UTF8);
            }
        }
        catch
        {
            // TUI write logging is diagnostic-only and must never break terminal output.
        }
    }

    private static string ResolveFilePath(
        string configuredPath,
        Func<DateTimeOffset>? clock,
        int? processId)
    {
        if (!Directory.Exists(configuredPath))
        {
            return configuredPath;
        }

        var now = (clock ?? (() => DateTimeOffset.Now))();
        var timestamp = now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var pid = processId ?? Environment.ProcessId;
        return Path.Combine(configuredPath, $"tui-{timestamp}-{pid}.log");
    }
}

public sealed class SystemTuiTerminalTimer : ITuiTerminalTimer
{
    public IDisposable Schedule(TimeSpan dueTime, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Timer? timer = null;
        timer = new Timer(
            _ =>
            {
                timer?.Dispose();
                callback();
            },
            null,
            dueTime,
            Timeout.InfiniteTimeSpan);
        return timer;
    }
}

public sealed class TuiProcessTerminal
{
    public const string EnableBracketedPaste = "\u001b[?2004h";
    public const string DisableBracketedPaste = "\u001b[?2004l";
    public const string QueryKittyKeyboardProtocol = "\u001b[?u";
    public const string EnableKittyKeyboardProtocol = "\u001b[>7u";
    public const string DisableKittyKeyboardProtocol = "\u001b[<u";
    public const string EnableModifyOtherKeys = "\u001b[>4;2m";
    public const string DisableModifyOtherKeys = "\u001b[>4;0m";
    public const string HideCursorSequence = "\u001b[?25l";
    public const string ShowCursorSequence = "\u001b[?25h";
    public const string ClearLineSequence = "\u001b[K";
    public const string ClearFromCursorSequence = "\u001b[J";
    public const string ClearScreenSequence = "\u001b[2J\u001b[H";
    public const string QueryBackgroundColorSequence = "\u001b]11;?\u0007";
    public const string QueryColorSchemeSequence = "\u001b[?996n";

    private static readonly Regex KittyProtocolResponsePattern =
        new("^\u001b\\[\\?(\\d+)u$", RegexOptions.CultureInvariant);

    private readonly ITuiProcessTerminalTransport _transport;
    private readonly ITuiTerminalTimer _timer;
    private readonly TimeSpan _modifyOtherKeysDelay;
    private readonly TuiTerminalWriteLog? _writeLog;
    private readonly object _sync = new();
    private Action<string>? _inputHandler;
    private Action? _resizeHandler;
    private bool _wasRaw;
    private bool _started;
    private bool _kittyProtocolActive;
    private bool _modifyOtherKeysActive;
    private int _pendingBackgroundColorReplies;
    private readonly Queue<PendingBackgroundColorQuery> _pendingBackgroundColorQueries = [];
    private readonly List<PendingColorSchemeQuery> _pendingColorSchemeQueries = [];
    private TuiInputSequenceBuffer? _inputSequenceBuffer;
    private IDisposable? _inputSubscription;
    private IDisposable? _resizeSubscription;
    private IDisposable? _modifyOtherKeysTimer;

    public event Action<TuiTerminalColorScheme>? TerminalColorSchemeChanged;

    public TuiProcessTerminal(
        ITuiProcessTerminalTransport transport,
        ITuiTerminalTimer? timer = null,
        TimeSpan? modifyOtherKeysDelay = null,
        TuiTerminalWriteLog? writeLog = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timer = timer ?? new SystemTuiTerminalTimer();
        _modifyOtherKeysDelay = modifyOtherKeysDelay ?? TimeSpan.FromMilliseconds(150);
        _writeLog = writeLog;
    }

    public int Columns => Math.Max(1, _transport.Columns);

    public int Rows => Math.Max(1, _transport.Rows);

    public bool KittyProtocolActive
    {
        get
        {
            lock (_sync)
            {
                return _kittyProtocolActive;
            }
        }
    }

    public bool ModifyOtherKeysActive
    {
        get
        {
            lock (_sync)
            {
                return _modifyOtherKeysActive;
            }
        }
    }

    public void Start(Action<string> onInput, Action onResize)
    {
        ArgumentNullException.ThrowIfNull(onInput);
        ArgumentNullException.ThrowIfNull(onResize);

        lock (_sync)
        {
            if (_started)
            {
                throw new InvalidOperationException("TuiProcessTerminal is already started.");
            }

            _inputHandler = onInput;
            _resizeHandler = onResize;
            _wasRaw = _transport.IsRawMode;
            _kittyProtocolActive = false;
            _modifyOtherKeysActive = false;
            TuiKeyDecoder.SetKittyProtocolActive(false);

            _transport.SetRawMode(enabled: true);
            _transport.SetInputEncoding(Encoding.UTF8);
            _transport.ResumeInput();
            _transport.Write(EnableBracketedPaste);
            _resizeSubscription = _transport.SubscribeResize(onResize);

            if (_transport.IsWindows)
            {
                _transport.EnableVirtualTerminalInput();
            }
            else
            {
                _transport.RefreshDimensions();
            }

            SetupInputSequenceBufferCore();
            _inputSubscription = _transport.SubscribeInput(ProcessInput);
            _transport.Write(QueryKittyKeyboardProtocol);
            _modifyOtherKeysTimer = _timer.Schedule(_modifyOtherKeysDelay, EnableModifyOtherKeysIfNeeded);
            _started = true;
        }
    }

    public async Task DrainInputAsync(
        TimeSpan? max = null,
        TimeSpan? idle = null,
        CancellationToken cancellationToken = default)
    {
        var maxDuration = max ?? TimeSpan.FromSeconds(1);
        var idleDuration = idle ?? TimeSpan.FromMilliseconds(50);
        IDisposable? drainSubscription = null;
        Action<string>? previousHandler;
        var lastDataTime = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            DisableKeyboardProtocolsCore();
            previousHandler = _inputHandler;
            _inputHandler = null;
            drainSubscription = _transport.SubscribeInput(_ => lastDataTime = DateTimeOffset.UtcNow);
        }

        var endTime = DateTimeOffset.UtcNow + maxDuration;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                if (now >= endTime || now - lastDataTime >= idleDuration)
                {
                    break;
                }

                var remainingMax = endTime - now;
                var delay = remainingMax < idleDuration ? remainingMax : idleDuration;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            drainSubscription?.Dispose();
            lock (_sync)
            {
                if (_started)
                {
                    _inputHandler = previousHandler;
                }
            }
        }
    }

    public void Stop()
    {
        IReadOnlyList<TaskCompletionSource<TuiRgbColor?>> backgroundCompletions = [];
        IReadOnlyList<TaskCompletionSource<TuiTerminalColorScheme?>> colorSchemeCompletions = [];

        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _transport.Write(DisableBracketedPaste);
            DisableKeyboardProtocolsCore();
            _inputSequenceBuffer?.Destroy();
            _inputSequenceBuffer = null;
            _inputSubscription?.Dispose();
            _inputSubscription = null;
            _inputHandler = null;
            _resizeSubscription?.Dispose();
            _resizeSubscription = null;
            _resizeHandler = null;
            _modifyOtherKeysTimer?.Dispose();
            _modifyOtherKeysTimer = null;
            _transport.PauseInput();
            _transport.SetRawMode(_wasRaw);
            _started = false;
            (backgroundCompletions, colorSchemeCompletions) = ClearPendingTerminalQueriesCore();
        }

        foreach (var completion in backgroundCompletions)
        {
            completion.TrySetResult(null);
        }

        foreach (var completion in colorSchemeCompletions)
        {
            completion.TrySetResult(null);
        }
    }

    public void Write(string data)
    {
        data ??= string.Empty;
        _transport.Write(data);
        _writeLog?.Append(data);
    }

    public void MoveBy(int lines)
    {
        if (lines > 0)
        {
            _transport.Write($"\u001b[{lines}B");
        }
        else if (lines < 0)
        {
            _transport.Write($"\u001b[{-lines}A");
        }
    }

    public void HideCursor() => _transport.Write(HideCursorSequence);

    public void ShowCursor() => _transport.Write(ShowCursorSequence);

    public void ClearLine() => _transport.Write(ClearLineSequence);

    public void ClearFromCursor() => _transport.Write(ClearFromCursorSequence);

    public void ClearScreen() => _transport.Write(ClearScreenSequence);

    public void SetTitle(string title) => _transport.Write($"\u001b]0;{title ?? string.Empty}\u0007");

    public Task<TuiRgbColor?> QueryTerminalBackgroundColorAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var query = new PendingBackgroundColorQuery();
        lock (_sync)
        {
            query.Timer = _timer.Schedule(timeout ?? TimeSpan.FromSeconds(1), () => TimeoutBackgroundColorQuery(query));
            _pendingBackgroundColorQueries.Enqueue(query);
            _pendingBackgroundColorReplies++;
            _transport.Write(QueryBackgroundColorSequence);
        }

        return query.Completion.Task.WaitAsync(cancellationToken);
    }

    public Task<TuiTerminalColorScheme?> QueryTerminalColorSchemeAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var query = new PendingColorSchemeQuery();
        lock (_sync)
        {
            query.Timer = _timer.Schedule(timeout ?? TimeSpan.FromSeconds(1), () => TimeoutColorSchemeQuery(query));
            _pendingColorSchemeQueries.Add(query);
            _transport.Write(QueryColorSchemeSequence);
        }

        return query.Completion.Task.WaitAsync(cancellationToken);
    }

    private void SetupInputSequenceBufferCore()
    {
        _inputSequenceBuffer = new TuiInputSequenceBuffer();
        _inputSequenceBuffer.Data += sequence =>
        {
            lock (_sync)
            {
                if (!_kittyProtocolActive && KittyProtocolResponsePattern.IsMatch(sequence))
                {
                    _kittyProtocolActive = true;
                    TuiKeyDecoder.SetKittyProtocolActive(true);
                    _transport.Write(EnableKittyKeyboardProtocol);
                    return;
                }

                if (ConsumeBackgroundColorResponseCore(sequence) ||
                    ConsumeColorSchemeReportCore(sequence))
                {
                    return;
                }

                _inputHandler?.Invoke(sequence);
            }
        };
        _inputSequenceBuffer.Paste += content =>
        {
            lock (_sync)
            {
                _inputHandler?.Invoke($"\u001b[200~{content}\u001b[201~");
            }
        };
    }

    private void ProcessInput(string data)
    {
        lock (_sync)
        {
            _inputSequenceBuffer?.Process(data);
        }
    }

    private void EnableModifyOtherKeysIfNeeded()
    {
        lock (_sync)
        {
            _modifyOtherKeysTimer?.Dispose();
            _modifyOtherKeysTimer = null;
            if (_started && !_kittyProtocolActive && !_modifyOtherKeysActive)
            {
                _transport.Write(EnableModifyOtherKeys);
                _modifyOtherKeysActive = true;
            }
        }
    }

    private bool ConsumeBackgroundColorResponseCore(string sequence)
    {
        if (_pendingBackgroundColorReplies <= 0 ||
            !TuiTerminalColors.IsOsc11BackgroundColorResponse(sequence))
        {
            return false;
        }

        var color = TuiTerminalColors.ParseOsc11BackgroundColor(sequence);
        _pendingBackgroundColorReplies--;
        var query = _pendingBackgroundColorQueries.Count > 0
            ? _pendingBackgroundColorQueries.Dequeue()
            : null;

        if (query is null || query.Settled)
        {
            return true;
        }

        query.Settled = true;
        query.Timer?.Dispose();
        query.Timer = null;
        query.Completion.TrySetResult(color);
        return true;
    }

    private bool ConsumeColorSchemeReportCore(string sequence)
    {
        var scheme = TuiTerminalColors.ParseTerminalColorSchemeReport(sequence);
        if (scheme is null)
        {
            return false;
        }

        foreach (var query in _pendingColorSchemeQueries.ToArray())
        {
            if (query.Settled)
            {
                continue;
            }

            query.Settled = true;
            query.Timer?.Dispose();
            query.Timer = null;
            query.Completion.TrySetResult(scheme);
        }

        _pendingColorSchemeQueries.RemoveAll(static query => query.Settled);
        TerminalColorSchemeChanged?.Invoke(scheme.Value);
        return true;
    }

    private void TimeoutBackgroundColorQuery(PendingBackgroundColorQuery query)
    {
        lock (_sync)
        {
            if (query.Settled)
            {
                return;
            }

            query.Settled = true;
            query.Timer = null;
            query.Completion.TrySetResult(null);
        }
    }

    private void TimeoutColorSchemeQuery(PendingColorSchemeQuery query)
    {
        lock (_sync)
        {
            if (query.Settled)
            {
                return;
            }

            query.Settled = true;
            query.Timer = null;
            _pendingColorSchemeQueries.Remove(query);
            query.Completion.TrySetResult(null);
        }
    }

    private (IReadOnlyList<TaskCompletionSource<TuiRgbColor?>>,
        IReadOnlyList<TaskCompletionSource<TuiTerminalColorScheme?>>) ClearPendingTerminalQueriesCore()
    {
        var backgroundCompletions = new List<TaskCompletionSource<TuiRgbColor?>>();
        while (_pendingBackgroundColorQueries.Count > 0)
        {
            var query = _pendingBackgroundColorQueries.Dequeue();
            query.Timer?.Dispose();
            query.Timer = null;
            if (!query.Settled)
            {
                query.Settled = true;
                backgroundCompletions.Add(query.Completion);
            }
        }
        _pendingBackgroundColorReplies = 0;

        var colorSchemeCompletions = new List<TaskCompletionSource<TuiTerminalColorScheme?>>();
        foreach (var query in _pendingColorSchemeQueries)
        {
            query.Timer?.Dispose();
            query.Timer = null;
            if (!query.Settled)
            {
                query.Settled = true;
                colorSchemeCompletions.Add(query.Completion);
            }
        }
        _pendingColorSchemeQueries.Clear();

        return (backgroundCompletions, colorSchemeCompletions);
    }

    private void DisableKeyboardProtocolsCore()
    {
        _modifyOtherKeysTimer?.Dispose();
        _modifyOtherKeysTimer = null;

        if (_kittyProtocolActive)
        {
            _transport.Write(DisableKittyKeyboardProtocol);
            _kittyProtocolActive = false;
        }
        TuiKeyDecoder.SetKittyProtocolActive(false);

        if (_modifyOtherKeysActive)
        {
            _transport.Write(DisableModifyOtherKeys);
            _modifyOtherKeysActive = false;
        }
    }

    private sealed class PendingBackgroundColorQuery
    {
        public TaskCompletionSource<TuiRgbColor?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Settled { get; set; }

        public IDisposable? Timer { get; set; }
    }

    private sealed class PendingColorSchemeQuery
    {
        public TaskCompletionSource<TuiTerminalColorScheme?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Settled { get; set; }

        public IDisposable? Timer { get; set; }
    }
}
