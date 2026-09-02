using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.Tui;

/// <summary>与上游 TUI Component 对齐的公共渲染组件接口。</summary>
public interface Component : ITuiComponent
{
}

/// <summary>可接收焦点并输出硬件光标标记的组件接口。</summary>
public interface Focusable
{
    /// <summary>组件当前是否获得焦点。</summary>
    bool Focused { get; set; }
}

/// <summary>TUI 硬件光标零宽标记。</summary>
public static class TuiMarkers
{
    /// <summary>用于定位终端硬件光标的 APC 序列。</summary>
    public const string CURSOR_MARKER = "\u001b_pi:c\u0007";
}

/// <summary>通用子组件容器。</summary>
public sealed class Container : Component
{
    private readonly List<Component> _children = [];
    /// <summary>添加子组件。</summary>
    /// <param name="component">子组件。</param>
    public void Add(Component component) { ArgumentNullException.ThrowIfNull(component); _children.Add(component); }
    /// <summary>移除子组件。</summary>
    /// <param name="component">子组件。</param>
    /// <returns>是否移除成功。</returns>
    public bool Remove(Component component) => _children.Remove(component);
    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width) => _children.SelectMany(child => child.Render(width)).ToArray();
    /// <inheritdoc />
    public void Invalidate() { foreach (var child in _children) child.Invalidate(); }
}

/// <summary>上游 DynamicBorder 的公共名称兼容实现。</summary>
public sealed class DynamicBorder : Component
{
    private readonly TuiDynamicBorder _inner;

    /// <summary>创建动态边框。</summary>
    /// <param name="color">边框颜色函数。</param>
    public DynamicBorder(Func<string, string>? color = null) => _inner = new TuiDynamicBorder(color);

    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width) => _inner.Render(width);

    /// <inheritdoc />
    public void Invalidate() => _inner.Invalidate();
}

/// <summary>上游 BorderedLoader 的公共名称兼容实现。</summary>
public sealed class BorderedLoader : Component, ITuiInputComponent, IDisposable
{
    private readonly TuiBorderedLoader _inner;

    /// <summary>创建带边框的加载器。</summary>
    /// <param name="message">加载提示文本。</param>
    /// <param name="cancellable">是否允许取消。</param>
    /// <param name="borderColor">边框颜色函数。</param>
    /// <param name="spinnerFormatter">spinner 颜色函数。</param>
    /// <param name="messageFormatter">提示颜色函数。</param>
    /// <param name="requestRender">动画重绘回调。</param>
    public BorderedLoader(
        string message,
        bool cancellable = true,
        Func<string, string>? borderColor = null,
        Func<string, string>? spinnerFormatter = null,
        Func<string, string>? messageFormatter = null,
        Action? requestRender = null) =>
        _inner = new TuiBorderedLoader(message, cancellable, borderColor, spinnerFormatter, messageFormatter, requestRender);

    /// <summary>获取取消信号。</summary>
    public CancellationToken Signal => _inner.Signal;

    /// <summary>获取内部 spinner 加载器。</summary>
    public TuiLoader Loader => _inner.Loader;

    /// <summary>加载器被取消时触发。</summary>
    public event Action? Aborted
    {
        add => _inner.Aborted += value;
        remove => _inner.Aborted -= value;
    }

    /// <summary>设置取消时执行的回调。</summary>
    /// <param name="handler">取消回调，为空表示清除。</param>
    public Action? OnAbort
    {
        get => _inner.OnAbort;
        set => _inner.OnAbort = value;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width) => _inner.Render(width);

    /// <inheritdoc />
    public void Invalidate() => _inner.Invalidate();

    /// <summary>处理 Escape/Ctrl+C 取消按键。</summary>
    /// <param name="key">终端按键。</param>
    /// <returns>按键处理结果。</returns>
    public TuiInputResult HandleInput(ConsoleKeyInfo key) => _inner.HandleInput(key);

    /// <summary>释放 spinner 与取消资源。</summary>
    public void Dispose() => _inner.Dispose();
}

/// <summary>Focusable 类型判断辅助。</summary>
public static class TuiFocus
{
    /// <summary>判断组件是否实现可聚焦接口。</summary>
    /// <param name="component">待判断组件。</param>
    /// <returns>实现 Focusable 时返回 true。</returns>
    public static bool IsFocusable(Component? component) => component is Focusable;
}

/// <summary>兼容上游 Input 的单行可编辑文本组件。</summary>
public sealed class Input : Component, Focusable
{
    private readonly TuiTextBlock _text = new(string.Empty, paddingX: 0, paddingY: 0, wrap: false);
    /// <summary>创建输入组件。</summary>
    /// <param name="value">初始值。</param>
    public Input(string value = "") { Value = value; }
    /// <summary>当前输入值。</summary>
    public string Value { get; private set; }
    /// <summary>当前光标在 UTF-16 文本中的位置。</summary>
    public int CursorPosition { get; private set; }
    /// <inheritdoc />
    public bool Focused { get; set; }
    /// <summary>设置输入值。</summary>
    /// <param name="value">新值。</param>
    public void SetValue(string value) { Value = value ?? string.Empty; CursorPosition = Math.Min(CursorPosition, Value.Length); _text.SetText(Value); }
    /// <summary>将光标移动到指定位置并限制在文本范围内。</summary>
    /// <param name="position">目标 UTF-16 位置。</param>
    public void SetCursorPosition(int position) => CursorPosition = Math.Clamp(position, 0, Value.Length);
    /// <summary>在光标处插入文本。</summary>
    /// <param name="text">待插入文本。</param>
    public void Insert(string text)
    {
        text ??= string.Empty;
        Value = Value.Insert(CursorPosition, text);
        CursorPosition += text.Length;
        _text.SetText(Value);
    }
    /// <summary>删除光标前的一个字符。</summary>
    public void Backspace()
    {
        if (CursorPosition == 0) return;
        Value = Value.Remove(CursorPosition - 1, 1);
        CursorPosition--;
        _text.SetText(Value);
    }
    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width)
    {
        var before = Value[..CursorPosition];
        var after = Value[CursorPosition..];
        return [Focused ? before + TuiMarkers.CURSOR_MARKER + after : Value];
    }
    /// <inheritdoc />
    public void Invalidate() => _text.Invalidate();
}

/// <summary>兼容上游 Editor 的多行编辑组件。</summary>
public sealed class Editor : Component, Focusable
{
    private readonly List<string> _lines = [];
    /// <summary>创建编辑器。</summary>
    /// <param name="text">初始文本。</param>
    public Editor(string text = "") => SetText(text);
    /// <summary>当前完整文本。</summary>
    public string Text => string.Join("\n", _lines);
    /// <summary>当前光标所在行。</summary>
    public int CursorLine { get; private set; }
    /// <summary>当前光标所在列。</summary>
    public int CursorColumn { get; private set; }
    /// <inheritdoc />
    public bool Focused { get; set; }
    /// <summary>替换文本内容。</summary>
    /// <param name="text">新文本。</param>
    public void SetText(string text)
    {
        _lines.Clear(); _lines.AddRange((text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        CursorLine = Math.Min(CursorLine, Math.Max(0, _lines.Count - 1));
        CursorColumn = Math.Min(CursorColumn, _lines[CursorLine].Length);
    }
    /// <summary>设置编辑器光标位置。</summary>
    /// <param name="line">行索引。</param>
    /// <param name="column">列索引。</param>
    public void SetCursorPosition(int line, int column)
    {
        CursorLine = Math.Clamp(line, 0, Math.Max(0, _lines.Count - 1));
        CursorColumn = Math.Clamp(column, 0, _lines[CursorLine].Length);
    }
    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width)
    {
        var output = _lines.SelectMany(line => TuiText.Wrap(line, Math.Max(1, width))).ToList();
        if (Focused && output.Count > 0)
        {
            var markerLine = Math.Clamp(CursorLine, 0, output.Count - 1);
            var markerColumn = Math.Min(CursorColumn, output[markerLine].Length);
            output[markerLine] = output[markerLine][..markerColumn] + TuiMarkers.CURSOR_MARKER + output[markerLine][markerColumn..];
        }
        return output;
    }
    /// <inheritdoc />
    public void Invalidate() { }
}

/// <summary>垂直堆叠容器。</summary>
public sealed class VStack : Component
{
    private readonly List<Component> _children = [];
    /// <summary>添加子组件。</summary>
    /// <param name="component">要添加的组件。</param>
    public void Add(Component component) { ArgumentNullException.ThrowIfNull(component); _children.Add(component); }
    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width) => _children.SelectMany(child => child.Render(width)).ToArray();
    /// <inheritdoc />
    public void Invalidate() { foreach (var child in _children) child.Invalidate(); }
}

/// <summary>水平堆叠容器。</summary>
public sealed class HStack : Component
{
    private readonly List<Component> _children = [];
    /// <summary>添加子组件。</summary>
    /// <param name="component">要添加的组件。</param>
    public void Add(Component component) { ArgumentNullException.ThrowIfNull(component); _children.Add(component); }
    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width)
    {
        var childLines = _children.Select(child => child.Render(Math.Max(1, width / Math.Max(1, _children.Count)))).ToArray();
        var height = childLines.Length == 0 ? 0 : childLines.Max(lines => lines.Count);
        return Enumerable.Range(0, height).Select(row => string.Concat(childLines.Select(lines => row < lines.Count ? lines[row] : string.Empty))).Select(line => TuiText.TruncateToWidth(line, width, string.Empty, pad: true)).ToArray();
    }
    /// <inheritdoc />
    public void Invalidate() { foreach (var child in _children) child.Invalidate(); }
}

/// <summary>可滚动的垂直内容容器。</summary>
public sealed class ScrollView : Component
{
    private readonly Component _content;
    private int _offset;
    private int _viewportHeight;
    private int _contentHeight;
    private readonly bool _followEnd;
    private bool _followingEnd;
    private ScrollbarMode _scrollbar;
    /// <summary>创建滚动视图。</summary>
    /// <param name="content">内容组件。</param>
    public ScrollView(Component content, ScrollViewOptions? options = null)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _followEnd = options?.Follow == ScrollFollow.End;
        _followingEnd = _followEnd;
        _scrollbar = options?.Scrollbar ?? ScrollbarMode.Hidden;
    }
    /// <summary>当前滚动偏移。</summary>
    public int Offset => _offset;
    /// <summary>当前内容总行数。</summary>
    public int ContentHeight => _contentHeight;
    /// <summary>当前视口行数。</summary>
    public int ViewportHeight => _viewportHeight;
    /// <summary>是否跟随内容末尾。</summary>
    public bool IsFollowingEnd => _followingEnd;
    /// <summary>滚动条模式。</summary>
    public ScrollbarMode Scrollbar => _scrollbar;
    /// <summary>滚动指定行数。</summary>
    /// <param name="delta">正数向下，负数向上。</param>
    public void Scroll(int delta) => ScrollBy(delta);
    /// <summary>设置视口高度并更新偏移边界。</summary>
    /// <param name="height">视口行数。</param>
    public void SetViewportHeight(int height)
    {
        _viewportHeight = Math.Max(0, height);
        ClampOffset();
    }
    /// <summary>滚动到指定行。</summary>
    /// <param name="offset">目标滚动行。</param>
    /// <param name="disableFollow">是否禁止到末尾时恢复跟随。</param>
    public void ScrollTo(int offset, bool disableFollow = false)
    {
        var max = Math.Max(0, _contentHeight - _viewportHeight);
        _offset = Math.Clamp(offset, 0, max);
        _followingEnd = !disableFollow && _followEnd && _offset == max;
    }
    /// <summary>按行滚动，并返回未能滚动的行数。</summary>
    /// <param name="delta">正数向下，负数向上。</param>
    /// <returns>由于边界未消耗的行数。</returns>
    public int ScrollBy(int delta)
    {
        var max = Math.Max(0, _contentHeight - _viewportHeight);
        var start = _followingEnd ? max : _offset;
        _offset = Math.Clamp(start + delta, 0, max);
        _followingEnd = _followEnd && _offset == max;
        return delta - (_offset - start);
    }
    /// <summary>滚动到内容起点。</summary>
    public void ScrollToStart() => ScrollTo(0, disableFollow: true);
    /// <summary>滚动到内容末尾。</summary>
    public void ScrollToEnd() { var max = Math.Max(0, _contentHeight - _viewportHeight); _offset = max; _followingEnd = _followEnd; }
    /// <summary>修改滚动条模式。</summary>
    /// <param name="mode">滚动条模式。</param>
    public void SetScrollbar(ScrollbarMode mode) => _scrollbar = mode;
    /// <inheritdoc />
    public IReadOnlyList<string> Render(int width) => RenderViewport(width, _viewportHeight);
    /// <summary>按宽度和视口高度渲染一次内容，避免重复调用子组件。</summary>
    /// <param name="width">可用列数。</param>
    /// <param name="height">视口行数，0 表示不裁剪。</param>
    /// <returns>当前视口内的行。</returns>
    public IReadOnlyList<string> RenderViewport(int width, int height)
    {
        var lines = _content.Render(Math.Max(1, width));
        _contentHeight = lines.Count;
        if (height > 0) _viewportHeight = height;
        if (_followingEnd && _followEnd) _offset = Math.Max(0, _contentHeight - _viewportHeight);
        ClampOffset();
        var visible = lines.Skip(Math.Min(_offset, lines.Count)).Take(_viewportHeight > 0 ? _viewportHeight : int.MaxValue).ToArray();
        if (_scrollbar != ScrollbarMode.Hidden && _viewportHeight > 0 && _contentHeight > _viewportHeight)
        {
            var max = Math.Max(1, _viewportHeight - 1);
            var thumb = Math.Max(1, (int)Math.Round((double)_viewportHeight * _viewportHeight / _contentHeight));
            var top = (int)Math.Round((double)_offset * (max - thumb) / Math.Max(1, _contentHeight - _viewportHeight));
            visible = visible.Select((line, index) => (index >= top && index < top + thumb ? "█" : "│") + " " + line).ToArray();
        }
        return visible;
    }
    private void ClampOffset() { var max = Math.Max(0, _contentHeight - _viewportHeight); _offset = Math.Clamp(_offset, 0, max); }
    /// <inheritdoc />
    public void Invalidate() => _content.Invalidate();
}

/// <summary>ScrollView 的跟随策略。</summary>
public enum ScrollFollow { None, End }

/// <summary>ScrollView 滚动条显示策略。</summary>
public enum ScrollbarMode { Hidden, Auto, Always }

/// <summary>ScrollView 构造配置。</summary>
public sealed record ScrollViewOptions
{
    /// <summary>内容跟随策略。</summary>
    public ScrollFollow Follow { get; init; }
    /// <summary>滚动条模式。</summary>
    public ScrollbarMode Scrollbar { get; init; }
}

/// <summary>主屏幕和备用屏幕的统一公共入口。</summary>
public sealed class TuiAltScreen : IDisposable
{
    private readonly TuiCompositionSession _session;
    /// <summary>创建备用屏幕。</summary>
    /// <param name="surface">渲染表面。</param>
    public TuiAltScreen(ITuiRenderSurface surface) => _session = new TuiCompositionSession(surface);
    /// <summary>启动备用屏幕。</summary>
    /// <returns>首帧渲染结果。</returns>
    public TuiTranscriptRenderResult Start() => _session.Start();
    /// <summary>停止备用屏幕。</summary>
    public void Dispose() => _session.Stop();
}

/// <summary>保留当前终端内容的主屏幕渲染入口。</summary>
public sealed class TuiMainScreen
{
    private readonly ITuiRenderSurface _surface;
    /// <summary>创建主屏幕。</summary>
    /// <param name="surface">渲染表面。</param>
    public TuiMainScreen(ITuiRenderSurface surface) => _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    /// <summary>渲染组件到主屏幕。</summary>
    /// <param name="component">要渲染的组件。</param>
    /// <returns>渲染行。</returns>
    public IReadOnlyList<string> Render(Component component) => component.Render(_surface.Width);
}

/// <summary>LaTeX 文本的轻量终端降级渲染器。</summary>
public static class Latex
{
    /// <summary>去除 LaTeX 分隔符并保留可读公式文本。</summary>
    /// <param name="text">原始 Markdown/LaTeX 文本。</param>
    /// <returns>终端可显示文本。</returns>
    public static string RenderLatex(string text) => (text ?? string.Empty).Replace("\\(", string.Empty, StringComparison.Ordinal).Replace("\\)", string.Empty, StringComparison.Ordinal).Replace("\\[", string.Empty, StringComparison.Ordinal).Replace("\\]", string.Empty, StringComparison.Ordinal);
}
