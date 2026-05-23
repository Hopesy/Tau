using Tau.Tui.Abstractions;

namespace Tau.Tui.Components;

public class TuiContainer : ITuiComponent
{
    private readonly List<ITuiComponent> _children = [];

    public IReadOnlyList<ITuiComponent> Children => _children;

    public void Add(ITuiComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        _children.Add(component);
    }

    public bool Remove(ITuiComponent component) => _children.Remove(component);

    public void Clear() => _children.Clear();

    public virtual void Invalidate()
    {
        foreach (var child in _children)
        {
            child.Invalidate();
        }
    }

    public virtual IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        foreach (var child in _children)
        {
            lines.AddRange(child.Render(width));
        }

        return lines;
    }
}
