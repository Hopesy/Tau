using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public enum TuiTranscriptInputAction
{
    None,
    ScrollLineUp,
    ScrollLineDown,
    ScrollPageUp,
    ScrollPageDown,
    ScrollTop,
    ScrollBottom,
}

public readonly record struct TuiTranscriptInputResult(
    bool Consumed,
    TuiTranscriptInputAction Action,
    TuiTranscriptRenderResult? RenderResult)
{
    public static TuiTranscriptInputResult Ignored { get; } =
        new(false, TuiTranscriptInputAction.None, null);

    public static TuiTranscriptInputResult From(
        TuiTranscriptInputAction action,
        TuiTranscriptRenderResult? renderResult) =>
        new(true, action, renderResult);
}

public sealed class TuiTranscriptSession
{
    private readonly IConsoleKeyReader? _keyReader;

    public TuiTranscriptSession(
        ITuiRenderSurface surface,
        IConsoleKeyReader? keyReader = null,
        IEnumerable<TuiMessage>? messages = null,
        string statusLeft = "",
        string statusRight = "",
        bool autoRender = true,
        int maxScrollbackLines = 10_000)
    {
        Host = new TuiTranscriptViewportHost(
            surface,
            messages,
            statusLeft,
            statusRight,
            maxScrollbackLines);
        _keyReader = keyReader;
        AutoRender = autoRender;
    }

    public TuiTranscriptViewportHost Host { get; }
    public TuiTranscriptViewport Viewport => Host.Viewport;
    public bool AutoRender { get; set; }
    public bool IsStarted { get; private set; }
    public TuiTranscriptRenderResult? LastRenderResult { get; private set; }

    public TuiTranscriptRenderResult Start()
    {
        if (!IsStarted)
        {
            IsStarted = true;
            Host.ResetFrame();
            Host.HideCursor();
        }

        return Render(force: true);
    }

    public void Stop()
    {
        IsStarted = false;
        LastRenderResult = null;
        Host.ShowCursor();
        Host.ResetFrame();
    }

    public TuiTranscriptRenderResult Render(bool force = false)
    {
        var result = Host.Render(force);
        LastRenderResult = result;
        return result;
    }

    public TuiTranscriptRenderResult? SetMessages(IEnumerable<TuiMessage> messages)
    {
        Host.SetMessages(messages);
        return RenderAfterStateChange();
    }

    public TuiTranscriptRenderResult? AppendMessage(TuiMessage message)
    {
        Host.AppendMessage(message);
        return RenderAfterStateChange();
    }

    public TuiTranscriptRenderResult? AppendMessages(IEnumerable<TuiMessage> messages)
    {
        Host.AppendMessages(messages);
        return RenderAfterStateChange();
    }

    public TuiTranscriptRenderResult? ClearMessages()
    {
        Host.ClearMessages();
        return RenderAfterStateChange();
    }

    public TuiTranscriptRenderResult? SetStatus(string left, string right)
    {
        Host.SetStatus(left, right);
        return RenderAfterStateChange();
    }

    public TuiTranscriptOverlayHandle OpenOverlay(
        ITuiComponent component,
        TuiTranscriptOverlayOptions? options = null)
    {
        var handle = Host.OpenOverlay(component, options);
        RenderAfterStateChange();
        return handle;
    }

    public TuiTranscriptRenderResult? CloseOverlay(TuiTranscriptOverlayHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Close();
        return RenderAfterStateChange();
    }

    public TuiTranscriptInputResult HandleInput(ConsoleKeyInfo key)
    {
        var action = ResolveAction(key);
        if (action == TuiTranscriptInputAction.None)
        {
            return TuiTranscriptInputResult.Ignored;
        }

        ApplyAction(action);
        return TuiTranscriptInputResult.From(action, RenderAfterStateChange());
    }

    public async ValueTask<TuiTranscriptInputResult> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        if (_keyReader is null)
        {
            throw new InvalidOperationException("TuiTranscriptSession requires an IConsoleKeyReader to read input.");
        }

        var key = await _keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        return HandleInput(key);
    }

    private TuiTranscriptRenderResult? RenderAfterStateChange() =>
        IsStarted && AutoRender ? Render() : null;

    private void ApplyAction(TuiTranscriptInputAction action)
    {
        switch (action)
        {
            case TuiTranscriptInputAction.ScrollLineUp:
                Host.ScrollLine(delta: 1);
                break;
            case TuiTranscriptInputAction.ScrollLineDown:
                Host.ScrollLine(delta: -1);
                break;
            case TuiTranscriptInputAction.ScrollPageUp:
                Host.ScrollPage(delta: 1);
                break;
            case TuiTranscriptInputAction.ScrollPageDown:
                Host.ScrollPage(delta: -1);
                break;
            case TuiTranscriptInputAction.ScrollTop:
                Host.ScrollTop();
                break;
            case TuiTranscriptInputAction.ScrollBottom:
                Host.ScrollBottom();
                break;
            case TuiTranscriptInputAction.None:
            default:
                break;
        }
    }

    private static TuiTranscriptInputAction ResolveAction(ConsoleKeyInfo key) =>
        key.Key switch
        {
            ConsoleKey.UpArrow => TuiTranscriptInputAction.ScrollLineUp,
            ConsoleKey.DownArrow => TuiTranscriptInputAction.ScrollLineDown,
            ConsoleKey.PageUp => TuiTranscriptInputAction.ScrollPageUp,
            ConsoleKey.PageDown => TuiTranscriptInputAction.ScrollPageDown,
            ConsoleKey.Home => TuiTranscriptInputAction.ScrollTop,
            ConsoleKey.End => TuiTranscriptInputAction.ScrollBottom,
            _ => TuiTranscriptInputAction.None,
        };
}
