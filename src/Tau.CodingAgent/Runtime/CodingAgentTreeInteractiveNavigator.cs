using System.Text;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentTreeInteractiveNavigator
{
    private const int PageStep = 5;

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
        IEnumerable<string>? initialFoldedEntryIds = null,
        Action<IReadOnlySet<string>>? foldedEntryIdsChanged = null,
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
        var foldedEntryIds = new HashSet<string>(
            initialFoldedEntryIds?.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()) ?? [],
            StringComparer.OrdinalIgnoreCase);
        var showInspector = false;
        visibleItems = ApplyFilter(items, FilterCycle[filterIndex], searchPattern, foldedEntryIds);
        selected = FindIndexById(visibleItems, items[^1].EntryId);

        Render(items, visibleItems, selected, writer, clearScreen, searchPattern, FilterCycle[filterIndex], foldedEntryIds, showInspector);
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
                case ConsoleKey.LeftArrow:
                    if (IsBranchNavigationModifier(key))
                    {
                        var currentEntryId = visibleItems.Count > 0 ? visibleItems[selected].EntryId : null;
                        var navigation = BuildVisibleNavigation(items, visibleItems);
                        if (currentEntryId is not null && IsSegmentFoldable(currentEntryId, navigation) && !foldedEntryIds.Contains(currentEntryId))
                        {
                            foldedEntryIds.Add(currentEntryId);
                            NotifyFoldedEntryIdsChanged(foldedEntryIds, foldedEntryIdsChanged);
                            visibleItems = ApplyFilter(items, FilterCycle[filterIndex], searchPattern, foldedEntryIds);
                            selected = FindIndexById(visibleItems, currentEntryId);
                        }
                        else if (visibleItems.Count > 0)
                        {
                            selected = FindBranchSegmentStart(visibleItems, selected, navigation, direction: -1);
                        }
                    }
                    else if (visibleItems.Count > 0)
                    {
                        selected = Math.Max(0, selected - PageStep);
                    }
                    break;
                case ConsoleKey.RightArrow:
                    if (IsBranchNavigationModifier(key))
                    {
                        var currentEntryId = visibleItems.Count > 0 ? visibleItems[selected].EntryId : null;
                        var navigation = BuildVisibleNavigation(items, visibleItems);
                        if (currentEntryId is not null && foldedEntryIds.Remove(currentEntryId))
                        {
                            NotifyFoldedEntryIdsChanged(foldedEntryIds, foldedEntryIdsChanged);
                            visibleItems = ApplyFilter(items, FilterCycle[filterIndex], searchPattern, foldedEntryIds);
                            selected = FindIndexById(visibleItems, currentEntryId);
                        }
                        else if (visibleItems.Count > 0)
                        {
                            selected = FindBranchSegmentStart(visibleItems, selected, navigation, direction: 1);
                        }
                    }
                    else if (visibleItems.Count > 0)
                    {
                        selected = Math.Min(visibleItems.Count - 1, selected + PageStep);
                    }
                    break;
                case ConsoleKey.PageUp:
                    if (visibleItems.Count > 0) selected = Math.Max(0, selected - PageStep);
                    break;
                case ConsoleKey.PageDown:
                    if (visibleItems.Count > 0) selected = Math.Min(visibleItems.Count - 1, selected + PageStep);
                    break;
                case ConsoleKey.Enter:
                    writer.WriteLine();
                    return visibleItems.Count == 0
                        ? new Result(null, selected, frames)
                        : new Result(visibleItems[selected].EntryId, selected, frames);
                case ConsoleKey.Q:
                    writer.WriteLine();
                    return new Result(null, selected, frames);
                case ConsoleKey.Escape:
                    if (searchPattern is not null)
                    {
                        var currentId = visibleItems.Count > 0 ? visibleItems[selected].EntryId : null;
                        searchPattern = null;
                        ClearFoldedEntryIds(foldedEntryIds, foldedEntryIdsChanged);
                        visibleItems = ApplyFilter(items, FilterCycle[filterIndex], null, foldedEntryIds);
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
                        ClearFoldedEntryIds(foldedEntryIds, foldedEntryIdsChanged);
                        visibleItems = ApplyFilter(items, FilterCycle[filterIndex], searchPattern, foldedEntryIds);
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
                        ClearFoldedEntryIds(foldedEntryIds, foldedEntryIdsChanged);
                        visibleItems = ApplyFilter(items, FilterCycle[filterIndex], searchPattern, foldedEntryIds);
                        selected = visibleItems.Count > 0 ? visibleItems.Count - 1 : 0;
                    }
                    break;
                case ConsoleKey.Spacebar:
                    if (visibleItems.Count > 0)
                    {
                        var currentEntryId = visibleItems[selected].EntryId;
                        if (ToggleFold(items, currentEntryId, foldedEntryIds))
                        {
                            NotifyFoldedEntryIdsChanged(foldedEntryIds, foldedEntryIdsChanged);
                        }
                        visibleItems = ApplyFilter(items, FilterCycle[filterIndex], searchPattern, foldedEntryIds);
                        selected = FindIndexById(visibleItems, currentEntryId);
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
                case ConsoleKey.I:
                    if ((key.Modifiers & ConsoleModifiers.Control) == 0)
                    {
                        showInspector = !showInspector;
                    }
                    else
                    {
                        continue;
                    }
                    break;
                default:
                    continue;
            }

            if (visibleItems.Count == 0)
            {
                Render(items, visibleItems, 0, writer, clearScreen, searchPattern, FilterCycle[filterIndex], foldedEntryIds, showInspector);
            }
            else
            {
                Render(items, visibleItems, selected, writer, clearScreen, searchPattern, FilterCycle[filterIndex], foldedEntryIds, showInspector);
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
        string? searchPattern,
        IReadOnlySet<string> foldedEntryIds)
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

        if (foldedEntryIds.Count > 0)
        {
            var byId = items.ToDictionary(static item => item.EntryId, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(item => !IsHiddenByFold(item, byId, foldedEntryIds));
        }

        return filtered.ToArray();
    }

    private sealed record VisibleNavigation(
        IReadOnlyDictionary<string, CodingAgentTreeViewItem> AllById,
        IReadOnlyDictionary<string, string?> VisibleParentById,
        IReadOnlyDictionary<string, IReadOnlyList<string>> VisibleChildrenById);

    private static bool IsBranchNavigationModifier(ConsoleKeyInfo key) =>
        (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0;

    private static VisibleNavigation BuildVisibleNavigation(
        IReadOnlyList<CodingAgentTreeViewItem> allItems,
        IReadOnlyList<CodingAgentTreeViewItem> visibleItems)
    {
        var allById = allItems.ToDictionary(static item => item.EntryId, StringComparer.OrdinalIgnoreCase);
        var visibleIds = visibleItems.Select(static item => item.EntryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleParentById = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var visibleChildrenById = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        static void AddChild(IDictionary<string, List<string>> childrenById, string? parentId, string childId)
        {
            var key = parentId ?? string.Empty;
            if (!childrenById.TryGetValue(key, out var children))
            {
                children = [];
                childrenById[key] = children;
            }

            children.Add(childId);
        }

        foreach (var item in visibleItems)
        {
            var parentId = FindNearestVisibleParent(item, allById, visibleIds);
            visibleParentById[item.EntryId] = parentId;
            AddChild(visibleChildrenById, parentId, item.EntryId);
        }

        var readonlyChildren = visibleChildrenById.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);

        return new VisibleNavigation(allById, visibleParentById, readonlyChildren);
    }

    private static string? FindNearestVisibleParent(
        CodingAgentTreeViewItem item,
        IReadOnlyDictionary<string, CodingAgentTreeViewItem> allById,
        IReadOnlySet<string> visibleIds)
    {
        var parentId = item.ParentEntryId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(parentId) && guard++ < 256)
        {
            if (visibleIds.Contains(parentId))
            {
                return parentId;
            }

            parentId = allById.TryGetValue(parentId, out var parent) ? parent.ParentEntryId : null;
        }

        return null;
    }

    private static bool IsSegmentFoldable(string entryId, VisibleNavigation navigation)
    {
        if (!GetVisibleChildren(navigation, entryId).Any())
        {
            return false;
        }

        if (!navigation.VisibleParentById.TryGetValue(entryId, out var parentId) || parentId is null)
        {
            return true;
        }

        return GetVisibleChildren(navigation, parentId).Count > 1;
    }

    private static int FindBranchSegmentStart(
        IReadOnlyList<CodingAgentTreeViewItem> visibleItems,
        int selected,
        VisibleNavigation navigation,
        int direction)
    {
        if (visibleItems.Count == 0)
        {
            return 0;
        }

        var indexById = visibleItems
            .Select(static (item, index) => new { item.EntryId, index })
            .ToDictionary(static item => item.EntryId, static item => item.index, StringComparer.OrdinalIgnoreCase);
        var currentId = visibleItems[selected].EntryId;

        if (direction > 0)
        {
            while (true)
            {
                var children = GetVisibleChildren(navigation, currentId);
                if (children.Count == 0)
                {
                    return indexById[currentId];
                }

                if (children.Count > 1)
                {
                    return indexById[children[0]];
                }

                currentId = children[0];
            }
        }

        while (true)
        {
            if (!navigation.VisibleParentById.TryGetValue(currentId, out var parentId) || parentId is null)
            {
                return indexById[currentId];
            }

            var siblings = GetVisibleChildren(navigation, parentId);
            if (siblings.Count > 1)
            {
                var segmentIndex = indexById[currentId];
                if (segmentIndex < selected)
                {
                    return segmentIndex;
                }
            }

            currentId = parentId;
        }
    }

    private static IReadOnlyList<string> GetVisibleChildren(VisibleNavigation navigation, string? parentId) =>
        navigation.VisibleChildrenById.TryGetValue(parentId ?? string.Empty, out var children) ? children : [];

    private static bool ToggleFold(
        IReadOnlyList<CodingAgentTreeViewItem> items,
        string entryId,
        ISet<string> foldedEntryIds)
    {
        if (!HasDescendants(items, entryId))
        {
            return false;
        }

        if (!foldedEntryIds.Remove(entryId))
        {
            foldedEntryIds.Add(entryId);
        }

        return true;
    }

    private static void ClearFoldedEntryIds(
        ISet<string> foldedEntryIds,
        Action<IReadOnlySet<string>>? foldedEntryIdsChanged)
    {
        if (foldedEntryIds.Count == 0)
        {
            return;
        }

        foldedEntryIds.Clear();
        NotifyFoldedEntryIdsChanged(foldedEntryIds, foldedEntryIdsChanged);
    }

    private static void NotifyFoldedEntryIdsChanged(
        IEnumerable<string> foldedEntryIds,
        Action<IReadOnlySet<string>>? foldedEntryIdsChanged)
    {
        foldedEntryIdsChanged?.Invoke(new HashSet<string>(foldedEntryIds, StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasDescendants(IReadOnlyList<CodingAgentTreeViewItem> items, string entryId) =>
        items.Any(item => string.Equals(item.ParentEntryId, entryId, StringComparison.OrdinalIgnoreCase));

    private static bool IsHiddenByFold(
        CodingAgentTreeViewItem item,
        IReadOnlyDictionary<string, CodingAgentTreeViewItem> byId,
        IReadOnlySet<string> foldedEntryIds)
    {
        var parentId = item.ParentEntryId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(parentId) && guard++ < 256)
        {
            if (foldedEntryIds.Contains(parentId))
            {
                return true;
            }

            parentId = byId.TryGetValue(parentId, out var parent) ? parent.ParentEntryId : null;
        }

        return false;
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
        IReadOnlyList<CodingAgentTreeViewItem> allItems,
        IReadOnlyList<CodingAgentTreeViewItem> items,
        int selected,
        TextWriter writer,
        Action? clearScreen,
        string? searchPattern,
        CodingAgentTreeFilterMode filterMode,
        IReadOnlySet<string> foldedEntryIds,
        bool showInspector)
    {
        clearScreen?.Invoke();

        var builder = new StringBuilder();
        var filterLabel = filterMode.ToString().ToLowerInvariant();
        var searchLabel = searchPattern is not null ? $" search=\"{searchPattern}\"" : "";
        var countLabel = items.Count > 0 ? $"selected {selected + 1}/{items.Count}" : "no matches";
        var selectedMeta = items.Count > 0
            ? $", entry {items[selected].EntryId}, type {FormatEntryType(items[selected])}, depth {items[selected].Depth}{FormatSelectedFlags(items[selected])}"
            : string.Empty;
        var foldedLabel = foldedEntryIds.Count > 0 ? $", folded {foldedEntryIds.Count}" : string.Empty;
        builder.AppendLine($"tree navigator: {items.Count} entries, {countLabel}{selectedMeta}, filter={filterLabel}{searchLabel}{foldedLabel} - j/k move, Left/Right page, Ctrl/Alt+Left/Right branch, f filter, / search, n/N next/prev, Space fold, i inspect, Enter select, q quit");

        if (items.Count == 0)
        {
            builder.AppendLine("  (no entries match current filter/search)");
        }
        else
        {
            if (showInspector)
            {
                AppendInspector(builder, allItems, items[selected], foldedEntryIds);
            }

            for (var i = 0; i < items.Count; i++)
            {
                var prefix = i == selected ? ">>" : "  ";
                var folded = foldedEntryIds.Contains(items[i].EntryId) ? " [folded]" : string.Empty;
                builder.AppendLine($"{prefix} {items[i].DisplayLine}{folded}");
            }
        }

        writer.Write(builder.ToString());
        writer.Flush();
    }

    private static void AppendInspector(
        StringBuilder builder,
        IReadOnlyList<CodingAgentTreeViewItem> allItems,
        CodingAgentTreeViewItem selected,
        IReadOnlySet<string> foldedEntryIds)
    {
        var parent = string.IsNullOrWhiteSpace(selected.ParentEntryId) ? "none" : selected.ParentEntryId;
        var childCount = allItems.Count(item =>
            string.Equals(item.ParentEntryId, selected.EntryId, StringComparison.OrdinalIgnoreCase));
        var foldState = foldedEntryIds.Contains(selected.EntryId)
            ? "folded"
            : childCount > 0 ? "expanded" : "none";
        var pathState = selected.IsCurrentLeaf ? "leaf" : selected.IsOnBranch ? "branch" : "off-branch";

        builder
            .Append("  details: id ")
            .Append(selected.EntryId)
            .Append(", parent ")
            .Append(parent)
            .Append(", type ")
            .Append(FormatEntryType(selected))
            .Append(", depth ")
            .Append(selected.Depth)
            .Append(", children ")
            .Append(childCount)
            .Append(", fold ")
            .Append(foldState)
            .Append(", path ")
            .AppendLine(pathState);
    }

    private static string FormatEntryType(CodingAgentTreeViewItem item) =>
        string.IsNullOrWhiteSpace(item.EntryType) ? "entry" : item.EntryType;

    private static string FormatSelectedFlags(CodingAgentTreeViewItem item)
    {
        if (item.IsCurrentLeaf)
        {
            return ", leaf";
        }

        return item.IsOnBranch ? ", branch" : string.Empty;
    }
}
