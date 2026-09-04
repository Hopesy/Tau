using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public sealed class TuiCompositionSession
{
    private BindingHandle? _binding;

    /// <summary>
    /// 创建组合式终端会话。
    /// </summary>
    /// <param name="surface">负责输出终端帧的渲染表面。</param>
    /// <param name="keyReader">可选的控制台按键读取器。</param>
    /// <param name="messages">初始消息集合。</param>
    /// <param name="statusLeft">状态栏左侧初始文本。</param>
    /// <param name="statusRight">状态栏右侧初始文本。</param>
    /// <param name="autoRender">状态变化后是否自动渲染。</param>
    /// <param name="maxScrollbackLines">滚动缓冲区最大行数。</param>
    /// <param name="displayOptions">消息区域显示主题。</param>
    /// <param name="statusTheme">状态栏显示主题。</param>
    public TuiCompositionSession(
        ITuiRenderSurface surface,
        IConsoleKeyReader? keyReader = null,
        IEnumerable<TuiMessage>? messages = null,
        string statusLeft = "",
        string statusRight = "",
        bool autoRender = true,
        int maxScrollbackLines = 10_000,
        TuiMessageDisplayOptions? displayOptions = null,
        TuiStatusBarTheme? statusTheme = null)
        : this(new TuiCompositionHost(
            surface,
            keyReader,
            messages,
            statusLeft,
            statusRight,
            autoRender,
            maxScrollbackLines,
            displayOptions,
            statusTheme))
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

    /// <summary>
    /// 【终端布局】【尺寸同步】同步当前终端尺寸而不产生中间绘制，用于输入覆盖层重新计算位置。
    /// </summary>
    public void RefreshViewportSize() => Host.RefreshViewportSize();

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

    /// <summary>
    /// 设置输入框等底部固定区域的占位行数。
    /// </summary>
    /// <param name="lines">需要预留的行数。</param>
    /// <returns>自动渲染产生的结果；关闭自动渲染时返回 <see langword="null"/>。</returns>
    public TuiTranscriptRenderResult? SetReservedBottomLines(int lines) =>
        Host.SetReservedBottomLines(lines);

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
