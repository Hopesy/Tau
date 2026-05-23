namespace Tau.Tui.Abstractions;

public interface ITuiComponent
{
    IReadOnlyList<string> Render(int width);
    void Invalidate();
}

public interface ITuiInputComponent : ITuiComponent
{
    TuiInputResult HandleInput(ConsoleKeyInfo key);
}

public readonly record struct TuiInputResult(bool Consumed)
{
    public static TuiInputResult Ignored { get; } = new(false);
    public static TuiInputResult Handled { get; } = new(true);
}
