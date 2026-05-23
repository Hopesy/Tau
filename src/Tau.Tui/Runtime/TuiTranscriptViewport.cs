using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Runtime;

public sealed class TuiTranscriptViewport
{
    private readonly TuiMessageArea _messageArea;
    private readonly TuiStatusBar _statusBar;
    private readonly TuiScrollbackBuffer _scrollback;
    private int _width;
    private int _height;

    public TuiTranscriptViewport(
        int width,
        int height,
        IEnumerable<TuiMessage>? messages = null,
        string statusLeft = "",
        string statusRight = "",
        int maxScrollbackLines = 10_000)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _messageArea = new TuiMessageArea();
        _statusBar = new TuiStatusBar(statusLeft, statusRight);
        _scrollback = new TuiScrollbackBuffer(ScrollbackHeight, maxScrollbackLines);

        if (messages is not null)
        {
            SetMessages(messages);
        }
    }

    public int Width => _width;
    public int Height => _height;
    public int MessageHeight => Math.Max(0, _height - 1);
    public int ScrollOffsetFromBottom => _scrollback.ScrollOffsetFromBottom;
    public bool IsFollowingBottom => _scrollback.IsFollowingBottom;
    public IReadOnlyList<TuiMessage> Messages => _messageArea.Messages;
    public IReadOnlyList<string> ScrollbackLines => _scrollback.Lines;
    public string StatusLeft => _statusBar.Left;
    public string StatusRight => _statusBar.Right;

    private int ScrollbackHeight => Math.Max(1, MessageHeight);

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

        _scrollback.Append(TuiMessageArea.RenderMessages(appended, _width));
    }

    public void ClearMessages()
    {
        _messageArea.Clear();
        _scrollback.Clear();
    }

    public void SetStatus(string left, string right) => _statusBar.SetSegments(left, right);

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var widthChanged = width != _width;
        var wasFollowingBottom = _scrollback.IsFollowingBottom;
        var previousOffset = _scrollback.ScrollOffsetFromBottom;

        _width = width;
        _height = height;
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

    public void ScrollBottom() => _scrollback.ScrollToBottom();

    public IReadOnlyList<string> Render()
    {
        var rows = new List<string>(_height);
        var visibleMessages = MessageHeight == 0 ? [] : _scrollback.VisibleLines();

        for (var i = visibleMessages.Count; i < MessageHeight; i++)
        {
            rows.Add(new string(' ', _width));
        }

        foreach (var line in visibleMessages.Take(MessageHeight))
        {
            rows.Add(TuiText.TruncateToWidth(line, _width, string.Empty, pad: true));
        }

        rows.AddRange(_statusBar.Render(_width));
        return rows;
    }

    private void RebuildScrollback(bool followBottom, int scrollOffsetFromBottom)
    {
        _scrollback.Replace(TuiMessageArea.RenderMessages(_messageArea.Messages, _width));
        if (!followBottom)
        {
            _scrollback.ScrollUp(scrollOffsetFromBottom);
        }
    }
}
