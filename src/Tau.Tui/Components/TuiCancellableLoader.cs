using Tau.Tui.Abstractions;

namespace Tau.Tui.Components;

public sealed class TuiCancellableLoader : ITuiInputComponent, IDisposable
{
    private readonly TuiLoader _loader;
    private readonly CancellationTokenSource _abortSource = new();
    private bool _disposed;

    public TuiCancellableLoader(
        string message = "Loading...",
        Func<string, string>? spinnerFormatter = null,
        Func<string, string>? messageFormatter = null,
        Action? requestRender = null,
        TimeSpan? frameInterval = null,
        bool autoStart = false,
        IReadOnlyList<string>? frames = null)
        : this(new TuiLoader(
            message,
            spinnerFormatter,
            messageFormatter,
            requestRender,
            frameInterval,
            autoStart,
            frames))
    {
    }

    public TuiCancellableLoader(TuiLoader loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    public event Action? Aborted;

    public CancellationToken Signal => _abortSource.Token;

    public bool IsAborted => _abortSource.IsCancellationRequested;

    public TuiLoader Loader => _loader;

    public IReadOnlyList<string> Render(int width) => _loader.Render(width);

    public void Invalidate() => _loader.Invalidate();

    public TuiInputResult HandleInput(ConsoleKeyInfo key)
    {
        if (!IsCancel(key))
        {
            return TuiInputResult.Ignored;
        }

        Abort();
        return TuiInputResult.Handled;
    }

    public void Abort()
    {
        ThrowIfDisposed();
        if (_abortSource.IsCancellationRequested)
        {
            return;
        }

        _abortSource.Cancel();
        Aborted?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loader.Dispose();
        _abortSource.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool IsCancel(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Escape ||
        ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.C);
}
