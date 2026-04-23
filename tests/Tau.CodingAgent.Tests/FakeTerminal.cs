using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Tests;

public sealed class FakeTerminal : ITerminal
{
    private readonly Queue<string?> _inputs = new();

    public List<(string Text, ConsoleColor? Color, bool IsLine)> Writes { get; } = [];

    public void QueueInput(string? input)
    {
        _inputs.Enqueue(input);
    }

    public Task<string?> PromptAsync(string prompt, ConsoleColor? color = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add((prompt, color, false));
        return Task.FromResult(_inputs.Count > 0 ? _inputs.Dequeue() : null);
    }

    public void Write(string text, ConsoleColor? color = null)
    {
        Writes.Add((text, color, false));
    }

    public void WriteLine(string? text = null, ConsoleColor? color = null)
    {
        Writes.Add((text ?? string.Empty, color, true));
    }

    public string FlattenedText()
    {
        return string.Concat(Writes.Select(w => w.IsLine ? $"{w.Text}\n" : w.Text));
    }
}
