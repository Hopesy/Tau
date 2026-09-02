using Tau.Tui.Abstractions;

namespace Tau.Tui.Components;

/// <summary>
/// 根据当前可用宽度绘制一条动态边框线。
/// </summary>
public class TuiDynamicBorder : ITuiComponent
{
    private readonly Func<string, string> _color;

    /// <summary>创建动态边框。</summary>
    /// <param name="color">对边框文本应用颜色的函数。</param>
    public TuiDynamicBorder(Func<string, string>? color = null)
    {
        _color = color ?? (static text => text);
    }

    /// <summary>边框没有内部缓存，因此无需执行额外失效操作。</summary>
    public void Invalidate()
    {
    }

    /// <summary>按宽度生成一行水平边框。</summary>
    /// <param name="width">终端可用列数。</param>
    /// <returns>包含一行边框的渲染结果。</returns>
    public IReadOnlyList<string> Render(int width)
    {
        var line = new string('─', Math.Max(1, width));
        return [_color(line)];
    }
}
