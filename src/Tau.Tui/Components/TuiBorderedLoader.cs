using Tau.Tui.Abstractions;

namespace Tau.Tui.Components;

/// <summary>
/// 在上下边框之间显示加载器，并可选地响应 Escape/Ctrl+C 取消。
/// </summary>
public sealed class TuiBorderedLoader : ITuiInputComponent, IDisposable
{
    private readonly TuiContainer _content = new();
    private readonly TuiLoader _loader;
    private readonly TuiCancellableLoader? _cancellableLoader;

    /// <summary>
    /// 创建带边框的加载器。
    /// </summary>
    /// <param name="message">加载提示文本。</param>
    /// <param name="cancellable">是否允许 Escape/Ctrl+C 取消。</param>
    /// <param name="borderColor">边框颜色函数。</param>
    /// <param name="spinnerFormatter">spinner 颜色函数。</param>
    /// <param name="messageFormatter">提示文本颜色函数。</param>
    /// <param name="requestRender">动画帧变化时请求宿主重绘。</param>
    public TuiBorderedLoader(
        string message,
        bool cancellable = true,
        Func<string, string>? borderColor = null,
        Func<string, string>? spinnerFormatter = null,
        Func<string, string>? messageFormatter = null,
        Action? requestRender = null)
    {
        _content.Add(new TuiDynamicBorder(borderColor));
        if (cancellable)
        {
            _cancellableLoader = new TuiCancellableLoader(
                message,
                spinnerFormatter,
                messageFormatter,
                requestRender,
                autoStart: true);
            _loader = _cancellableLoader.Loader;
            _content.Add(_cancellableLoader);
        }
        else
        {
            _loader = new TuiLoader(
                message,
                spinnerFormatter,
                messageFormatter,
                requestRender,
                autoStart: true);
            _content.Add(_loader);
        }

        if (cancellable)
        {
            _content.Add(new TuiSpacer(1));
            _content.Add(new TuiTextBlock("Esc to cancel", paddingX: 1, paddingY: 0));
        }

        _content.Add(new TuiSpacer(1));
        _content.Add(new TuiDynamicBorder(borderColor));
    }

    /// <summary>获取取消信号；不可取消模式返回永不触发的信号。</summary>
    public CancellationToken Signal => _cancellableLoader?.Signal ?? CancellationToken.None;

    /// <summary>获取内部加载器，便于更新消息或控制动画。</summary>
    public TuiLoader Loader => _loader;

    /// <summary>取消事件回调。</summary>
    public event Action? Aborted
    {
        add
        {
            if (_cancellableLoader is not null)
                _cancellableLoader.Aborted += value;
        }
        remove
        {
            if (_cancellableLoader is not null)
                _cancellableLoader.Aborted -= value;
        }
    }

    /// <summary>
    /// 设置取消时执行的回调；不可取消模式忽略该设置。
    /// </summary>
    /// <param name="handler">取消回调，为空表示清除。</param>
    public Action? OnAbort
    {
        get => _onAbort;
        set
        {
            if (_onAbort is not null && _cancellableLoader is not null)
                _cancellableLoader.Aborted -= _onAbort;
            _onAbort = value;
            if (_onAbort is not null && _cancellableLoader is not null)
                _cancellableLoader.Aborted += _onAbort;
        }
    }

    private Action? _onAbort;

    /// <summary>渲染边框、加载器和取消提示。</summary>
    /// <param name="width">终端可用列数。</param>
    /// <returns>多行渲染结果。</returns>
    public IReadOnlyList<string> Render(int width) => _content.Render(width);

    /// <summary>转发取消按键到可取消加载器。</summary>
    /// <param name="key">终端按键。</param>
    /// <returns>按键是否被消费。</returns>
    public TuiInputResult HandleInput(ConsoleKeyInfo key) =>
        _cancellableLoader?.HandleInput(key) ?? TuiInputResult.Ignored;

    /// <summary>使子组件缓存失效。</summary>
    public void Invalidate() => _content.Invalidate();

    /// <summary>停止 spinner 并释放取消资源。</summary>
    public void Dispose()
    {
        _cancellableLoader?.Dispose();
        if (_cancellableLoader is null)
            _loader.Dispose();
        GC.SuppressFinalize(this);
    }
}
