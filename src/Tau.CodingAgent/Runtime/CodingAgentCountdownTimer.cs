namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentCountdownTimer : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<int> _onTick;
    private readonly Action _onExpire;
    private readonly Action? _requestRender;
    private Timer? _timer;
    private int _remainingSeconds;
    private bool _disposed;

    public CodingAgentCountdownTimer(
        TimeSpan timeout,
        Action<int> onTick,
        Action onExpire,
        Action? requestRender = null,
        TimeSpan? tickInterval = null)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        ArgumentNullException.ThrowIfNull(onExpire);

        _onTick = onTick;
        _onExpire = onExpire;
        _requestRender = requestRender;
        _remainingSeconds = (int)Math.Ceiling(Math.Max(0, timeout.TotalMilliseconds) / 1000d);

        _onTick(_remainingSeconds);

        var interval = tickInterval ?? TimeSpan.FromSeconds(1);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tickInterval), "Tick interval must be positive.");
        }

        _timer = new Timer(static state => ((CodingAgentCountdownTimer)state!).Tick(), this, interval, interval);
    }

    public int RemainingSeconds
    {
        get
        {
            lock (_gate)
            {
                return _remainingSeconds;
            }
        }
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    internal void Tick()
    {
        Action<int>? onTick = null;
        Action? requestRender = null;
        Action? onExpire = null;
        Timer? timerToDispose = null;
        int remaining;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _remainingSeconds--;
            remaining = _remainingSeconds;
            onTick = _onTick;
            requestRender = _requestRender;
            if (_remainingSeconds <= 0)
            {
                _disposed = true;
                timerToDispose = _timer;
                _timer = null;
                onExpire = _onExpire;
            }
        }

        onTick(remaining);
        requestRender?.Invoke();
        timerToDispose?.Dispose();
        onExpire?.Invoke();
    }
}
