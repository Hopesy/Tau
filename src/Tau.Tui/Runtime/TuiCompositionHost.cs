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

    public TuiCompositionHost(
        ITuiRenderSurface surface,
        IConsoleKeyReader? keyReader = null,
        IEnumerable<TuiMessage>? messages = null,
        string statusLeft = "",
        string statusRight = "",
        bool autoRender = true,
        int maxScrollbackLines = 10_000)
    {
        TranscriptHost = new TuiTranscriptViewportHost(
            surface,
            messages,
            statusLeft,
            statusRight,
            maxScrollbackLines);
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
