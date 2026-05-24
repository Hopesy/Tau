using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public sealed record TuiTranscriptRenderResult(
    TuiRenderFrame Frame,
    TuiRenderDiff Diff,
    TuiRenderFrame? BaseFrame = null,
    bool CursorVisible = true,
    bool HasVisibleOverlay = false);

public sealed record TuiTranscriptOverlayOptions(
    int? Width = null,
    int Row = 0,
    int Column = 0,
    bool InitiallyHidden = false,
    Func<int, int, bool>? IsVisible = null);

public sealed class TuiTranscriptOverlayHandle
{
    private readonly Action _close;
    private readonly Action<bool> _setHidden;
    private readonly Func<bool> _isHidden;
    private readonly Action _focus;
    private readonly Func<bool> _isFocused;
    private readonly Func<bool> _isClosed;

    internal TuiTranscriptOverlayHandle(
        Action close,
        Action<bool> setHidden,
        Func<bool> isHidden,
        Action focus,
        Func<bool> isFocused,
        Func<bool> isClosed)
    {
        _close = close;
        _setHidden = setHidden;
        _isHidden = isHidden;
        _focus = focus;
        _isFocused = isFocused;
        _isClosed = isClosed;
    }

    public bool IsHidden => _isHidden();
    public bool IsFocused => _isFocused();
    public bool IsClosed => _isClosed();

    public void Close() => _close();

    public void SetHidden(bool hidden) => _setHidden(hidden);

    public void Focus() => _focus();
}

public sealed class TuiTranscriptViewportHost
{
    private readonly ITuiRenderSurface _surface;
    private readonly List<OverlayEntry> _overlays = [];
    private TuiRenderFrame? _previousFrame;
    private OverlayEntry? _focusedOverlay;
    private long _focusOrder;

    public TuiTranscriptViewportHost(
        ITuiRenderSurface surface,
        IEnumerable<TuiMessage>? messages = null,
        string statusLeft = "",
        string statusRight = "",
        int maxScrollbackLines = 10_000)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Viewport = new TuiTranscriptViewport(
            surface.Width,
            surface.Height,
            messages,
            statusLeft,
            statusRight,
            maxScrollbackLines);
    }

    public TuiTranscriptViewport Viewport { get; }
    public TuiRenderFrame? PreviousFrame => _previousFrame;
    public bool CursorVisible { get; private set; } = true;
    public bool HasVisibleOverlay => VisibleOverlays().Any();
    public int OverlayCount => _overlays.Count;

    public void SetMessages(IEnumerable<TuiMessage> messages) => Viewport.SetMessages(messages);

    public void AppendMessage(TuiMessage message) => Viewport.AppendMessage(message);

    public void AppendMessages(IEnumerable<TuiMessage> messages) => Viewport.AppendMessages(messages);

    public void ClearMessages() => Viewport.ClearMessages();

    public void SetStatus(string left, string right) => Viewport.SetStatus(left, right);

    public void ScrollLine(int delta) => Viewport.ScrollLine(delta);

    public void ScrollPage(int delta) => Viewport.ScrollPage(delta);

    public void ScrollTop() => Viewport.ScrollTop();

    public void ScrollBottom() => Viewport.ScrollBottom();

    public void ResetFrame() => _previousFrame = null;

    public void ShowCursor() => CursorVisible = true;

    public void HideCursor() => CursorVisible = false;

    public TuiTranscriptOverlayHandle OpenOverlay(
        ITuiComponent component,
        TuiTranscriptOverlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(component);

        var entry = new OverlayEntry(
            component,
            options ?? new TuiTranscriptOverlayOptions(),
            ++_focusOrder);
        entry.Hidden = entry.Options.InitiallyHidden;
        _overlays.Add(entry);

        if (IsOverlayVisible(entry))
        {
            FocusOverlay(entry);
        }

        HideCursor();

        return new TuiTranscriptOverlayHandle(
            close: () => CloseOverlay(entry),
            setHidden: hidden => SetOverlayHidden(entry, hidden),
            isHidden: () => entry.Hidden,
            focus: () => FocusOverlay(entry),
            isFocused: () => ReferenceEquals(_focusedOverlay, entry),
            isClosed: () => !_overlays.Contains(entry));
    }

    public TuiTranscriptRenderResult Render(bool force = false)
    {
        Viewport.Resize(_surface.Width, _surface.Height);
        var baseFrame = new TuiRenderFrame(Viewport.Width, Viewport.Height, Viewport.Render());
        var frame = ComposeFrame(baseFrame);
        var diff = TuiDiffRenderer.Diff(_previousFrame, frame, force);
        _surface.Apply(diff);
        _previousFrame = frame;
        return new TuiTranscriptRenderResult(
            frame,
            diff,
            baseFrame,
            CursorVisible,
            HasVisibleOverlay);
    }

    private void CloseOverlay(OverlayEntry entry)
    {
        if (!_overlays.Remove(entry))
        {
            return;
        }

        if (ReferenceEquals(_focusedOverlay, entry))
        {
            _focusedOverlay = TopVisibleOverlay();
        }
    }

    private void SetOverlayHidden(OverlayEntry entry, bool hidden)
    {
        if (!_overlays.Contains(entry) || entry.Hidden == hidden)
        {
            return;
        }

        entry.Hidden = hidden;
        if (hidden && ReferenceEquals(_focusedOverlay, entry))
        {
            _focusedOverlay = TopVisibleOverlay();
        }
        else if (!hidden && IsOverlayVisible(entry))
        {
            FocusOverlay(entry);
        }
    }

    private void FocusOverlay(OverlayEntry entry)
    {
        if (!_overlays.Contains(entry) || !IsOverlayVisible(entry))
        {
            return;
        }

        entry.FocusOrder = ++_focusOrder;
        _focusedOverlay = entry;
        HideCursor();
    }

    private OverlayEntry? TopVisibleOverlay() =>
        VisibleOverlays().OrderByDescending(static entry => entry.FocusOrder).FirstOrDefault();

    private bool IsOverlayVisible(OverlayEntry entry)
    {
        if (entry.Hidden)
        {
            return false;
        }

        return entry.Options.IsVisible?.Invoke(_surface.Width, _surface.Height) ?? true;
    }

    private IEnumerable<OverlayEntry> VisibleOverlays() =>
        _overlays.Where(IsOverlayVisible);

    private TuiRenderFrame ComposeFrame(TuiRenderFrame baseFrame)
    {
        var visible = VisibleOverlays().OrderBy(static entry => entry.FocusOrder).ToArray();
        if (visible.Length == 0)
        {
            return baseFrame;
        }

        var lines = baseFrame.Lines.Select(line =>
            TuiText.TruncateToWidth(line, baseFrame.Width, string.Empty, pad: true)).ToList();

        while (lines.Count < baseFrame.Height)
        {
            lines.Add(new string(' ', baseFrame.Width));
        }

        foreach (var overlay in visible)
        {
            CompositeOverlay(lines, overlay, baseFrame.Width, baseFrame.Height);
        }

        return new TuiRenderFrame(baseFrame.Width, baseFrame.Height, lines.Take(baseFrame.Height).ToArray());
    }

    private static void CompositeOverlay(List<string> target, OverlayEntry overlay, int width, int height)
    {
        var overlayWidth = Math.Clamp(overlay.Options.Width ?? Math.Min(80, width), 1, width);
        var row = Math.Clamp(overlay.Options.Row, 0, Math.Max(0, height - 1));
        var column = Math.Clamp(overlay.Options.Column, 0, Math.Max(0, width - overlayWidth));
        var rendered = overlay.Component.Render(overlayWidth);

        for (var index = 0; index < rendered.Count && row + index < height; index++)
        {
            target[row + index] = CompositeLine(
                target[row + index],
                rendered[index],
                column,
                overlayWidth,
                width);
        }
    }

    private static string CompositeLine(
        string baseLine,
        string overlayLine,
        int column,
        int overlayWidth,
        int totalWidth)
    {
        var normalizedBase = TuiText.TruncateToWidth(baseLine, totalWidth, string.Empty, pad: true);
        var overlay = TuiText.TruncateToWidth(overlayLine, overlayWidth, string.Empty, pad: true);
        var before = SliceColumns(normalizedBase, 0, column);
        var afterStart = Math.Min(totalWidth, column + overlayWidth);
        var after = SliceColumns(normalizedBase, afterStart, totalWidth - afterStart);
        return TuiText.TruncateToWidth(before + overlay + after, totalWidth, string.Empty, pad: true);
    }

    private static string SliceColumns(string text, int startColumn, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var endColumn = startColumn + width;
        var currentColumn = 0;
        var result = new List<string>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var elementWidth = TuiText.VisibleWidth(element);
            if (elementWidth == 0)
            {
                if (currentColumn >= startColumn && currentColumn < endColumn)
                {
                    result.Add(element);
                }

                continue;
            }

            var nextColumn = currentColumn + elementWidth;
            if (nextColumn <= startColumn)
            {
                currentColumn = nextColumn;
                continue;
            }

            if (currentColumn >= endColumn)
            {
                break;
            }

            if (currentColumn >= startColumn && nextColumn <= endColumn)
            {
                result.Add(element);
            }

            currentColumn = nextColumn;
        }

        return string.Concat(result);
    }

    private sealed class OverlayEntry(
        ITuiComponent component,
        TuiTranscriptOverlayOptions options,
        long focusOrder)
    {
        public ITuiComponent Component { get; } = component;
        public TuiTranscriptOverlayOptions Options { get; } = options;
        public long FocusOrder { get; set; } = focusOrder;
        public bool Hidden { get; set; }
    }
}
