using System.Globalization;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public sealed class TuiCompositionInteractiveRenderer : IInteractiveRenderer, IInteractiveAutocompleteRenderer
{
    private readonly TuiCompositionSession _session;
    private readonly InputOverlayComponent _component = new();
    private readonly object _stateSync = new();
    private TuiTranscriptOverlayHandle? _handle;

    public TuiCompositionInteractiveRenderer(TuiCompositionSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public int WindowWidth => Math.Max(1, _session.Viewport.Width);

    public void WritePrompt(string prompt, ConsoleColor? color = null)
    {
        lock (_stateSync)
        {
            EnsureStarted();
            _component.SetPrompt(prompt);
            _component.SetLine(string.Empty, 0);
            _component.SetSearch(null, null, 0);
            EnsureOverlay();
            _session.Render();
        }
    }

    public void Render(string buffer, int cursorIndex)
    {
        lock (_stateSync)
        {
            EnsureStarted();
            _component.SetLine(buffer, cursorIndex);
            _component.SetSearch(null, null, 0);
            EnsureOverlay();
            _session.Render();
        }
    }

    public void RenderSearch(string pattern, string? match, int cursorInMatch)
    {
        lock (_stateSync)
        {
            EnsureStarted();
            _component.SetSearch(pattern, match, cursorInMatch);
            EnsureOverlay();
            _session.Render();
        }
    }

    /// <summary>
    /// 更新输入区下方的补全候选项，并触发组合式终端重绘。
    /// </summary>
    /// <param name="items">需要显示的补全候选项集合。</param>
    /// <param name="selectedIndex">当前选中项索引；没有选中项时为负数。</param>
    public void RenderAutocomplete(IReadOnlyList<TuiAutocompleteItem> items, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(items);

        lock (_stateSync)
        {
            EnsureStarted();
            _component.SetAutocomplete(items, selectedIndex);
            EnsureOverlay();
            _session.Render();
        }
    }

    public void Commit()
    {
        lock (_stateSync)
        {
            CloseOverlay();
            _session.SetReservedBottomLines(0);
            _session.Render();
        }
    }

    public void Cancel()
    {
        lock (_stateSync)
        {
            CloseOverlay();
            _session.SetReservedBottomLines(0);
            _session.Render();
        }
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
        // 1. 先同步 surface 尺寸，确保 resize 后使用最新的视口边界
        _session.Render();
        var width = Math.Max(1, _session.Viewport.Width);
        // 输入框先按完整消息区测量，再把实际高度登记为底部占位，避免覆盖 transcript
        _component.SetAvailableHeight(Math.Max(1, _session.Viewport.MessageHeight));
        var overlayHeight = _component.GetRenderedLineCount(width);
        var previousReservedLines = _session.Viewport.ReservedBottomLines;
        _session.SetReservedBottomLines(overlayHeight);
        var row = _session.Viewport.TranscriptHeight;
        if (_handle is not null &&
            !_handle.IsClosed &&
            _component.Row == row &&
            _component.Width == width &&
            _component.Height == overlayHeight &&
            previousReservedLines == overlayHeight)
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
        private const int MaxInputLines = 5;
        private const int MaxCompletionLines = 5;
        private const int MaxSearchLines = 2;
        private readonly object _stateSync = new();
        private string _prompt = string.Empty;
        private string _buffer = string.Empty;
        private int _cursorIndex;
        private int _availableHeight = int.MaxValue;
        private string? _searchPattern;
        private string? _searchMatch;
        private int _searchCursor;
        private IReadOnlyList<TuiAutocompleteItem> _autocompleteItems = [];
        private int _selectedAutocompleteIndex = -1;

        public int Row { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public void SetPrompt(string prompt)
        {
            lock (_stateSync)
            {
                _prompt = prompt ?? string.Empty;
            }
        }

        /// <summary>
        /// 设置输入覆盖层可以占用的消息区行数，避免覆盖底部状态栏。
        /// </summary>
        /// <param name="height">消息区可用行数。</param>
        public void SetAvailableHeight(int height)
        {
            lock (_stateSync)
            {
                _availableHeight = Math.Max(1, height);
            }
        }

        public void SetLine(string buffer, int cursorIndex)
        {
            lock (_stateSync)
            {
                _buffer = buffer ?? string.Empty;
                _cursorIndex = Math.Clamp(cursorIndex, 0, _buffer.Length);
            }
        }

        public void SetSearch(string? pattern, string? match, int cursorInMatch)
        {
            lock (_stateSync)
            {
                _searchPattern = pattern;
                _searchMatch = match;
                _searchCursor = Math.Max(0, cursorInMatch);
            }
        }

        /// <summary>
        /// 设置输入区下方显示的补全候选项和选中位置。
        /// </summary>
        /// <param name="items">候选项集合。</param>
        /// <param name="selectedIndex">当前选中项索引；没有选中项时为负数。</param>
        public void SetAutocomplete(IReadOnlyList<TuiAutocompleteItem> items, int selectedIndex)
        {
            ArgumentNullException.ThrowIfNull(items);

            lock (_stateSync)
            {
                _autocompleteItems = items.ToArray();
                _selectedAutocompleteIndex = _autocompleteItems.Count == 0
                    ? -1
                    : Math.Clamp(selectedIndex, 0, _autocompleteItems.Count - 1);
            }
        }

        public IReadOnlyList<string> Render(int width)
        {
            lock (_stateSync)
            {
                width = Math.Max(1, width);
                return RenderLines(width)
                    .Select(line => TuiText.PadRightToWidth(TuiText.TruncateToWidth(line, width, string.Empty), width))
                    .ToArray();
            }
        }

        public void Invalidate()
        {
        }

        public int GetRenderedLineCount(int width)
        {
            lock (_stateSync)
            {
                return RenderLines(Math.Max(1, width)).Count;
            }
        }

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
            // 末尾光标使用一个反色空格绘制，因此布局时预留一列，避免刚好填满时被截掉
            var layoutWidth = Math.Max(1, width - 1);
            var inputLines = RenderCursorWindow(
                _prompt + _buffer,
                _prompt.Length + cursorTextIndex,
                layoutWidth,
                MaxInputLines);
            var availableHeight = Math.Max(1, _availableHeight);
            if (availableHeight == 1)
            {
                return [NormalizeInputLine(inputLines[0], width)];
            }

            var lines = new List<string>(Math.Min(availableHeight, inputLines.Count + 2))
            {
                RenderInputSeparator(width),
            };
            var inputLineCount = Math.Min(inputLines.Count, Math.Max(1, availableHeight - 2));
            lines.AddRange(inputLines.Take(inputLineCount).Select(line => NormalizeInputLine(line, width)));
            if (lines.Count < availableHeight)
            {
                lines.Add(RenderInputSeparator(width));
            }

            if (_autocompleteItems.Count > 0 && lines.Count < availableHeight)
            {
                var remainingHeight = availableHeight - lines.Count;
                lines.AddRange(RenderAutocompleteLines(width, Math.Min(remainingHeight, MaxCompletionLines)));
            }

            return lines;
        }

        /// <summary>
        /// 将补全候选项渲染为 Cade 风格的紧凑列表行。
        /// </summary>
        /// <param name="width">补全区域目标宽度。</param>
        /// <param name="maxLines">本次最多显示的候选项行数。</param>
        /// <returns>已经裁剪到目标宽度的候选项行。</returns>
        private IReadOnlyList<string> RenderAutocompleteLines(int width, int maxLines)
        {
            if (maxLines <= 0)
            {
                return [];
            }

            return _autocompleteItems
                .Take(maxLines)
                .Select((item, index) =>
                {
                    var marker = index == _selectedAutocompleteIndex
                        ? "\u001b[36m>\u001b[39m "
                        : "  ";
                    var label = TuiText.TruncateToWidth(item.Label, Math.Max(1, width - 2), string.Empty);
                    var description = string.IsNullOrWhiteSpace(item.Description)
                        ? string.Empty
                        : $"  \u001b[2m{TuiText.TruncateToWidth(item.Description, Math.Max(1, width - TuiText.VisibleWidth(marker + label) - 2), string.Empty)}\u001b[22m";
                    return TuiText.TruncateToWidth(marker + label + description, width, string.Empty, pad: true);
                })
                .ToArray();
        }

        /// <summary>
        /// 将输入行规范化为目标终端宽度，避免光标或宽字符造成行宽抖动。
        /// </summary>
        /// <param name="line">待显示的输入内容。</param>
        /// <param name="width">输入区域目标宽度。</param>
        /// <returns>可见宽度稳定的输入内容行。</returns>
        private static string NormalizeInputLine(string line, int width)
        {
            return TuiText.TruncateToWidth(line, Math.Max(1, width), string.Empty, pad: true);
        }

        /// <summary>
        /// 渲染输入区域的横向分隔线。
        /// </summary>
        /// <param name="width">输入区域目标宽度。</param>
        /// <returns>使用 dim 前景色且可见宽度稳定的分隔线。</returns>
        private static string RenderInputSeparator(int width)
        {
            width = Math.Max(1, width);
            var line = new string('─', width);
            return $"\u001b[90m{line}\u001b[39m";
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

            return _availableHeight == 1 ? [detail] : [header, detail];
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
