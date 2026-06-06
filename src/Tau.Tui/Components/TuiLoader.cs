using Tau.Tui.Abstractions;

namespace Tau.Tui.Components;

public class TuiLoader : ITuiComponent, IDisposable
{
    private static readonly string[] DefaultFrames =
    [
        "\u280b",
        "\u2819",
        "\u2839",
        "\u2838",
        "\u283c",
        "\u2834",
        "\u2826",
        "\u2827",
        "\u2807",
        "\u280f"
    ];

    private readonly object _gate = new();
    private readonly TuiTextBlock _textBlock = new(paddingX: 1, paddingY: 0);
    private readonly IReadOnlyList<string> _frames;
    private readonly Func<string, string> _spinnerFormatter;
    private readonly Func<string, string> _messageFormatter;
    private readonly Action? _requestRender;
    private readonly TimeSpan _frameInterval;
    private string _message;
    private int _currentFrame;
    private Timer? _timer;
    private bool _disposed;

    public TuiLoader(
        string message = "Loading...",
        Func<string, string>? spinnerFormatter = null,
        Func<string, string>? messageFormatter = null,
        Action? requestRender = null,
        TimeSpan? frameInterval = null,
        bool autoStart = false,
        IReadOnlyList<string>? frames = null)
    {
        _message = message;
        _spinnerFormatter = spinnerFormatter ?? (static value => value);
        _messageFormatter = messageFormatter ?? (static value => value);
        _requestRender = requestRender;
        _frameInterval = frameInterval ?? TimeSpan.FromMilliseconds(80);
        _frames = ValidateFrames(frames ?? DefaultFrames);
        UpdateDisplayNoLock();

        if (autoStart)
        {
            Start();
        }
    }

    public string Message
    {
        get
        {
            lock (_gate)
            {
                return _message;
            }
        }
    }

    public int CurrentFrameIndex
    {
        get
        {
            lock (_gate)
            {
                return _currentFrame;
            }
        }
    }

    public string CurrentFrame
    {
        get
        {
            lock (_gate)
            {
                return _frames[_currentFrame];
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    public void Start()
    {
        Action? requestRender = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_timer is not null)
            {
                return;
            }

            _timer = new Timer(static state => ((TuiLoader)state!).AdvanceFrameFromTimer(), this, _frameInterval, _frameInterval);
            requestRender = _requestRender;
        }

        requestRender?.Invoke();
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    public void SetMessage(string message)
    {
        Action? requestRender = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.Equals(_message, message, StringComparison.Ordinal))
            {
                return;
            }

            _message = message;
            UpdateDisplayNoLock();
            requestRender = _requestRender;
        }

        requestRender?.Invoke();
    }

    public void AdvanceFrame() => AdvanceFrameCore(throwIfDisposed: true);

    public IReadOnlyList<string> Render(int width)
    {
        lock (_gate)
        {
            return ["", .. _textBlock.Render(width)];
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _textBlock.Invalidate();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private void AdvanceFrameFromTimer() => AdvanceFrameCore(throwIfDisposed: false);

    private void AdvanceFrameCore(bool throwIfDisposed)
    {
        Action? requestRender = null;
        lock (_gate)
        {
            if (_disposed)
            {
                if (throwIfDisposed)
                {
                    ThrowIfDisposed();
                }

                return;
            }

            _currentFrame = (_currentFrame + 1) % _frames.Count;
            UpdateDisplayNoLock();
            requestRender = _requestRender;
        }

        requestRender?.Invoke();
    }

    private void UpdateDisplayNoLock()
    {
        var frame = _frames[_currentFrame];
        _textBlock.SetText($"{_spinnerFormatter(frame)} {_messageFormatter(_message)}");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static IReadOnlyList<string> ValidateFrames(IReadOnlyList<string> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("Loader requires at least one spinner frame.", nameof(frames));
        }

        if (frames.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("Loader spinner frames cannot be empty.", nameof(frames));
        }

        return frames.ToArray();
    }
}
