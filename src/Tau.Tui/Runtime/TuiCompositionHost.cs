using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public enum TuiCompositionInputTarget
{
    None,
    Overlay,
    Transcript,
}

public readonly record struct TuiCompositionInputResult(
    bool Consumed,
    TuiCompositionInputTarget Target,
    TuiInputResult? OverlayResult,
    TuiTranscriptInputResult? TranscriptResult,
    TuiTranscriptRenderResult? RenderResult)
{
    public static TuiCompositionInputResult Ignored { get; } =
        new(false, TuiCompositionInputTarget.None, null, null, null);

    public static TuiCompositionInputResult FromOverlay(
        TuiInputResult overlayResult,
        TuiTranscriptRenderResult? renderResult) =>
        new(true, TuiCompositionInputTarget.Overlay, overlayResult, null, renderResult);

    public static TuiCompositionInputResult FromTranscript(
        TuiTranscriptInputResult transcriptResult) =>
        new(
            transcriptResult.Consumed,
            transcriptResult.Consumed ? TuiCompositionInputTarget.Transcript : TuiCompositionInputTarget.None,
            null,
            transcriptResult,
            transcriptResult.RenderResult);
}

public sealed class TuiCompositionHost
{
    private readonly IConsoleKeyReader? _keyReader;
    private readonly List<OverlayInputEntry> _inputOverlays = [];
    private readonly object _sync = new();

    /// <summary>
    /// 创建组合式终端渲染宿主，并初始化消息区、状态栏和覆盖层管理器。
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
    public TuiCompositionHost(
        ITuiRenderSurface surface,
        IConsoleKeyReader? keyReader = null,
        IEnumerable<TuiMessage>? messages = null,
        string statusLeft = "",
        string statusRight = "",
        bool autoRender = true,
        int maxScrollbackLines = 10_000,
        TuiMessageDisplayOptions? displayOptions = null,
        TuiStatusBarTheme? statusTheme = null)
    {
        TranscriptHost = new TuiTranscriptViewportHost(
            surface,
            messages,
            statusLeft,
            statusRight,
            maxScrollbackLines,
            displayOptions,
            statusTheme);
        _keyReader = keyReader;
        AutoRender = autoRender;
    }

    public TuiTranscriptViewportHost TranscriptHost { get; }
    public TuiTranscriptViewport Viewport => TranscriptHost.Viewport;
    public bool AutoRender { get; set; }
    public bool IsStarted { get; private set; }
    public TuiTranscriptRenderResult? LastRenderResult { get; private set; }
    public bool HasVisibleOverlay
    {
        get
        {
            lock (_sync)
            {
                return TranscriptHost.HasVisibleOverlay;
            }
        }
    }

    public bool HasFocusedInputOverlay
    {
        get
        {
            lock (_sync)
            {
                return FocusedInputOverlayCore() is not null;
            }
        }
    }

    public TuiTranscriptRenderResult Start()
    {
        lock (_sync)
        {
            if (!IsStarted)
            {
                IsStarted = true;
                TranscriptHost.ResetFrame();
                TranscriptHost.HideCursor();
            }

            return RenderCore(force: true);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            IsStarted = false;
            LastRenderResult = null;
            TranscriptHost.ShowCursor();
            TranscriptHost.ResetFrame();
        }
    }

    /// <summary>
    /// 【终端布局】【尺寸同步】在不输出中间帧的情况下同步终端尺寸和视口边界。
    /// </summary>
    public void RefreshViewportSize()
    {
        lock (_sync)
        {
            TranscriptHost.RefreshViewportSize();
        }
    }

    /// <summary>
    /// 【终端布局】【批量重绘】在会话锁内批量修改状态，并暂时禁止状态变更触发自动绘制。
    /// </summary>
    /// <param name="mutation">需要在单个终端帧内完成的状态修改。</param>
    internal void RunWithoutAutoRender(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_sync)
        {
            var autoRender = AutoRender;
            AutoRender = false;
            try
            {
                mutation();
            }
            finally
            {
                AutoRender = autoRender;
            }
        }
    }

    public TuiTranscriptRenderResult Render(bool force = false)
    {
        lock (_sync)
        {
            return RenderCore(force);
        }
    }

    public TuiTranscriptRenderResult? SetMessages(IEnumerable<TuiMessage> messages)
    {
        lock (_sync)
        {
            TranscriptHost.SetMessages(messages);
            return RenderAfterStateChangeCore();
        }
    }

    public TuiTranscriptRenderResult? AppendMessage(TuiMessage message)
    {
        lock (_sync)
        {
            TranscriptHost.AppendMessage(message);
            return RenderAfterStateChangeCore();
        }
    }

    public TuiTranscriptRenderResult? AppendMessages(IEnumerable<TuiMessage> messages)
    {
        lock (_sync)
        {
            TranscriptHost.AppendMessages(messages);
            return RenderAfterStateChangeCore();
        }
    }

    public TuiTranscriptRenderResult? ClearMessages()
    {
        lock (_sync)
        {
            TranscriptHost.ClearMessages();
            return RenderAfterStateChangeCore();
        }
    }

    public TuiTranscriptRenderResult? SetStatus(string left, string right)
    {
        lock (_sync)
        {
            TranscriptHost.SetStatus(left, right);
            return RenderAfterStateChangeCore();
        }
    }

    public TuiTranscriptRenderResult? SetStatusLines(IEnumerable<TuiStatusBarLine> lines)
    {
        lock (_sync)
        {
            TranscriptHost.SetStatusLines(lines);
            return RenderAfterStateChangeCore();
        }
    }

    /// <summary>
    /// 设置底部输入区域的占位行数，并在自动渲染开启时立即更新画面。
    /// </summary>
    /// <param name="lines">输入框或其他底部覆盖层需要占用的行数。</param>
    /// <returns>自动渲染产生的结果；关闭自动渲染时返回 <see langword="null"/>。</returns>
    public TuiTranscriptRenderResult? SetReservedBottomLines(int lines)
    {
        lock (_sync)
        {
            var normalized = Math.Clamp(lines, 0, TranscriptHost.Viewport.MessageHeight);
            if (TranscriptHost.Viewport.ReservedBottomLines == normalized)
            {
                return LastRenderResult;
            }

            TranscriptHost.SetReservedBottomLines(lines);
            return RenderAfterStateChangeCore();
        }
    }

    public TuiTranscriptOverlayHandle OpenOverlay(
        ITuiComponent component,
        TuiTranscriptOverlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(component);

        lock (_sync)
        {
            var handle = TranscriptHost.OpenOverlay(component, options);
            if (component is ITuiInputComponent inputComponent)
            {
                _inputOverlays.Add(new OverlayInputEntry(handle, inputComponent));
            }

            RenderAfterStateChangeCore();
            return handle;
        }
    }

    public TuiTranscriptRenderResult? CloseOverlay(TuiTranscriptOverlayHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        lock (_sync)
        {
            handle.Close();
            _inputOverlays.RemoveAll(entry => ReferenceEquals(entry.Handle, handle));
            return RenderAfterStateChangeCore();
        }
    }

    public TuiCompositionInputResult HandleInput(ConsoleKeyInfo key)
    {
        lock (_sync)
        {
            if (FocusedInputOverlayCore() is { } overlay)
            {
                var overlayResult = overlay.Component.HandleInput(key);
                if (overlayResult.Consumed)
                {
                    return TuiCompositionInputResult.FromOverlay(overlayResult, RenderAfterStateChangeCore());
                }
            }

            var action = TuiTranscriptInput.ResolveAction(key);
            if (action == TuiTranscriptInputAction.None)
            {
                return TuiCompositionInputResult.Ignored;
            }

            TuiTranscriptInput.ApplyAction(TranscriptHost, action);
            return TuiCompositionInputResult.FromTranscript(
                TuiTranscriptInputResult.From(action, RenderAfterStateChangeCore()));
        }
    }

    public async ValueTask<TuiCompositionInputResult> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        if (_keyReader is null)
        {
            throw new InvalidOperationException("TuiCompositionHost requires an IConsoleKeyReader to read input.");
        }

        var key = await _keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        return HandleInput(key);
    }

    private TuiTranscriptRenderResult RenderCore(bool force = false)
    {
        var result = TranscriptHost.Render(force);
        LastRenderResult = result;
        return result;
    }

    private TuiTranscriptRenderResult? RenderAfterStateChangeCore() =>
        IsStarted && AutoRender ? RenderCore() : null;

    private OverlayInputEntry? FocusedInputOverlayCore()
    {
        TranscriptHost.RefreshOverlayFocus();
        _inputOverlays.RemoveAll(static entry => entry.Handle.IsClosed);
        return _inputOverlays.LastOrDefault(static entry => entry.Handle.IsFocused && entry.Handle.IsVisible);
    }

    private sealed record OverlayInputEntry(TuiTranscriptOverlayHandle Handle, ITuiInputComponent Component);
}
