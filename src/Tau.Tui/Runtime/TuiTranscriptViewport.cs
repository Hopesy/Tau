using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public sealed class TuiTranscriptViewport
{
    private readonly TuiMessageArea _messageArea;
    private readonly TuiMessageDisplayOptions _displayOptions;
    private readonly TuiStatusBar _statusBar;
    private readonly TuiScrollbackBuffer _scrollback;
    private int _width;
    private int _height;

    /// <summary>
    /// 创建消息、滚动缓冲区和状态栏组成的终端视口。
    /// </summary>
    /// <param name="width">视口初始列数。</param>
    /// <param name="height">视口初始行数。</param>
    /// <param name="messages">初始消息集合。</param>
    /// <param name="statusLeft">状态栏左侧初始文本。</param>
    /// <param name="statusRight">状态栏右侧初始文本。</param>
    /// <param name="maxScrollbackLines">滚动缓冲区最大行数。</param>
    /// <param name="displayOptions">消息区域显示主题。</param>
    /// <param name="statusTheme">状态栏显示主题。</param>
    public TuiTranscriptViewport(
        int width,
        int height,
        IEnumerable<TuiMessage>? messages = null,
        string statusLeft = "",
        string statusRight = "",
        int maxScrollbackLines = 10_000,
        TuiMessageDisplayOptions? displayOptions = null,
        TuiStatusBarTheme? statusTheme = null)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _displayOptions = displayOptions ?? TuiMessageDisplayOptions.Plain;
        _messageArea = new TuiMessageArea(displayOptions: _displayOptions);
        _statusBar = new TuiStatusBar(statusLeft, statusRight, statusTheme);
        _scrollback = new TuiScrollbackBuffer(ScrollbackHeight, maxScrollbackLines);

        if (messages is not null)
        {
            SetMessages(messages);
        }
    }

    public int Width => _width;
    public int Height => _height;
    public int StatusHeight => Math.Min(_height, _statusBar.LineCount);
    public int MessageHeight => Math.Max(0, _height - StatusHeight);
    /// <summary>
    /// 获取为底部输入框或其他固定覆盖层预留的行数。
    /// </summary>
    public int ReservedBottomLines { get; private set; }

    /// <summary>
    /// 获取实际用于显示 transcript 的行数，不包含状态栏和底部预留区。
    /// </summary>
    public int TranscriptHeight => Math.Max(0, MessageHeight - ReservedBottomLines);
    public int ScrollOffsetFromBottom => _scrollback.ScrollOffsetFromBottom;
    public bool IsFollowingBottom => _scrollback.IsFollowingBottom;
    public IReadOnlyList<TuiMessage> Messages => _messageArea.Messages;
    public IReadOnlyList<string> ScrollbackLines => _scrollback.Lines;
    public string StatusLeft => _statusBar.Left;
    public string StatusRight => _statusBar.Right;
    public IReadOnlyList<TuiStatusBarLine> StatusLines => _statusBar.Lines;

    private int ScrollbackHeight => Math.Max(1, TranscriptHeight);

    /// <summary>
    /// 设置底部固定区域占用的行数，并保持现有滚动位置有效。
    /// </summary>
    /// <param name="lines">输入框或覆盖层需要预留的行数。</param>
    public void SetReservedBottomLines(int lines)
    {
        ReservedBottomLines = Math.Clamp(lines, 0, MessageHeight);
        _scrollback.SetHeight(ScrollbackHeight);
    }

    public void SetMessages(IEnumerable<TuiMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _messageArea.SetMessages(messages);
        RebuildScrollback(followBottom: true, scrollOffsetFromBottom: 0);
    }

    public void AppendMessage(TuiMessage message) => AppendMessages([message]);

    public void AppendMessages(IEnumerable<TuiMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var appended = messages.ToArray();
        if (appended.Length == 0)
        {
            return;
        }

        foreach (var message in appended)
        {
            _messageArea.Add(message);
        }

        _scrollback.Append(TuiMessageArea.RenderMessages(appended, _width, displayOptions: _displayOptions));
    }

    public void ClearMessages()
    {
        _messageArea.Clear();
        _scrollback.Clear();
    }

    public void SetStatus(string left, string right)
    {
        _statusBar.SetSegments(left, right);
        _scrollback.SetHeight(ScrollbackHeight);
    }

    public void SetStatusLines(IEnumerable<TuiStatusBarLine> lines)
    {
        _statusBar.SetLines(lines);
        _scrollback.SetHeight(ScrollbackHeight);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var widthChanged = width != _width;
        var wasFollowingBottom = _scrollback.IsFollowingBottom;
        var previousOffset = _scrollback.ScrollOffsetFromBottom;

        _width = width;
        _height = height;
        // 终端缩小时收敛旧的底部占位，避免渲染行数超过新的视口高度
        ReservedBottomLines = Math.Min(ReservedBottomLines, MessageHeight);
        _scrollback.SetHeight(ScrollbackHeight);

        if (widthChanged)
        {
            RebuildScrollback(wasFollowingBottom, previousOffset);
        }
    }

    public void ScrollLine(int delta)
    {
        if (delta > 0)
        {
            _scrollback.ScrollUp(delta);
        }
        else if (delta < 0)
        {
            _scrollback.ScrollDown(-delta);
        }
    }

    public void ScrollPage(int delta)
    {
        if (delta > 0)
        {
            _scrollback.ScrollUp(delta * ScrollbackHeight);
        }
        else if (delta < 0)
        {
            _scrollback.ScrollDown(-delta * ScrollbackHeight);
        }
    }

    public void ScrollUp(int lines = 1) => _scrollback.ScrollUp(lines);

    public void ScrollDown(int lines = 1) => _scrollback.ScrollDown(lines);

    public void PageUp() => _scrollback.PageUp();

    public void PageDown() => _scrollback.PageDown();

    public void ScrollTop() => _scrollback.ScrollToTop();

    public void ScrollBottom() => _scrollback.ScrollToBottom();

    public IReadOnlyList<string> Render()
    {
        var rows = new List<string>(_height);
        var visibleMessages = TranscriptHeight == 0 ? [] : _scrollback.VisibleLines();

        var alignTop = _displayOptions.AlignContentTopWhenFits &&
            _scrollback.IsFollowingBottom &&
            _scrollback.Count <= TranscriptHeight;
        var leadingEmptyLines = alignTop ? 0 : TranscriptHeight - visibleMessages.Count;

        for (var i = 0; i < leadingEmptyLines; i++)
        {
            rows.Add(new string(' ', _width));
        }

        foreach (var line in visibleMessages.Take(TranscriptHeight))
        {
            rows.Add(TuiText.TruncateToWidth(line, _width, string.Empty, pad: true));
        }

        var trailingEmptyLines = alignTop
            ? Math.Max(0, TranscriptHeight - visibleMessages.Count)
            : 0;
        for (var i = 0; i < trailingEmptyLines; i++)
        {
            rows.Add(new string(' ', _width));
        }

        // 预留区保持为空，由输入框覆盖层在同一行范围内绘制
        for (var i = 0; i < ReservedBottomLines; i++)
        {
            rows.Add(new string(' ', _width));
        }

        rows.AddRange(_statusBar.Render(_width).TakeLast(StatusHeight));
        return rows;
    }

    private void RebuildScrollback(bool followBottom, int scrollOffsetFromBottom)
    {
        _scrollback.Replace(TuiMessageArea.RenderMessages(_messageArea.Messages, _width, displayOptions: _displayOptions));
        if (!followBottom)
        {
            _scrollback.ScrollUp(scrollOffsetFromBottom);
        }
    }
}
