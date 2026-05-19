using System.Text;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentTreeInteractiveNavigator
{
    private static readonly CodingAgentTreeFilterMode[] FilterCycle =
    [
        CodingAgentTreeFilterMode.Default,
        CodingAgentTreeFilterMode.NoTools,
        CodingAgentTreeFilterMode.UserOnly,
        CodingAgentTreeFilterMode.LabeledOnly,
        CodingAgentTreeFilterMode.All
    ];

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

        var visibleItems = items;
        var selected = visibleItems.Count - 1;
        var frames = 0;
        string? searchPattern = null;
        var filterIndex = 0;

        Render(visibleItems, selected, writer, clearScreen, searchPattern, FilterCycle[filterIndex]);
        frames++;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await reader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);

            switch (key.Key)
            {
                case ConsoleKey.J:
                case ConsoleKey.DownArrow:
                    if (selected < visibleItems.Count - 1) selected++;
                    break;
                case ConsoleKey.K:
                case ConsoleKey.UpArrow:
                    if (selected > 0) selected--;
                    break;
                case ConsoleKey.G:
                    selected = (key.Modifiers & ConsoleModifiers.Shift) != 0 ? visibleItems.Count - 1 : 0;
                    break;
                case ConsoleKey.Home:
                    selected = 0;
                    break;
                case ConsoleKey.End:
                    selected = visibleItems.Count - 1;
                    break;
                case ConsoleKey.Enter:
                    writer.WriteLine();
                    return new Result(visibleItems[selected].EntryId, selected, frames);
                case ConsoleKey.Q:
                    writer.WriteLine();
                    return new Result(null, selected, frames);
                case ConsoleKey.Escape:
                    if (searchPattern is not null)
                    {
                        var currentId = visibleItems.Count > 0 ? visibleItems[selected].EntryId : null;
                        searchPattern = null;
                        visibleItems = ApplyFilter(items, FilterCycle[filterIndex], null);
                        selected = FindIndexById(visibleItems, currentId);
                    }
                    else
                    {
                        writer.WriteLine();
                        return new Result(null, selected, frames);
                    }
                    break;
                case ConsoleKey.F:
                    if ((key.Modifiers & ConsoleModifiers.Control) == 0)
                    {
                        var currentEntryId = visibleItems.Count > 0 ? visibleItems[selected].EntryId : null;
                        filterIndex = (filterIndex + 1) % FilterCycle.Length;
                        visibleItems = ApplyFilter(items, FilterCycle[filterIndex], searchPattern);
                        selected = FindIndexById(visibleItems, currentEntryId);
                    }
                    else
                    {
                        continue;
                    }
                    break;
                case ConsoleKey.Oem2: // '/' on US layout
                case ConsoleKey.Divide:
                    searchPattern = await ReadSearchPatternAsync(reader, writer, cancellationToken).ConfigureAwait(false);
                    if (searchPattern is not null)
                    {
                        visibleItems = ApplyFilter(items, FilterCycle[filterIndex], searchPattern);
                        selected = visibleItems.Count > 0 ? visibleItems.Count - 1 : 0;
                    }
                    break;
                case ConsoleKey.N:
                    if (searchPattern is not null && visibleItems.Count > 0)
                    {
                        selected = (key.Modifiers & ConsoleModifiers.Shift) != 0
                            ? (selected > 0 ? selected - 1 : visibleItems.Count - 1)
                            : (selected < visibleItems.Count - 1 ? selected + 1 : 0);
                    }
                    break;
                default:
                    continue;
            }

            if (visibleItems.Count == 0)
            {
                Render(visibleItems, 0, writer, clearScreen, searchPattern, FilterCycle[filterIndex]);
            }
            else
            {
                Render(visibleItems, selected, writer, clearScreen, searchPattern, FilterCycle[filterIndex]);
            }
            frames++;
        }
    }

    private static async Task<string?> ReadSearchPatternAsync(
        IConsoleKeyReader reader,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        var pattern = new List<char>();
        writer.Write("/");
        writer.Flush();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await reader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);

            if (key.Key == ConsoleKey.Enter)
            {
                writer.WriteLine();
                var result = new string(pattern.ToArray());
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                writer.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (pattern.Count > 0)
                {
                    pattern.RemoveAt(pattern.Count - 1);
                    writer.Write("\b \b");
                    writer.Flush();
                }
                continue;
            }

            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                pattern.Add(key.KeyChar);
                writer.Write(key.KeyChar);
                writer.Flush();
            }
        }
    }

    private static IReadOnlyList<CodingAgentTreeViewItem> ApplyFilter(
        IReadOnlyList<CodingAgentTreeViewItem> items,
        CodingAgentTreeFilterMode filterMode,
        string? searchPattern)
    {
        IEnumerable<CodingAgentTreeViewItem> filtered = items;

        if (filterMode != CodingAgentTreeFilterMode.Default && filterMode != CodingAgentTreeFilterMode.All)
        {
            filtered = filtered.Where(item => MatchesFilterMode(item, filterMode));
        }

        if (!string.IsNullOrWhiteSpace(searchPattern))
        {
            filtered = filtered.Where(item =>
                item.DisplayLine.Contains(searchPattern, StringComparison.OrdinalIgnoreCase));
        }

        return filtered.ToArray();
    }

    private static int FindIndexById(IReadOnlyList<CodingAgentTreeViewItem> items, string? entryId)
    {
        if (entryId is null || items.Count == 0)
        {
            return Math.Max(0, items.Count - 1);
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].EntryId.Equals(entryId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return Math.Max(0, items.Count - 1);
    }

    private static bool MatchesFilterMode(CodingAgentTreeViewItem item, CodingAgentTreeFilterMode mode)
    {
        return mode switch
        {
            CodingAgentTreeFilterMode.NoTools =>
                !item.DisplayLine.Contains("message toolResult", StringComparison.OrdinalIgnoreCase) &&
                !item.DisplayLine.Contains("message assistant [tool-only]", StringComparison.OrdinalIgnoreCase),
            CodingAgentTreeFilterMode.UserOnly =>
                item.DisplayLine.Contains("message user", StringComparison.OrdinalIgnoreCase),
            CodingAgentTreeFilterMode.LabeledOnly =>
                item.DisplayLine.Contains('[') && item.DisplayLine.Contains(']'),
            _ => true
        };
    }

    private static void Render(
        IReadOnlyList<CodingAgentTreeViewItem> items,
        int selected,
        TextWriter writer,
        Action? clearScreen,
        string? searchPattern,
        CodingAgentTreeFilterMode filterMode)
    {
        clearScreen?.Invoke();

        var builder = new StringBuilder();
        var filterLabel = filterMode.ToString().ToLowerInvariant();
        var searchLabel = searchPattern is not null ? $" search=\"{searchPattern}\"" : "";
        var countLabel = items.Count > 0 ? $"selected {selected + 1}/{items.Count}" : "no matches";
        builder.AppendLine($"tree navigator: {items.Count} entries, {countLabel}, filter={filterLabel}{searchLabel} — j/k move, f filter, / search, n/N next/prev, Enter select, q quit");

        if (items.Count == 0)
        {
            builder.AppendLine("  (no entries match current filter/search)");
        }
        else
        {
            for (var i = 0; i < items.Count; i++)
            {
                var prefix = i == selected ? ">>" : "  ";
                builder.AppendLine($"{prefix} {items[i].DisplayLine}");
            }
        }

        writer.Write(builder.ToString());
        writer.Flush();
    }
}
