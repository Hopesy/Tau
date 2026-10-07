// 作者：xxx
using System.Globalization;
using Tau.Tui;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【认证选择器】包含搜索输入、认证状态及边界导航的认证选择界面。</summary>
public sealed class CodingAgentAuthSelectorComponent : ITuiInputComponent, Focusable
{
    private readonly CodingAgentAuthSelectorState _state;
    private readonly TuiSelectList _selector;
    private readonly CodingAgentTheme? _theme;
    private readonly int _maxVisible;
    private readonly bool _empty;
    private string _search = "";
    private int _cursor;

    /// <summary>【CodingAgent】【认证选择器】创建独立搜索状态，初始无匹配时仍允许修改查询。</summary>
    /// <param name="state">认证列表、动作及初始查询。</param><param name="maxVisible">可见列表长度。</param><param name="theme">可选主题。</param>
    public CodingAgentAuthSelectorComponent(CodingAgentAuthSelectorState state, int maxVisible = 8, CodingAgentTheme? theme = null)
    {
        _state = state;
        _theme = theme;
        _maxVisible = Math.Max(1, maxVisible);
        _selector = CodingAgentAuthSelector.CreateSelectList(state, _maxVisible);
        _empty = _selector.FilteredItems.Count == 0;
        SetSearch(state.InitialFilter ?? "");
    }

    public event Action<TuiSelectItem>? Selected;
    public event Action? Cancelled;
    public bool Focused { get; set; } = true;
    public string Search => _search;
    public int CursorPosition => _cursor;
    public IReadOnlyList<TuiSelectItem> FilteredItems => _selector.FilteredItems;
    public TuiSelectItem? SelectedItem => _selector.SelectedItem;

    /// <summary>【CodingAgent】【认证搜索】替换查询并把输入光标放到末尾。</summary>
    /// <param name="search">新查询。</param>
    public void SetSearch(string search)
    {
        _search = search;
        _cursor = search.Length;
        Refilter();
    }

    /// <summary>【CodingAgent】【认证搜索】刷新过滤结果并保留原来的有效选择位置。</summary>
    private void Refilter()
    {
        var index = _selector.SelectedIndex;
        _selector.SetFilter(_search);
        _selector.SetSelectedIndex(index);
    }

    /// <summary>【CodingAgent】【认证输入】方向键选择条目，文字键始终输入查询，编辑按 Unicode 文本元素移动。</summary>
    /// <param name="key">控制台输入。</param><returns>是否消费输入。</returns>
    public TuiInputResult HandleInput(ConsoleKeyInfo key)
    {
        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (key.Key == ConsoleKey.Escape || control && key.Key == ConsoleKey.C) Cancelled?.Invoke();
        else if (key.Key == ConsoleKey.Enter) { if (SelectedItem is { } selected) Selected?.Invoke(selected); }
        else if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            _selector.SetSelectedIndex(_selector.SelectedIndex + (key.Key == ConsoleKey.UpArrow ? -1 : 1));
        else if (key.Key == ConsoleKey.LeftArrow || control && key.Key == ConsoleKey.B) _cursor = PreviousBoundary();
        else if (key.Key == ConsoleKey.RightArrow || control && key.Key == ConsoleKey.F) _cursor = NextBoundary();
        else if (key.Key == ConsoleKey.Home || control && key.Key == ConsoleKey.A) _cursor = 0;
        else if (key.Key == ConsoleKey.End || control && key.Key == ConsoleKey.E) _cursor = _search.Length;
        else if (key.Key == ConsoleKey.Backspace && _cursor > 0)
        {
            var start = PreviousBoundary();
            _search = _search.Remove(start, _cursor - start);
            _cursor = start;
            Refilter();
        }
        else if (key.Key == ConsoleKey.Delete || control && key.Key == ConsoleKey.D)
        {
            _search = _search.Remove(_cursor, NextBoundary() - _cursor);
            Refilter();
        }
        else if (control && key.Key == ConsoleKey.U)
        {
            _search = _search[_cursor..];
            _cursor = 0;
            Refilter();
        }
        else if (control && key.Key == ConsoleKey.K)
        {
            _search = _search[.._cursor];
            Refilter();
        }
        else if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0 && !char.IsControl(key.KeyChar))
        {
            _search = _search.Insert(_cursor++, key.KeyChar.ToString());
            Refilter();
        }
        else return TuiInputResult.Ignored;
        return TuiInputResult.Handled;
    }

    /// <summary>【CodingAgent】【搜索光标】定位光标前一个完整 Unicode 文本元素。</summary>
    /// <returns>前一边界。</returns>
    private int PreviousBoundary() => StringInfo.ParseCombiningCharacters(_search).LastOrDefault(index => index < _cursor);

    /// <summary>【CodingAgent】【搜索光标】定位光标后一个完整 Unicode 文本元素。</summary>
    /// <returns>后一边界。</returns>
    private int NextBoundary() => StringInfo.ParseCombiningCharacters(_search).FirstOrDefault(index => index > _cursor, _search.Length);

    /// <summary>【CodingAgent】【认证渲染】按当前终端宽度绘制标题、搜索框、条目和空状态。</summary>
    /// <param name="width">可用列数。</param><returns>不超过可用宽度的终端行。</returns>
    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        var title = _state.Title ?? (_state.Mode switch { "logout" => "Select provider to logout:", "auth" => "Select provider to inspect:", _ => "Select provider to configure:" });
        var lines = new List<string> { Color("border", new string('─', width)), "", Color("accent", " " + title), "", RenderSearch(width), "" };
        var items = _selector.FilteredItems;
        var start = Math.Max(0, Math.Min(_selector.SelectedIndex - _maxVisible / 2, items.Count - _maxVisible));
        var end = Math.Min(start + _maxVisible, items.Count);
        var mixed = _state.Options.Select(option => option.AuthType).Distinct(StringComparer.Ordinal).Skip(1).Any();
        for (var i = start; i < end; i++)
        {
            var item = items[i];
            var option = _state.Options.FirstOrDefault(option => option.SelectionKey == item.Value);
            var selected = i == _selector.SelectedIndex;
            var line = " " + Color(selected ? "accent" : "text", (selected ? "→ " : "  ") + item.Label);
            if (option is not null && !(_state.Mode == "login-method" && option.Provider == "__auth_method__"))
            {
                if (mixed && _state.Mode != "login-method") line += Color("muted", $" [{CodingAgentAuthSelector.FormatAuthType(option.AuthType, option.Subscription)}]");
                var status = CodingAgentAuthSelector.FormatOptionStatus(option);
                var statusColor = option.Status is not { IsConfigured: true } ? "muted"
                    : option.Status.UsesOAuth == (option.AuthType == "oauth") ? "success" : "warning";
                line += Color(statusColor, status);
            }
            else if (option is null) line += Color("muted", " " + item.Description);
            lines.Add(line);
        }
        if (start > 0 || end < items.Count) lines.Add(Color("muted", $"   ({_selector.SelectedIndex + 1}/{items.Count})"));
        if (items.Count == 0) lines.Add(Color("muted", "   " + (!_empty ? "No matching providers" : _state.Mode == "logout"
            ? "No providers logged in. Use /login first." : "No providers available")));
        lines.Add("");
        lines.Add(Color("border", new string('─', width)));
        return lines.Select(line => TuiText.TruncateToWidth(line, width, string.Empty)).ToArray();
    }

    /// <summary>【CodingAgent】【搜索渲染】横向滚动查询以保留可见光标，窄终端不拆分字符。</summary>
    /// <param name="width">可用列数。</param><returns>带输入光标的单行查询。</returns>
    private string RenderSearch(int width)
    {
        var available = Math.Max(0, width - 2);
        if (available == 0) return "> ";
        var before = _search[.._cursor];
        while (before.Length > 0 && TuiText.VisibleWidth(before) >= available)
        {
            var offsets = StringInfo.ParseCombiningCharacters(before);
            before = before[(offsets.Length > 1 ? offsets[1] : before.Length)..];
        }
        var after = _search[_cursor..];
        var cursorEnd = after.Length == 0 ? 0 : StringInfo.GetNextTextElementLength(after);
        var cursor = cursorEnd == 0 ? " " : after[..cursorEnd];
        return "> " + before + (Focused ? "\u001b[7m" + cursor + "\u001b[27m" : cursor) + after[cursorEnd..];
    }

    /// <summary>【CodingAgent】【认证主题】将主题颜色转换为前景 ANSI 序列，缺省保持纯文本。</summary>
    /// <param name="role">颜色用途。</param><param name="text">正文。</param><returns>带颜色的正文。</returns>
    private string Color(string role, string text)
    {
        if (_theme?.Colors.GetValueOrDefault(role) is not { } color || color.Length == 0) return text;
        if (color.Length == 7 && color[0] == '#' && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return $"\u001b[38;2;{rgb >> 16};{(rgb >> 8) & 255};{rgb & 255}m{text}\u001b[39m";
        return int.TryParse(color, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index is >= 0 and <= 255
            ? $"\u001b[38;5;{index}m{text}\u001b[39m" : text;
    }

    /// <summary>【CodingAgent】【认证渲染】组件没有渲染缓存，失效时由宿主重新请求帧。</summary>
    public void Invalidate() { }
}
