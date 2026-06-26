using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public sealed class TuiCompositionSession
{
    private BindingHandle? _binding;

    public TuiCompositionSession(
        ITuiRenderSurface surface,
        IConsoleKeyReader? keyReader = null,
        IEnumerable<TuiMessage>? messages = null,
        string statusLeft = "",
        string statusRight = "",
        bool autoRender = true,
        int maxScrollbackLines = 10_000)
        : this(new TuiCompositionHost(
            surface,
            keyReader,
            messages,
            statusLeft,
            statusRight,
            autoRender,
            maxScrollbackLines))
    {
    }

    public TuiCompositionSession(TuiCompositionHost host)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public TuiCompositionHost Host { get; }
    public TuiTranscriptViewportHost TranscriptHost => Host.TranscriptHost;
    public TuiTranscriptViewport Viewport => Host.Viewport;
    public bool AutoRender
    {
        get => Host.AutoRender;
        set => Host.AutoRender = value;
    }

    public bool IsStarted => Host.IsStarted;
    public TuiTranscriptRenderResult? LastRenderResult => Host.LastRenderResult;
    public bool HasVisibleOverlay => Host.HasVisibleOverlay;
    public bool HasFocusedInputOverlay => Host.HasFocusedInputOverlay;

    public TuiTranscriptRenderResult Start() => Host.Start();

    public void Stop() => Host.Stop();

    public TuiTranscriptRenderResult Render(bool force = false) => Host.Render(force);

    public TuiTranscriptRenderResult? SetMessages(IEnumerable<TuiMessage> messages) =>
        Host.SetMessages(messages);

    public TuiTranscriptRenderResult? SyncMessagesFrom(InteractiveConsoleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Host.SetMessages(session.SnapshotMessages());
    }

    public IDisposable BindTranscript(InteractiveConsoleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _binding?.Dispose();
        SyncMessagesFrom(session);

        void OnChanged() => SyncMessagesFrom(session);

        session.TranscriptChanged += OnChanged;
        _binding = new BindingHandle(session, OnChanged, () => _binding = null);
        return _binding;
    }

    public TuiTranscriptRenderResult? AppendMessage(TuiMessage message) =>
        Host.AppendMessage(message);

    public TuiTranscriptRenderResult? AppendMessages(IEnumerable<TuiMessage> messages) =>
        Host.AppendMessages(messages);

    public TuiTranscriptRenderResult? ClearMessages() => Host.ClearMessages();

    public TuiTranscriptRenderResult? SetStatus(string left, string right) =>
        Host.SetStatus(left, right);

    public TuiTranscriptRenderResult? SetStatusLines(IEnumerable<TuiStatusBarLine> lines) =>
        Host.SetStatusLines(lines);

    public TuiTranscriptOverlayHandle OpenOverlay(
        ITuiComponent component,
        TuiTranscriptOverlayOptions? options = null) =>
        Host.OpenOverlay(component, options);

    public TuiTranscriptRenderResult? CloseOverlay(TuiTranscriptOverlayHandle handle) =>
        Host.CloseOverlay(handle);

    public TuiCompositionInputResult HandleInput(ConsoleKeyInfo key) =>
        Host.HandleInput(key);

    public ValueTask<TuiCompositionInputResult> ReadInputAsync(CancellationToken cancellationToken = default) =>
        Host.ReadInputAsync(cancellationToken);

    private sealed class BindingHandle(
        InteractiveConsoleSession session,
        Action handler,
        Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            session.TranscriptChanged -= handler;
            _disposed = true;
            onDispose();
        }
    }
}
