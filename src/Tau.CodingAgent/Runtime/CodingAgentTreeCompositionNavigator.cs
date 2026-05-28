using System.Text;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public static class CodingAgentTreeCompositionNavigator
{
    public static async Task<CodingAgentTreeInteractiveNavigator.Result> RunAsync(
        IReadOnlyList<CodingAgentTreeViewItem> items,
        TuiCompositionSession session,
        IEnumerable<string>? initialFoldedEntryIds = null,
        Action<IReadOnlySet<string>>? foldedEntryIdsChanged = null,
        Func<string, CodingAgentTreeMetadataSnapshot>? metadataSnapshotProvider = null,
        string? initialSelectedEntryId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(session);

        var component = new CodingAgentTreeNavigatorComponent(
            items,
            () => Math.Max(1, session.Viewport.MessageHeight),
            initialFoldedEntryIds,
            foldedEntryIdsChanged,
            metadataSnapshotProvider is not null,
            initialSelectedEntryId);
        var handle = session.OpenOverlay(
            component,
            new TuiTranscriptOverlayOptions(Width: int.MaxValue, Row: 0, Column: 0));

        try
        {
            if (!session.IsStarted)
            {
                session.Start();
            }

            while (component.Result is null)
            {
                await session.ReadInputAsync(cancellationToken).ConfigureAwait(false);
                if (metadataSnapshotProvider is not null &&
                    component.TryTakeMetadataInspectionRequest(out var entryId))
                {
                    var snapshot = metadataSnapshotProvider(entryId);
                    var selectedMetadataEntryId = await CodingAgentCompositionMetadataViewer
                        .RunWithSelectionAsync(snapshot, session, cancellationToken)
                        .ConfigureAwait(false);
                    component.TrySelectEntry(selectedMetadataEntryId);
                }
            }

            return component.Result!;
        }
        finally
        {
            session.CloseOverlay(handle);
        }
    }

    private sealed class CodingAgentTreeNavigatorComponent : ITuiInputComponent
    {
        private const string SelectedLineStart = "\u001b[7m";
        private const string SelectedLineEnd = "\u001b[27m";
        private const int PageStep = 5;

        private static readonly CodingAgentTreeFilterMode[] FilterCycle =
        [
            CodingAgentTreeFilterMode.Default,
            CodingAgentTreeFilterMode.NoTools,
            CodingAgentTreeFilterMode.UserOnly,
            CodingAgentTreeFilterMode.LabeledOnly,
            CodingAgentTreeFilterMode.All
        ];

        private readonly IReadOnlyList<CodingAgentTreeViewItem> _items;
        private readonly Func<int> _heightProvider;
        private readonly Action<IReadOnlySet<string>>? _foldedEntryIdsChanged;
        private readonly HashSet<string> _foldedEntryIds;
        private readonly bool _metadataInspectionEnabled;
        private IReadOnlyList<CodingAgentTreeViewItem> _visibleItems;
        private int _selectedIndex;
        private int _frames;
        private int _filterIndex;
        private string? _searchPattern;
        private string _pendingSearch = string.Empty;
        private string? _pendingMetadataInspectionEntryId;
        private string? _pendingLabelEditEntryId;
        private bool _showLabelTimestamps;
        private bool _searchInputActive;
        private bool _showInspector;

        public CodingAgentTreeNavigatorComponent(
            IReadOnlyList<CodingAgentTreeViewItem> items,
            Func<int> heightProvider,
            IEnumerable<string>? initialFoldedEntryIds,
            Action<IReadOnlySet<string>>? foldedEntryIdsChanged,
            bool metadataInspectionEnabled,
            string? initialSelectedEntryId)
        {
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _heightProvider = heightProvider ?? throw new ArgumentNullException(nameof(heightProvider));
            _foldedEntryIdsChanged = foldedEntryIdsChanged;
            _metadataInspectionEnabled = metadataInspectionEnabled;
            _foldedEntryIds = new HashSet<string>(
                initialFoldedEntryIds?.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()) ?? [],
                StringComparer.OrdinalIgnoreCase);
            _showLabelTimestamps = items.Any(static item => item.LabelTimestampsEnabled);
            _visibleItems = items;
            RefreshVisible(initialSelectedEntryId ?? (items.Count == 0 ? null : items[^1].EntryId), preferLastVisible: false);
        }

        public CodingAgentTreeInteractiveNavigator.Result? Result { get; private set; }

        public IReadOnlyList<string> Render(int width)
        {
            _frames++;
            width = Math.Max(1, width);
            var maxHeight = Math.Max(1, _heightProvider());

            var headerLines = TuiText.Wrap(BuildHeaderLine(), width).ToList();
            var detailLines = BuildDetailLines(width);
            var searchInputLines = BuildSearchInputLines(width);
            var reservedLines = headerLines.Count + detailLines.Count + searchInputLines.Count;
            var availableItemLines = Math.Max(1, maxHeight - reservedLines);
            var itemLines = BuildItemLines(width, availableItemLines);

            var output = new List<string>(headerLines.Count + detailLines.Count + itemLines.Count + searchInputLines.Count);
            output.AddRange(headerLines);
            output.AddRange(detailLines);
            output.AddRange(itemLines);
            output.AddRange(searchInputLines);
            return output;
        }

        public void Invalidate()
        {
        }

        public TuiInputResult HandleInput(ConsoleKeyInfo key)
        {
            if (Result is not null)
            {
                return TuiInputResult.Handled;
            }

            if (_searchInputActive)
            {
                return HandleSearchInput(key);
            }

            switch (key.Key)
            {
                case ConsoleKey.J:
                case ConsoleKey.DownArrow:
                    if (_visibleItems.Count > 0 && _selectedIndex < _visibleItems.Count - 1)
                    {
                        _selectedIndex++;
                    }
                    return TuiInputResult.Handled;

                case ConsoleKey.K:
                case ConsoleKey.UpArrow:
                    if (_visibleItems.Count > 0 && _selectedIndex > 0)
                    {
                        _selectedIndex--;
                    }
                    return TuiInputResult.Handled;

                case ConsoleKey.G:
                    if (_visibleItems.Count > 0)
                    {
                        _selectedIndex = (key.Modifiers & ConsoleModifiers.Shift) != 0
                            ? _visibleItems.Count - 1
                            : 0;
                    }
                    return TuiInputResult.Handled;

                case ConsoleKey.Home:
                    _selectedIndex = 0;
                    return TuiInputResult.Handled;

                case ConsoleKey.End:
                    if (_visibleItems.Count > 0)
                    {
                        _selectedIndex = _visibleItems.Count - 1;
                    }
                    return TuiInputResult.Handled;

                case ConsoleKey.LeftArrow:
                    HandleHorizontalNavigation(direction: -1, key);
                    return TuiInputResult.Handled;

                case ConsoleKey.RightArrow:
                    HandleHorizontalNavigation(direction: 1, key);
                    return TuiInputResult.Handled;

                case ConsoleKey.PageUp:
                    if (_visibleItems.Count > 0)
                    {
                        _selectedIndex = Math.Max(0, _selectedIndex - PageStep);
                    }
                    return TuiInputResult.Handled;

                case ConsoleKey.PageDown:
                    if (_visibleItems.Count > 0)
                    {
                        _selectedIndex = Math.Min(_visibleItems.Count - 1, _selectedIndex + PageStep);
                    }
                    return TuiInputResult.Handled;

                case ConsoleKey.Enter:
                    Complete(_visibleItems.Count == 0 ? null : _visibleItems[_selectedIndex].EntryId);
                    return TuiInputResult.Handled;

                case ConsoleKey.Q:
                    Complete(null);
                    return TuiInputResult.Handled;

                case ConsoleKey.Escape:
                    if (_searchPattern is not null)
                    {
                        var currentId = CurrentEntryId();
                        _searchPattern = null;
                        ClearFoldedEntryIds();
                        RefreshVisible(currentId, preferLastVisible: false);
                    }
                    else
                    {
                        Complete(null);
                    }

                    return TuiInputResult.Handled;

                case ConsoleKey.F:
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        return TuiInputResult.Ignored;
                    }

                    CycleFilterMode();
                    return TuiInputResult.Handled;

                case ConsoleKey.D:
                    if ((key.Modifiers & ConsoleModifiers.Control) == 0 ||
                        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Shift)) != 0)
                    {
                        return TuiInputResult.Ignored;
                    }

                    SetFilterMode(CodingAgentTreeFilterMode.Default);
                    return TuiInputResult.Handled;

                case ConsoleKey.T:
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0 &&
                        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Shift)) == 0)
                    {
                        ToggleFilterMode(CodingAgentTreeFilterMode.NoTools);
                        return TuiInputResult.Handled;
                    }

                    if ((key.Modifiers & ConsoleModifiers.Shift) == 0 ||
                        (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0)
                    {
                        return TuiInputResult.Ignored;
                    }

                    _showLabelTimestamps = !_showLabelTimestamps;
                    return TuiInputResult.Handled;

                case ConsoleKey.U:
                    if ((key.Modifiers & ConsoleModifiers.Control) == 0 ||
                        (key.Modifiers & ConsoleModifiers.Alt) != 0)
                    {
                        return TuiInputResult.Ignored;
                    }

                    ToggleFilterMode(CodingAgentTreeFilterMode.UserOnly);
                    return TuiInputResult.Handled;

                case ConsoleKey.A:
                    if ((key.Modifiers & ConsoleModifiers.Control) == 0 ||
                        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Shift)) != 0)
                    {
                        return TuiInputResult.Ignored;
                    }

                    ToggleFilterMode(CodingAgentTreeFilterMode.All);
                    return TuiInputResult.Handled;

                case ConsoleKey.O:
                    if ((key.Modifiers & ConsoleModifiers.Control) == 0 ||
                        (key.Modifiers & ConsoleModifiers.Alt) != 0)
                    {
                        return TuiInputResult.Ignored;
                    }

                    CycleFilterMode(backward: (key.Modifiers & ConsoleModifiers.Shift) != 0);
                    return TuiInputResult.Handled;

                case ConsoleKey.Oem2:
                case ConsoleKey.Divide:
                    _searchInputActive = true;
                    _pendingSearch = string.Empty;
                    return TuiInputResult.Handled;

                case ConsoleKey.Spacebar:
                    ToggleCurrentFold();
                    return TuiInputResult.Handled;

                case ConsoleKey.N:
                    if (_searchPattern is not null && _visibleItems.Count > 0)
                    {
                        _selectedIndex = (key.Modifiers & ConsoleModifiers.Shift) != 0
                            ? (_selectedIndex > 0 ? _selectedIndex - 1 : _visibleItems.Count - 1)
                            : (_selectedIndex < _visibleItems.Count - 1 ? _selectedIndex + 1 : 0);
                        return TuiInputResult.Handled;
                    }

                    return TuiInputResult.Ignored;

                case ConsoleKey.I:
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        return TuiInputResult.Ignored;
                    }

                    if (_metadataInspectionEnabled)
                    {
                        _pendingMetadataInspectionEntryId = CurrentEntryId();
                    }
                    else
                    {
                        _showInspector = !_showInspector;
                    }

                    return TuiInputResult.Handled;

                case ConsoleKey.L:
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0 &&
                        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Shift)) == 0)
                    {
                        ToggleFilterMode(CodingAgentTreeFilterMode.LabeledOnly);
                        return TuiInputResult.Handled;
                    }

                    if ((key.Modifiers & ConsoleModifiers.Shift) == 0 ||
                        (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0 ||
                        _visibleItems.Count == 0)
                    {
                        return TuiInputResult.Ignored;
                    }

                    _pendingLabelEditEntryId = CurrentEntryId();
                    Complete(null);
                    return TuiInputResult.Handled;

                default:
                    return TuiInputResult.Ignored;
            }
        }

        public bool TryTakeMetadataInspectionRequest(out string entryId)
        {
            if (string.IsNullOrWhiteSpace(_pendingMetadataInspectionEntryId))
            {
                entryId = string.Empty;
                return false;
            }

            entryId = _pendingMetadataInspectionEntryId;
            _pendingMetadataInspectionEntryId = null;
            return true;
        }

        public bool TrySelectEntry(string? entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return false;
            }

            if (TryFindIndexById(_visibleItems, entryId, out var visibleIndex))
            {
                _selectedIndex = visibleIndex;
                return true;
            }

            if (!TryRevealEntry(entryId))
            {
                return false;
            }

            if (TryFindIndexById(_visibleItems, entryId, out visibleIndex))
            {
                _selectedIndex = visibleIndex;
                return true;
            }

            return false;
        }

        private TuiInputResult HandleSearchInput(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    _searchInputActive = false;
                    _searchPattern = string.IsNullOrWhiteSpace(_pendingSearch) ? null : _pendingSearch;
                    ClearFoldedEntryIds();
                    RefreshVisible(CurrentEntryId(), preferLastVisible: true);
                    return TuiInputResult.Handled;

                case ConsoleKey.Escape:
                    _searchInputActive = false;
                    _pendingSearch = string.Empty;
                    return TuiInputResult.Handled;

                case ConsoleKey.Backspace:
                    if (_pendingSearch.Length > 0)
                    {
                        _pendingSearch = _pendingSearch[..^1];
                    }

                    return TuiInputResult.Handled;

                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        _pendingSearch += key.KeyChar;
                    }

                    return TuiInputResult.Handled;
            }
        }

        private void HandleHorizontalNavigation(int direction, ConsoleKeyInfo key)
        {
            if (_visibleItems.Count == 0)
            {
                return;
            }

            if (IsBranchNavigationModifier(key))
            {
                var currentEntryId = CurrentEntryId();
                var navigation = BuildVisibleNavigation(_items, _visibleItems);
                if (direction < 0 &&
                    currentEntryId is not null &&
                    IsSegmentFoldable(currentEntryId, navigation) &&
                    !_foldedEntryIds.Contains(currentEntryId))
                {
                    _foldedEntryIds.Add(currentEntryId);
                    NotifyFoldedEntryIdsChanged();
                    RefreshVisible(currentEntryId, preferLastVisible: false);
                    return;
                }

                if (direction > 0 &&
                    currentEntryId is not null &&
                    _foldedEntryIds.Remove(currentEntryId))
                {
                    NotifyFoldedEntryIdsChanged();
                    RefreshVisible(currentEntryId, preferLastVisible: false);
                    return;
                }

                _selectedIndex = FindBranchSegmentStart(_visibleItems, _selectedIndex, navigation, direction);
                return;
            }

            _selectedIndex = direction < 0
                ? Math.Max(0, _selectedIndex - PageStep)
                : Math.Min(_visibleItems.Count - 1, _selectedIndex + PageStep);
        }

        private IReadOnlyList<string> BuildDetailLines(int width)
        {
            if (!_showInspector || _visibleItems.Count == 0)
            {
                return Array.Empty<string>();
            }

            return TuiText.Wrap(BuildInspectorLine(_visibleItems[_selectedIndex]), width);
        }

        private IReadOnlyList<string> BuildSearchInputLines(int width) =>
            !_searchInputActive
                ? Array.Empty<string>()
                : TuiText.Wrap($"search> /{_pendingSearch}_", width);

        private List<string> BuildItemLines(int width, int availableLines)
        {
            var lines = new List<string>();
            if (_visibleItems.Count == 0)
            {
                lines.Add(TuiText.TruncateToWidth("  (no entries match current filter/search)", width, string.Empty, pad: true));
                return lines;
            }

            var startIndex = Math.Max(
                0,
                Math.Min(_selectedIndex - (availableLines / 2), _visibleItems.Count - availableLines));
            var endIndex = Math.Min(startIndex + availableLines, _visibleItems.Count);

            for (var index = startIndex; index < endIndex; index++)
            {
                var item = _visibleItems[index];
                var line = $"{(index == _selectedIndex ? ">>" : "  ")} {FormatDisplayLine(item)}{(_foldedEntryIds.Contains(item.EntryId) ? " [folded]" : string.Empty)}";
                var rendered = TuiText.TruncateToWidth(line, width, string.Empty, pad: true);
                lines.Add(index == _selectedIndex ? $"{SelectedLineStart}{rendered}{SelectedLineEnd}" : rendered);
            }

            return lines;
        }

        private string BuildHeaderLine()
        {
            var filterLabel = FilterCycle[_filterIndex].ToString().ToLowerInvariant();
            var searchLabel = _searchPattern is not null ? $" search=\"{_searchPattern}\"" : "";
            var foldedLabel = _foldedEntryIds.Count > 0 ? $", folded {_foldedEntryIds.Count}" : string.Empty;
            var labelTimeLabel = _showLabelTimestamps ? " [+label time]" : string.Empty;
            var countLabel = _visibleItems.Count > 0 ? $"selected {_selectedIndex + 1}/{_visibleItems.Count}" : "no matches";
            var selectedMeta = _visibleItems.Count > 0
                ? $", entry {_visibleItems[_selectedIndex].EntryId}, type {FormatEntryType(_visibleItems[_selectedIndex])}, depth {_visibleItems[_selectedIndex].Depth}{FormatSelectedFlags(_visibleItems[_selectedIndex])}"
                : string.Empty;
            var inspectLabel = _metadataInspectionEnabled ? "i metadata" : "i inspect";
            return $"tree navigator: {_visibleItems.Count} entries, {countLabel}{selectedMeta}, filter={filterLabel}{searchLabel}{foldedLabel}{labelTimeLabel} - j/k move, Left/Right page, Ctrl/Alt+Left/Right branch, f filter, Ctrl+D default, Ctrl+T no-tools, Ctrl+U user-only, Ctrl+L labeled, Ctrl+A all, Ctrl+O/Shift+Ctrl+O cycle, / search, n/N next/prev, Shift+L label, Shift+T label time, Space fold, {inspectLabel}, Enter select, q quit";
        }

        private string BuildInspectorLine(CodingAgentTreeViewItem selected)
        {
            var parent = string.IsNullOrWhiteSpace(selected.ParentEntryId) ? "none" : selected.ParentEntryId;
            var childCount = _items.Count(item =>
                string.Equals(item.ParentEntryId, selected.EntryId, StringComparison.OrdinalIgnoreCase));
            var foldState = _foldedEntryIds.Contains(selected.EntryId)
                ? "folded"
                : childCount > 0 ? "expanded" : "none";
            var pathState = selected.IsCurrentLeaf ? "leaf" : selected.IsOnBranch ? "branch" : "off-branch";
            return $"details: id {selected.EntryId}, parent {parent}, type {FormatEntryType(selected)}, depth {selected.Depth}, children {childCount}, fold {foldState}, path {pathState}";
        }

        private void CycleFilterMode()
        {
            var currentId = CurrentEntryId();
            _filterIndex = (_filterIndex + 1) % FilterCycle.Length;
            ClearFoldedEntryIds();
            RefreshVisible(currentId, preferLastVisible: false);
        }

        private void CycleFilterMode(bool backward)
        {
            var currentId = CurrentEntryId();
            _filterIndex = backward
                ? (_filterIndex - 1 + FilterCycle.Length) % FilterCycle.Length
                : (_filterIndex + 1) % FilterCycle.Length;
            ClearFoldedEntryIds();
            RefreshVisible(currentId, preferLastVisible: false);
        }

        private void SetFilterMode(CodingAgentTreeFilterMode targetMode)
        {
            var currentId = CurrentEntryId();
            _filterIndex = FindFilterIndex(targetMode);
            ClearFoldedEntryIds();
            RefreshVisible(currentId, preferLastVisible: false);
        }

        private void ToggleFilterMode(CodingAgentTreeFilterMode toggleMode)
        {
            var currentId = CurrentEntryId();
            var currentMode = FilterCycle[_filterIndex];
            var nextMode = currentMode == toggleMode
                ? CodingAgentTreeFilterMode.Default
                : toggleMode;
            _filterIndex = Array.IndexOf(FilterCycle, nextMode);
            ClearFoldedEntryIds();
            RefreshVisible(currentId, preferLastVisible: false);
        }

        private void ToggleCurrentFold()
        {
            if (_visibleItems.Count == 0)
            {
                return;
            }

            var currentEntryId = _visibleItems[_selectedIndex].EntryId;
            if (ToggleFold(_items, currentEntryId, _foldedEntryIds))
            {
                NotifyFoldedEntryIdsChanged();
            }

            RefreshVisible(currentEntryId, preferLastVisible: false);
        }

        private void RefreshVisible(string? selectedEntryId, bool preferLastVisible)
        {
            _visibleItems = ApplyFilter(_items, FilterCycle[_filterIndex], _searchPattern, _foldedEntryIds);
            if (_visibleItems.Count == 0)
            {
                _selectedIndex = 0;
                return;
            }

            _selectedIndex = preferLastVisible
                ? _visibleItems.Count - 1
                : FindIndexById(_visibleItems, selectedEntryId);
        }

        private bool TryRevealEntry(string entryId)
        {
            if (!TryFindIndexById(_items, entryId, out var itemIndex))
            {
                return false;
            }

            var itemById = _items.ToDictionary(static item => item.EntryId, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            var parentId = _items[itemIndex].ParentEntryId;
            var guard = 0;
            while (!string.IsNullOrWhiteSpace(parentId) &&
                   guard++ < 256 &&
                   itemById.TryGetValue(parentId, out var parent))
            {
                if (_foldedEntryIds.Remove(parent.EntryId))
                {
                    changed = true;
                }

                parentId = parent.ParentEntryId;
            }

            if (changed)
            {
                NotifyFoldedEntryIdsChanged();
            }

            RefreshVisible(entryId, preferLastVisible: false);
            return true;
        }

        private void ClearFoldedEntryIds()
        {
            if (_foldedEntryIds.Count == 0)
            {
                return;
            }

            _foldedEntryIds.Clear();
            NotifyFoldedEntryIdsChanged();
        }

        private void NotifyFoldedEntryIdsChanged() =>
            _foldedEntryIdsChanged?.Invoke(SnapshotFoldedEntryIds(_foldedEntryIds));

        private string? CurrentEntryId() =>
            _visibleItems.Count == 0 ? null : _visibleItems[_selectedIndex].EntryId;

        private void Complete(string? selectedEntryId) =>
            Result = new CodingAgentTreeInteractiveNavigator.Result(
                selectedEntryId,
                _selectedIndex,
                _frames,
                SnapshotFoldedEntryIds(_foldedEntryIds),
                _pendingLabelEditEntryId);

        private static IReadOnlySet<string> SnapshotFoldedEntryIds(IEnumerable<string> foldedEntryIds) =>
            new HashSet<string>(foldedEntryIds, StringComparer.OrdinalIgnoreCase);

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
                (item.SearchText ?? item.DisplayLine).Contains(searchPattern, StringComparison.OrdinalIgnoreCase));
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

        private static int FindFilterIndex(CodingAgentTreeFilterMode targetMode)
        {
            var index = Array.IndexOf(FilterCycle, targetMode);
            return index >= 0 ? index : 0;
        }

        private static bool TryFindIndexById(
            IReadOnlyList<CodingAgentTreeViewItem> items,
            string? entryId,
            out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return false;
            }

            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].EntryId.Equals(entryId, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }

            return false;
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
                    item.HasLabel,
                _ => true
            };
        }

        private static string FormatEntryType(CodingAgentTreeViewItem item) =>
            string.IsNullOrWhiteSpace(item.EntryType) ? "entry" : item.EntryType;

        private string FormatDisplayLine(CodingAgentTreeViewItem item) =>
            (item.BaseDisplayLine ?? item.DisplayLine) +
            (_showLabelTimestamps ? item.LabelTimestampSuffix ?? string.Empty : string.Empty);

        private static string FormatSelectedFlags(CodingAgentTreeViewItem item)
        {
            if (item.IsCurrentLeaf)
            {
                return ", leaf";
            }

            return item.IsOnBranch ? ", branch" : string.Empty;
        }
    }
}
