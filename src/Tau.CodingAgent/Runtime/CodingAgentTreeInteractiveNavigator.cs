using System.Text;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentTreeInteractiveNavigator
{
    public sealed record Result(string? SelectedEntryId, int LastIndex, int Frames);

    public async Task<Result> NavigateAsync(
        IReadOnlyList<CodingAgentTreeViewItem> items,
        IConsoleKeyReader reader,
        TextWriter writer,
        Action? clearScreen = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        if (items.Count == 0)
        {
            return new Result(null, 0, 0);
        }

        var selected = items.Count - 1;
        var frames = 0;
        Render(items, selected, writer, clearScreen);
        frames++;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await reader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);

            switch (key.Key)
            {
                case ConsoleKey.J:
                case ConsoleKey.DownArrow:
                    if (selected < items.Count - 1) selected++;
                    break;
                case ConsoleKey.K:
                case ConsoleKey.UpArrow:
                    if (selected > 0) selected--;
                    break;
                case ConsoleKey.G:
                    selected = (key.Modifiers & ConsoleModifiers.Shift) != 0 ? items.Count - 1 : 0;
                    break;
                case ConsoleKey.Home:
                    selected = 0;
                    break;
                case ConsoleKey.End:
                    selected = items.Count - 1;
                    break;
                case ConsoleKey.Enter:
                    writer.WriteLine();
                    return new Result(items[selected].EntryId, selected, frames);
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    writer.WriteLine();
                    return new Result(null, selected, frames);
                default:
                    continue;
            }

            Render(items, selected, writer, clearScreen);
            frames++;
        }
    }

    private static void Render(
        IReadOnlyList<CodingAgentTreeViewItem> items,
        int selected,
        TextWriter writer,
        Action? clearScreen)
    {
        clearScreen?.Invoke();

        var builder = new StringBuilder();
        builder.AppendLine($"tree navigator: {items.Count} entries, selected {selected + 1}/{items.Count} — j/k/↑/↓ move, g/G first/last, Enter select, q/Esc quit");
        for (var i = 0; i < items.Count; i++)
        {
            var prefix = i == selected ? ">>" : "  ";
            builder.AppendLine($"{prefix} {items[i].DisplayLine}");
        }

        writer.Write(builder.ToString());
        writer.Flush();
    }
}
