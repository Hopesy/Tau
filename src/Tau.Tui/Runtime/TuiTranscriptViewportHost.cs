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
    Func<int, int, bool>? IsVisible = null,
    int? MinWidth = null,
    int? MaxHeight = null,
    TuiTranscriptOverlayAnchor? Anchor = null,
    int OffsetRow = 0,
    int OffsetColumn = 0,
    TuiTranscriptOverlayMargin? Margin = null);

public enum TuiTranscriptOverlayAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    LeftCenter,
    Center,
    RightCenter,
    BottomLeft,
    BottomCenter,
    BottomRight
}

public sealed record TuiTranscriptOverlayMargin(
    int Top = 0,
    int Right = 0,
    int Bottom = 0,
    int Left = 0)
{
    public static TuiTranscriptOverlayMargin All(int value) => new(value, value, value, value);
}

public sealed class TuiTranscriptOverlayHandle
{
    private readonly Action _close;
    private readonly Action<bool> _setHidden;
    private readonly Func<bool> _isHidden;
    private readonly Func<bool> _isVisible;
    private readonly Action _focus;
    private readonly Func<bool> _isFocused;
    private readonly Func<bool> _isClosed;

    internal TuiTranscriptOverlayHandle(
        Action close,
        Action<bool> setHidden,
        Func<bool> isHidden,
        Func<bool> isVisible,
        Action focus,
        Func<bool> isFocused,
        Func<bool> isClosed)
    {
        _close = close;
        _setHidden = setHidden;
        _isHidden = isHidden;
        _isVisible = isVisible;
        _focus = focus;
        _isFocused = isFocused;
        _isClosed = isClosed;
    }

    public bool IsHidden => _isHidden();
    public bool IsVisible => _isVisible();
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

    public void SetStatusLines(IEnumerable<TuiStatusBarLine> lines) => Viewport.SetStatusLines(lines);

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
        else
        {
            RefreshOverlayFocus();
        }

        return new TuiTranscriptOverlayHandle(
            close: () => CloseOverlay(entry),
            setHidden: hidden => SetOverlayHidden(entry, hidden),
            isHidden: () => entry.Hidden,
            isVisible: () => _overlays.Contains(entry) && IsOverlayVisible(entry),
            focus: () => FocusOverlay(entry),
            isFocused: () => ReferenceEquals(_focusedOverlay, entry),
            isClosed: () => !_overlays.Contains(entry));
    }

    public TuiTranscriptRenderResult Render(bool force = false)
    {
        Viewport.Resize(_surface.Width, _surface.Height);
        RefreshOverlayFocus();
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

    public void RefreshOverlayFocus()
    {
        var hadFocusedOverlay = _focusedOverlay is not null;
        var topVisible = TopVisibleOverlay();
        if (!ReferenceEquals(_focusedOverlay, topVisible))
        {
            _focusedOverlay = topVisible;
        }

        SyncCursorWithOverlayVisibility(hadFocusedOverlay);
    }

    private void CloseOverlay(OverlayEntry entry)
    {
        var wasVisible = ReferenceEquals(_focusedOverlay, entry) ||
            (_overlays.Contains(entry) && IsOverlayVisible(entry));
        if (!_overlays.Remove(entry))
        {
            return;
        }

        if (ReferenceEquals(_focusedOverlay, entry))
        {
            _focusedOverlay = TopVisibleOverlay();
        }

        SyncCursorWithOverlayVisibility(wasVisible);
    }

    private void SetOverlayHidden(OverlayEntry entry, bool hidden)
    {
        if (!_overlays.Contains(entry) || entry.Hidden == hidden)
        {
            return;
        }

        var wasVisible = ReferenceEquals(_focusedOverlay, entry) || IsOverlayVisible(entry);
        entry.Hidden = hidden;
        if (hidden && ReferenceEquals(_focusedOverlay, entry))
        {
            _focusedOverlay = TopVisibleOverlay();
        }
        else if (!hidden && IsOverlayVisible(entry))
        {
            FocusOverlay(entry);
        }

        SyncCursorWithOverlayVisibility(wasVisible);
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

    private void SyncCursorWithOverlayVisibility(bool hadVisibleOverlay)
    {
        if (_focusedOverlay is not null)
        {
            HideCursor();
        }
        else if (hadVisibleOverlay)
        {
            ShowCursor();
        }
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
        var sizing = ResolveOverlaySizing(overlay.Options, width, height);
        var rendered = overlay.Component.Render(sizing.Width);
        if (sizing.MaxHeight is int maxHeight && rendered.Count > maxHeight)
        {
            rendered = rendered.Take(maxHeight).ToArray();
        }

        var position = ResolveOverlayPosition(
            overlay.Options,
            sizing,
            rendered.Count,
            width,
            height);

        for (var index = 0; index < rendered.Count && position.Row + index < height; index++)
        {
            target[position.Row + index] = CompositeLine(
                target[position.Row + index],
                rendered[index],
                position.Column,
                sizing.Width,
                width);
        }
    }

    private static OverlaySizing ResolveOverlaySizing(
        TuiTranscriptOverlayOptions options,
        int width,
        int height)
    {
        var margin = NormalizeMargin(options.Margin);
        var availableWidth = Math.Max(1, width - margin.Left - margin.Right);
        var availableHeight = Math.Max(1, height - margin.Top - margin.Bottom);
        var overlayWidth = options.Width ?? Math.Min(80, availableWidth);
        if (options.MinWidth is int minWidth)
        {
            overlayWidth = Math.Max(overlayWidth, Math.Max(1, minWidth));
        }

        overlayWidth = Math.Clamp(overlayWidth, 1, availableWidth);
        int? maxHeight = null;
        if (options.MaxHeight is int requestedMaxHeight)
        {
            maxHeight = Math.Clamp(requestedMaxHeight, 1, availableHeight);
        }

        return new OverlaySizing(overlayWidth, maxHeight, margin);
    }

    private static OverlayPosition ResolveOverlayPosition(
        TuiTranscriptOverlayOptions options,
        OverlaySizing sizing,
        int renderedHeight,
        int width,
        int height)
    {
        var margin = sizing.Margin;
        var effectiveHeight = sizing.MaxHeight is int maxHeight
            ? Math.Min(renderedHeight, maxHeight)
            : renderedHeight;
        var availableWidth = Math.Max(1, width - margin.Left - margin.Right);
        var availableHeight = Math.Max(1, height - margin.Top - margin.Bottom);
        var hasLayoutBounds = options.Anchor is not null || options.Margin is not null || options.MaxHeight is not null;

        var row = options.Anchor is null
            ? options.Row
            : ResolveAnchorRow(options.Anchor.Value, effectiveHeight, availableHeight, margin.Top);
        var column = options.Anchor is null
            ? options.Column
            : ResolveAnchorColumn(options.Anchor.Value, sizing.Width, availableWidth, margin.Left);

        row += options.OffsetRow;
        column += options.OffsetColumn;

        var minRow = hasLayoutBounds ? margin.Top : 0;
        var maxRow = hasLayoutBounds
            ? Math.Max(minRow, height - margin.Bottom - effectiveHeight)
            : Math.Max(0, height - 1);
        var minColumn = options.Margin is null ? 0 : margin.Left;
        var maxColumn = Math.Max(minColumn, width - margin.Right - sizing.Width);

        return new OverlayPosition(
            Math.Clamp(row, minRow, maxRow),
            Math.Clamp(column, minColumn, maxColumn));
    }

    private static int ResolveAnchorRow(
        TuiTranscriptOverlayAnchor anchor,
        int overlayHeight,
        int availableHeight,
        int marginTop) =>
        anchor switch
        {
            TuiTranscriptOverlayAnchor.TopLeft or
            TuiTranscriptOverlayAnchor.TopCenter or
            TuiTranscriptOverlayAnchor.TopRight => marginTop,
            TuiTranscriptOverlayAnchor.BottomLeft or
            TuiTranscriptOverlayAnchor.BottomCenter or
            TuiTranscriptOverlayAnchor.BottomRight => marginTop + Math.Max(0, availableHeight - overlayHeight),
            _ => marginTop + Math.Max(0, availableHeight - overlayHeight) / 2
        };

    private static int ResolveAnchorColumn(
        TuiTranscriptOverlayAnchor anchor,
        int overlayWidth,
        int availableWidth,
        int marginLeft) =>
        anchor switch
        {
            TuiTranscriptOverlayAnchor.TopLeft or
            TuiTranscriptOverlayAnchor.LeftCenter or
            TuiTranscriptOverlayAnchor.BottomLeft => marginLeft,
            TuiTranscriptOverlayAnchor.TopRight or
            TuiTranscriptOverlayAnchor.RightCenter or
            TuiTranscriptOverlayAnchor.BottomRight => marginLeft + Math.Max(0, availableWidth - overlayWidth),
            _ => marginLeft + Math.Max(0, availableWidth - overlayWidth) / 2
        };

    private static TuiTranscriptOverlayMargin NormalizeMargin(TuiTranscriptOverlayMargin? margin) =>
        margin is null
            ? new TuiTranscriptOverlayMargin()
            : new TuiTranscriptOverlayMargin(
                Math.Max(0, margin.Top),
                Math.Max(0, margin.Right),
                Math.Max(0, margin.Bottom),
                Math.Max(0, margin.Left));

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

    private readonly record struct OverlaySizing(
        int Width,
        int? MaxHeight,
        TuiTranscriptOverlayMargin Margin);

    private readonly record struct OverlayPosition(int Row, int Column);

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
