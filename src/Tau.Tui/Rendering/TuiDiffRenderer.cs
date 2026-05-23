using Tau.Tui.Abstractions;

namespace Tau.Tui.Rendering;

public sealed record TuiRenderFrame(int Width, int Height, IReadOnlyList<string> Lines)
{
    public static TuiRenderFrame From(ITuiComponent component, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(component);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return new TuiRenderFrame(width, height, component.Render(width));
    }
}

public enum TuiRenderOperationKind
{
    ReplaceLine,
    ClearLine,
}

public sealed record TuiRenderOperation(TuiRenderOperationKind Kind, int Row, string Text)
{
    public static TuiRenderOperation ReplaceLine(int row, string text) =>
        new(TuiRenderOperationKind.ReplaceLine, row, text);

    public static TuiRenderOperation ClearLine(int row) =>
        new(TuiRenderOperationKind.ClearLine, row, string.Empty);
}

public sealed record TuiRenderDiff(bool RequiresFullRedraw, string Reason, IReadOnlyList<TuiRenderOperation> Operations)
{
    public static TuiRenderDiff Empty { get; } = new(false, string.Empty, []);
}

public sealed class TuiDiffRenderer
{
    private TuiRenderFrame? _previous;

    public TuiRenderFrame? PreviousFrame => _previous;

    public TuiRenderDiff Render(ITuiComponent component, int width, int height, bool force = false)
    {
        var next = TuiRenderFrame.From(component, width, height);
        var diff = Diff(_previous, next, force);
        _previous = next;
        return diff;
    }

    public void Reset() => _previous = null;

    public static TuiRenderDiff Diff(TuiRenderFrame? previous, TuiRenderFrame next, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (previous is null)
        {
            return Full("first render", next);
        }

        if (force)
        {
            return Full("forced", next);
        }

        if (previous.Width != next.Width)
        {
            return Full("width changed", next);
        }

        if (previous.Height != next.Height)
        {
            return Full("height changed", next);
        }

        var operations = new List<TuiRenderOperation>();
        var max = Math.Max(previous.Lines.Count, next.Lines.Count);
        for (var row = 0; row < max; row++)
        {
            var oldLine = row < previous.Lines.Count ? previous.Lines[row] : string.Empty;
            var newLine = row < next.Lines.Count ? next.Lines[row] : string.Empty;
            if (string.Equals(oldLine, newLine, StringComparison.Ordinal))
            {
                continue;
            }

            operations.Add(row < next.Lines.Count
                ? TuiRenderOperation.ReplaceLine(row, newLine)
                : TuiRenderOperation.ClearLine(row));
        }

        return operations.Count == 0
            ? TuiRenderDiff.Empty
            : new TuiRenderDiff(false, "line diff", operations);
    }

    private static TuiRenderDiff Full(string reason, TuiRenderFrame frame)
    {
        var operations = frame.Lines
            .Select((line, row) => TuiRenderOperation.ReplaceLine(row, line))
            .ToArray();
        return new TuiRenderDiff(true, reason, operations);
    }
}
