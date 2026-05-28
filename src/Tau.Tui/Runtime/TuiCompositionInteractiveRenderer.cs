using System.Globalization;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public sealed class TuiCompositionInteractiveRenderer : IInteractiveRenderer
{
    private readonly TuiCompositionSession _session;
    private readonly InputOverlayComponent _component = new();
    private TuiTranscriptOverlayHandle? _handle;

    public TuiCompositionInteractiveRenderer(TuiCompositionSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public int WindowWidth => Math.Max(1, _session.Viewport.Width);

    public void WritePrompt(string prompt, ConsoleColor? color = null)
    {
        EnsureStarted();
        _component.SetPrompt(prompt);
        _component.SetLine(string.Empty, 0);
        _component.SetSearch(null, null, 0);
        EnsureOverlay();
        _session.Render();
    }

    public void Render(string buffer, int cursorIndex)
    {
        EnsureStarted();
        _component.SetLine(buffer, cursorIndex);
        _component.SetSearch(null, null, 0);
        EnsureOverlay();
        _session.Render();
    }

    public void RenderSearch(string pattern, string? match, int cursorInMatch)
    {
        EnsureStarted();
        _component.SetSearch(pattern, match, cursorInMatch);
        EnsureOverlay();
        _session.Render();
    }

    public void Commit()
    {
        CloseOverlay();
        _session.Render();
    }

    public void Cancel()
    {
        CloseOverlay();
        _session.Render();
    }

    private void EnsureStarted()
    {
        if (!_session.IsStarted)
        {
            _session.Start();
        }
    }

    private void EnsureOverlay()
    {
        var width = Math.Max(1, _session.Viewport.Width);
        var overlayHeight = _component.GetRenderedLineCount(width);
        var row = Math.Max(0, _session.Viewport.MessageHeight - overlayHeight);
        if (_handle is not null &&
            !_handle.IsClosed &&
            _component.Row == row &&
            _component.Width == width &&
            _component.Height == overlayHeight)
        {
            return;
        }

        CloseOverlay();
        _component.Row = row;
        _component.Width = width;
        _component.Height = overlayHeight;
        _handle = _session.OpenOverlay(
            _component,
            new TuiTranscriptOverlayOptions(Width: width, Row: row, Column: 0));
    }

    private void CloseOverlay()
    {
        if (_handle is null || _handle.IsClosed)
        {
            _handle = null;
            return;
        }

        _session.CloseOverlay(_handle);
        _handle = null;
    }

    private sealed class InputOverlayComponent : ITuiComponent
    {
        private const string CursorPlaceholder = "\uE000";
        private const string CursorStyleStart = "\u001b[7m";
        private const string CursorStyleEnd = "\u001b[27m";
        private const string HighlightStartPlaceholder = "\u0001";
        private const string HighlightEndPlaceholder = "\u0002";
        private const string HighlightStyleStart = "\u001b[4m";
        private const string HighlightStyleEnd = "\u001b[24m";
        private const string ReverseSearchNoMatchText = "[no match]";
        private const string InputHintText = "Enter send | Alt+Enter follow-up | Ctrl+L model | Ctrl+P next | Ctrl+R search";
        private const int MaxInputLines = 3;
        private const int MaxSearchLines = 2;
        private string _prompt = string.Empty;
        private string _buffer = string.Empty;
        private int _cursorIndex;
        private string? _searchPattern;
        private string? _searchMatch;
        private int _searchCursor;

        public int Row { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public void SetPrompt(string prompt) => _prompt = prompt ?? string.Empty;

        public void SetLine(string buffer, int cursorIndex)
        {
            _buffer = buffer ?? string.Empty;
            _cursorIndex = Math.Clamp(cursorIndex, 0, _buffer.Length);
        }

        public void SetSearch(string? pattern, string? match, int cursorInMatch)
        {
            _searchPattern = pattern;
            _searchMatch = match;
            _searchCursor = Math.Max(0, cursorInMatch);
        }

        public IReadOnlyList<string> Render(int width)
        {
            width = Math.Max(1, width);
            return RenderLines(width)
                .Select(line => TuiText.PadRightToWidth(TuiText.TruncateToWidth(line, width, string.Empty), width))
                .ToArray();
        }

        public void Invalidate()
        {
        }

        public int GetRenderedLineCount(int width) => RenderLines(width).Count;

        private IReadOnlyList<string> RenderLines(int width)
        {
            width = Math.Max(1, width);
            return _searchPattern is null
                ? RenderInputLines(width)
                : RenderSearchLines(width);
        }

        private IReadOnlyList<string> RenderInputLines(int width)
        {
            var cursorTextIndex = Math.Clamp(_cursorIndex, 0, _buffer.Length);
            var inputLines = RenderCursorWindow(
                _prompt + _buffer,
                _prompt.Length + cursorTextIndex,
                width,
                MaxInputLines);
            return [.. inputLines, InputHintText];
        }

        private IReadOnlyList<string> RenderSearchLines(int width)
        {
            var query = _searchPattern ?? string.Empty;
            var searchMatch = _searchMatch ?? string.Empty;
            var header = $"(reverse-i-search) `{query}`";
            var detail = searchMatch.Length == 0
                ? RenderCursorWindow(
                    query.Length == 0 ? string.Empty : $" {ReverseSearchNoMatchText}",
                    0,
                    width,
                    maxLines: 1)[0]
                : RenderCursorWindow(
                    searchMatch,
                    Math.Clamp(_searchCursor, 0, searchMatch.Length),
                    width,
                    maxLines: 1,
                    highlightStart: query.Length == 0 ? null : Math.Clamp(_searchCursor, 0, searchMatch.Length),
                    highlightLength: query.Length)[0];

            return [header, detail];
        }

        private static IReadOnlyList<string> RenderCursorWindow(
            string text,
            int cursorIndex,
            int width,
            int maxLines,
            int? highlightStart = null,
            int highlightLength = 0)
        {
            var logical = InsertDecoratedPlaceholders(text, cursorIndex, highlightStart, highlightLength);
            return SelectVisibleLines(TuiText.Wrap(logical, width), maxLines)
                .Select(ApplyDecorations)
                .ToArray();
        }

        private static string InsertDecoratedPlaceholders(
            string text,
            int cursorIndex,
            int? highlightStart,
            int highlightLength)
        {
            text ??= string.Empty;
            cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);

            if (highlightStart is { } startIndex && highlightLength > 0)
            {
                var clampedStart = Math.Clamp(startIndex, 0, text.Length);
                var clampedLength = Math.Clamp(highlightLength, 0, text.Length - clampedStart);
                if (clampedLength > 0)
                {
                    text = text.Insert(clampedStart + clampedLength, HighlightEndPlaceholder);
                    text = text.Insert(clampedStart, HighlightStartPlaceholder);

                    if (cursorIndex >= clampedStart + clampedLength)
                    {
                        cursorIndex += HighlightEndPlaceholder.Length;
                    }

                    if (cursorIndex >= clampedStart)
                    {
                        cursorIndex += HighlightStartPlaceholder.Length;
                    }
                }
            }

            return InsertCursorPlaceholder(text, cursorIndex);
        }

        private static string InsertCursorPlaceholder(string text, int index)
        {
            text ??= string.Empty;
            index = Math.Clamp(index, 0, text.Length);
            return text.Insert(index, CursorPlaceholder);
        }

        private static string ApplyDecorations(string line) =>
            ApplyHighlightStyle(ApplyCursorStyle(line));

        private static string ApplyCursorStyle(string line)
        {
            var markerIndex = line.IndexOf(CursorPlaceholder, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return line;
            }

            var withoutMarker = line.Remove(markerIndex, CursorPlaceholder.Length);
            if (markerIndex >= withoutMarker.Length)
            {
                return withoutMarker.Insert(markerIndex, $"{CursorStyleStart} {CursorStyleEnd}");
            }

            var textElement = StringInfo.GetNextTextElement(withoutMarker, markerIndex);
            return withoutMarker.Remove(markerIndex, textElement.Length)
                .Insert(markerIndex, $"{CursorStyleStart}{textElement}{CursorStyleEnd}");
        }

        private static string ApplyHighlightStyle(string line) =>
            line.Replace(HighlightStartPlaceholder, HighlightStyleStart, StringComparison.Ordinal)
                .Replace(HighlightEndPlaceholder, HighlightStyleEnd, StringComparison.Ordinal);

        private static IReadOnlyList<string> SelectVisibleLines(IReadOnlyList<string> lines, int maxLines)
        {
            if (lines.Count <= maxLines)
            {
                return lines;
            }

            var cursorLine = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(CursorPlaceholder, StringComparison.Ordinal))
                {
                    cursorLine = i;
                    break;
                }
            }

            if (cursorLine < 0)
            {
                return lines.Skip(Math.Max(0, lines.Count - maxLines)).ToArray();
            }

            var start = Math.Max(0, cursorLine - maxLines + 1);
            if (start + maxLines > lines.Count)
            {
                start = Math.Max(0, lines.Count - maxLines);
            }

            return lines.Skip(start).Take(maxLines).ToArray();
        }
    }
}
