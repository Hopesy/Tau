namespace Tau.Tui.Rendering;

public interface ITuiRenderSurface
{
    int Width { get; }
    int Height { get; }
    void Apply(TuiRenderDiff diff);
}

/// <summary>
/// 【终端渲染】【光标控制】定义可控制终端硬件光标可见性的渲染表面能力。
/// </summary>
public interface ITuiCursorVisibilitySurface
{
    /// <summary>
    /// 设置终端硬件光标是否可见。
    /// </summary>
    /// <param name="visible">为 <see langword="true"/> 时显示光标，否则隐藏光标。</param>
    void SetCursorVisible(bool visible);
}
