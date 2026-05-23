using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed record TuiMultiSelectItem(
    string Value,
    string Label,
    string? Description = null,
    string? Group = null);

public sealed record TuiMultiSelectListLayout(
    int MinPrimaryColumnWidth = 36,
    int MaxPrimaryColumnWidth = 44);

public sealed class TuiMultiSelectListTheme
{
    public static TuiMultiSelectListTheme Plain { get; } = new();

    public Func<string, string> SelectedText { get; init; } = static text => text;
    public Func<string, string> Description { get; init; } = static text => text;
    public Func<string, string> Hint { get; init; } = static text => text;
    public Func<string, string> ScrollInfo { get; init; } = static text => text;
    public Func<string, string> NoMatch { get; init; } = static text => text;
}

public sealed class TuiMultiSelectList : ITuiInputComponent
{
    private const int PrimaryColumnGap = 2;
    private const int MinDescriptionWidth = 10;

    private readonly List<TuiMultiSelectItem> _items;
    private readonly Dictionary<string, TuiMultiSelectItem> _itemsByValue;
    private readonly List<TuiMultiSelectItem> _filteredItems = [];
    private readonly int _maxVisible;
    private readonly TuiMultiSelectListTheme _theme;
    private readonly TuiMultiSelectListLayout _layout;
    private readonly string[] _allValues;
    private IReadOnlyList<string>? _selectedValues;
    private string _filter = string.Empty;
    private int _selectedIndex;
    private bool _isDirty;

    public TuiMultiSelectList(
        IEnumerable<TuiMultiSelectItem> items,
        IReadOnlyList<string>? selectedValues = null,
        int maxVisible = 10,
        TuiMultiSelectListTheme? theme = null,
        TuiMultiSelectListLayout? layout = null)
    {
        _items = items?.ToList() ?? throw new ArgumentNullException(nameof(items));
        _itemsByValue = _items
            .GroupBy(static item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        _allValues = _items.Select(static item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _selectedValues = NormalizeSelectedValues(selectedValues);
        _maxVisible = Math.Max(1, maxVisible);
        _theme = theme ?? TuiMultiSelectListTheme.Plain;
        _layout = layout ?? new TuiMultiSelectListLayout();
        RefreshFilteredItems(keepSelectedValue: null);
    }

    public event Action<IReadOnlyList<string>?>? Saved;
    public event Action? Cancelled;
    public event Action<IReadOnlyList<string>?>? SelectionChanged;

    public int SelectedIndex => _selectedIndex;
    public string Filter => _filter;
    public bool IsDirty => _isDirty;
    public IReadOnlyList<TuiMultiSelectItem> FilteredItems => _filteredItems;
    public IReadOnlyList<string>? SelectedValues => _selectedValues;
    public TuiMultiSelectItem? SelectedItem => _filteredItems.Count == 0 ? null : _filteredItems[_selectedIndex];

    public void SetFilter(string filter)
    {
        _filter = filter ?? string.Empty;
        RefreshFilteredItems(keepSelectedValue: null);
    }

    public void SetSelectedIndex(int index)
    {
        if (_filteredItems.Count == 0)
        {
            _selectedIndex = 0;
            return;
        }

        _selectedIndex = Math.Clamp(index, 0, _filteredItems.Count - 1);
    }

    public void SetSelectedValue(string value)
    {
        var index = _filteredItems.FindIndex(item => item.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _selectedIndex = index;
        }
    }

    public void Invalidate()
    {
    }

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();

        if (_filteredItems.Count == 0)
        {
            lines.Add(_theme.NoMatch(TuiText.TruncateToWidth("  No matching models", width, string.Empty)));
        }
        else
        {
            var primaryColumnWidth = GetPrimaryColumnWidth();
            var startIndex = Math.Max(
                0,
                Math.Min(_selectedIndex - (_maxVisible / 2), _filteredItems.Count - _maxVisible));
            var endIndex = Math.Min(startIndex + _maxVisible, _filteredItems.Count);

            for (var i = startIndex; i < endIndex; i++)
            {
                var item = _filteredItems[i];
                lines.Add(RenderItem(item, i == _selectedIndex, width, primaryColumnWidth));
            }

            if (startIndex > 0 || endIndex < _filteredItems.Count)
            {
                var scrollText = $"  ({_selectedIndex + 1}/{_filteredItems.Count})";
                lines.Add(_theme.ScrollInfo(TuiText.TruncateToWidth(scrollText, width, string.Empty)));
            }
        }

        if (!string.IsNullOrWhiteSpace(_filter))
        {
            lines.Add(_theme.Hint(TuiText.TruncateToWidth($"  filter: {_filter}", width, string.Empty)));
        }

        lines.Add(_theme.Hint(TuiText.TruncateToWidth($"  {FormatFooter()}", width, string.Empty)));
        return lines;
    }

    public TuiInputResult HandleInput(ConsoleKeyInfo key)
    {
        if (IsSave(key))
        {
            Saved?.Invoke(CloneSelection(_selectedValues));
            _isDirty = false;
            return TuiInputResult.Handled;
        }

        if (IsCancel(key))
        {
            if (IsControlC(key) && _filter.Length > 0)
            {
                SetFilter(string.Empty);
                return TuiInputResult.Handled;
            }

            Cancelled?.Invoke();
            return TuiInputResult.Handled;
        }

        if (key.Key == ConsoleKey.Backspace && _filter.Length > 0)
        {
            SetFilter(_filter[..^1]);
            return TuiInputResult.Handled;
        }

        if (_filteredItems.Count > 0)
        {
            if (IsReorderUp(key))
            {
                ReorderSelected(-1);
                return TuiInputResult.Handled;
            }

            if (IsReorderDown(key))
            {
                ReorderSelected(1);
                return TuiInputResult.Handled;
            }

            if (IsMoveUp(key))
            {
                MoveBy(-1, wrap: true);
                return TuiInputResult.Handled;
            }

            if (IsMoveDown(key))
            {
                MoveBy(1, wrap: true);
                return TuiInputResult.Handled;
            }

            if (key.Key == ConsoleKey.Home)
            {
                SetSelectedIndex(0);
                return TuiInputResult.Handled;
            }

            if (key.Key == ConsoleKey.End)
            {
                SetSelectedIndex(_filteredItems.Count - 1);
                return TuiInputResult.Handled;
            }

            if (key.Key == ConsoleKey.PageUp)
            {
                MoveBy(-_maxVisible, wrap: false);
                return TuiInputResult.Handled;
            }

            if (key.Key == ConsoleKey.PageDown)
            {
                MoveBy(_maxVisible, wrap: false);
                return TuiInputResult.Handled;
            }

            if (IsToggle(key))
            {
                ToggleSelected();
                return TuiInputResult.Handled;
            }

            if (IsToggleGroup(key))
            {
                ToggleSelectedGroup();
                return TuiInputResult.Handled;
            }
        }

        if (IsEnableAll(key))
        {
            EnableTargets(GetTargetValuesForBulkAction());
            return TuiInputResult.Handled;
        }

        if (IsClearAll(key))
        {
            ClearTargets(GetTargetValuesForBulkAction());
            return TuiInputResult.Handled;
        }

        if (IsPrintableFilterInput(key))
        {
            SetFilter(_filter + key.KeyChar);
            return TuiInputResult.Handled;
        }

        return TuiInputResult.Ignored;
    }

    private void ToggleSelected()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var keepSelected = item.Value;
        if (_selectedValues is null)
        {
            _selectedValues = [item.Value];
        }
        else if (_selectedValues.Contains(item.Value, StringComparer.OrdinalIgnoreCase))
        {
            _selectedValues = _selectedValues
                .Where(value => !value.Equals(item.Value, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            _selectedValues = [.. _selectedValues, item.Value];
        }

        MarkSelectionChanged(keepSelected);
    }

    private void ToggleSelectedGroup()
    {
        if (SelectedItem is not { } item || string.IsNullOrWhiteSpace(item.Group))
        {
            return;
        }

        var groupValues = _items
            .Where(candidate => string.Equals(candidate.Group, item.Group, StringComparison.OrdinalIgnoreCase))
            .Select(static candidate => candidate.Value)
            .ToArray();
        if (groupValues.Length == 0)
        {
            return;
        }

        if (groupValues.All(IsValueSelected))
        {
            ClearTargets(groupValues, keepSelectedValue: item.Value);
        }
        else
        {
            EnableTargets(groupValues, keepSelectedValue: item.Value);
        }
    }

    private void EnableTargets(IReadOnlyList<string> targetValues, string? keepSelectedValue = null)
    {
        if (targetValues.Count == 0)
        {
            return;
        }

        if (_selectedValues is null)
        {
            return;
        }

        var next = _selectedValues.ToList();
        foreach (var target in targetValues)
        {
            if (!next.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                next.Add(target);
            }
        }

        _selectedValues = next.Count == _allValues.Length ? null : next.ToArray();
        MarkSelectionChanged(keepSelectedValue ?? SelectedItem?.Value);
    }

    private void ClearTargets(IReadOnlyList<string> targetValues, string? keepSelectedValue = null)
    {
        if (targetValues.Count == 0)
        {
            return;
        }

        _selectedValues = _selectedValues is null
            ? _allValues.Where(value => !targetValues.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray()
            : _selectedValues
                .Where(value => !targetValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        MarkSelectionChanged(keepSelectedValue ?? SelectedItem?.Value);
    }

    private void ReorderSelected(int delta)
    {
        if (_selectedValues is null || SelectedItem is not { } item)
        {
            return;
        }

        var list = _selectedValues.ToList();
        var current = list.FindIndex(value => value.Equals(item.Value, StringComparison.OrdinalIgnoreCase));
        if (current < 0)
        {
            return;
        }

        var next = current + delta;
        if (next < 0 || next >= list.Count)
        {
            return;
        }

        (list[current], list[next]) = (list[next], list[current]);
        _selectedValues = list.ToArray();
        MarkSelectionChanged(item.Value);
    }

    private IReadOnlyList<string> GetTargetValuesForBulkAction() =>
        _filter.Length == 0 ? _allValues : _filteredItems.Select(static item => item.Value).ToArray();

    private void MarkSelectionChanged(string? keepSelectedValue)
    {
        _selectedValues = NormalizeSelectedValues(_selectedValues);
        _isDirty = true;
        RefreshFilteredItems(keepSelectedValue);
        SelectionChanged?.Invoke(CloneSelection(_selectedValues));
    }

    private void RefreshFilteredItems(string? keepSelectedValue)
    {
        var orderedItems = BuildDisplayOrder();
        _filteredItems.Clear();
        _filteredItems.AddRange(orderedItems.Where(MatchesFilter));

        if (_filteredItems.Count == 0)
        {
            _selectedIndex = 0;
            return;
        }

        if (!string.IsNullOrWhiteSpace(keepSelectedValue))
        {
            var selected = _filteredItems.FindIndex(item =>
                item.Value.Equals(keepSelectedValue, StringComparison.OrdinalIgnoreCase));
            if (selected >= 0)
            {
                _selectedIndex = selected;
                return;
            }
        }

        _selectedIndex = Math.Clamp(_selectedIndex, 0, _filteredItems.Count - 1);
    }

    private IReadOnlyList<TuiMultiSelectItem> BuildDisplayOrder()
    {
        if (_selectedValues is null)
        {
            return _items;
        }

        var ordered = new List<TuiMultiSelectItem>();
        foreach (var value in _selectedValues)
        {
            if (_itemsByValue.TryGetValue(value, out var item))
            {
                ordered.Add(item);
            }
        }

        var selectedSet = ordered.Select(static item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(_items.Where(item => !selectedSet.Contains(item.Value)));
        return ordered;
    }

    private bool MatchesFilter(TuiMultiSelectItem item)
    {
        if (_filter.Length == 0)
        {
            return true;
        }

        return item.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || item.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || (item.Description?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Group?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void MoveBy(int delta, bool wrap)
    {
        if (_filteredItems.Count == 0)
        {
            return;
        }

        var next = _selectedIndex + delta;
        if (wrap)
        {
            next = ((next % _filteredItems.Count) + _filteredItems.Count) % _filteredItems.Count;
        }
        else
        {
            next = Math.Clamp(next, 0, _filteredItems.Count - 1);
        }

        SetSelectedIndex(next);
    }

    private string RenderItem(TuiMultiSelectItem item, bool focused, int width, int primaryColumnWidth)
    {
        var cursor = focused ? "> " : "  ";
        var check = IsValueSelected(item.Value) ? "[x] " : "[ ] ";
        var prefix = cursor + check;
        var prefixWidth = TuiText.VisibleWidth(prefix);
        var description = item.Description is null ? null : TuiText.NormalizeSingleLine(item.Description);

        if (!string.IsNullOrEmpty(description) && width > 48)
        {
            var effectivePrimaryColumnWidth = Math.Max(1, Math.Min(primaryColumnWidth, width - prefixWidth - 4));
            var maxPrimaryWidth = Math.Max(1, effectivePrimaryColumnWidth - PrimaryColumnGap);
            var primary = TuiText.TruncateToWidth(DisplayValue(item), maxPrimaryWidth, string.Empty);
            var primaryWidth = TuiText.VisibleWidth(primary);
            var spacing = new string(' ', Math.Max(1, effectivePrimaryColumnWidth - primaryWidth));
            var descriptionStart = prefixWidth + primaryWidth + spacing.Length;
            var remainingWidth = width - descriptionStart - 2;

            if (remainingWidth > MinDescriptionWidth)
            {
                var truncatedDescription = TuiText.TruncateToWidth(description, remainingWidth, string.Empty);
                if (focused)
                {
                    return _theme.SelectedText(prefix + primary + spacing + truncatedDescription);
                }

                return prefix + primary + _theme.Description(spacing + truncatedDescription);
            }
        }

        var maxWidth = Math.Max(1, width - prefixWidth - 2);
        var value = TuiText.TruncateToWidth(DisplayValue(item), maxWidth, string.Empty);
        return focused ? _theme.SelectedText(prefix + value) : prefix + value;
    }

    private int GetPrimaryColumnWidth()
    {
        var min = Math.Max(1, Math.Min(_layout.MinPrimaryColumnWidth, _layout.MaxPrimaryColumnWidth));
        var max = Math.Max(1, Math.Max(_layout.MinPrimaryColumnWidth, _layout.MaxPrimaryColumnWidth));
        var widest = _filteredItems.Count == 0
            ? min
            : _filteredItems.Max(item => TuiText.VisibleWidth(DisplayValue(item)) + PrimaryColumnGap);
        return Math.Clamp(widest, min, max);
    }

    private string FormatFooter()
    {
        var enabledCount = _selectedValues?.Count ?? _allValues.Length;
        var countText = _selectedValues is null
            ? $"all enabled ({_allValues.Length}/{_allValues.Length})"
            : $"{enabledCount}/{_allValues.Length} enabled";
        var unsaved = _isDirty ? " (unsaved)" : string.Empty;
        return $"Enter/Space toggle | Ctrl+A all | Ctrl+X clear | Ctrl+P provider | Alt+Up/Down reorder | Ctrl+S save | Esc cancel | {countText}{unsaved}";
    }

    private bool IsValueSelected(string value) =>
        _selectedValues is null || _selectedValues.Contains(value, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string>? NormalizeSelectedValues(IEnumerable<string>? selectedValues)
    {
        if (selectedValues is null)
        {
            return null;
        }

        var normalized = selectedValues
            .Where(value => _itemsByValue.ContainsKey(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == _allValues.Length ? null : normalized;
    }

    private static IReadOnlyList<string>? CloneSelection(IReadOnlyList<string>? selectedValues) =>
        selectedValues is null ? null : selectedValues.ToArray();

    private static string DisplayValue(TuiMultiSelectItem item) =>
        string.IsNullOrEmpty(item.Label) ? item.Value : item.Label;

    private static bool IsMoveUp(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.UpArrow || key.KeyChar is 'k' or 'K';

    private static bool IsMoveDown(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.DownArrow || key.KeyChar is 'j' or 'J';

    private static bool IsToggle(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar;

    private static bool IsSave(ConsoleKeyInfo key) =>
        HasControl(key) && key.Key == ConsoleKey.S;

    private static bool IsEnableAll(ConsoleKeyInfo key) =>
        HasControl(key) && key.Key == ConsoleKey.A;

    private static bool IsClearAll(ConsoleKeyInfo key) =>
        HasControl(key) && key.Key == ConsoleKey.X;

    private static bool IsToggleGroup(ConsoleKeyInfo key) =>
        HasControl(key) && key.Key == ConsoleKey.P;

    private static bool IsReorderUp(ConsoleKeyInfo key) =>
        HasAlt(key) && key.Key == ConsoleKey.UpArrow;

    private static bool IsReorderDown(ConsoleKeyInfo key) =>
        HasAlt(key) && key.Key == ConsoleKey.DownArrow;

    private static bool IsCancel(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Escape || IsControlC(key);

    private static bool IsControlC(ConsoleKeyInfo key) =>
        HasControl(key) && key.Key == ConsoleKey.C;

    private static bool HasControl(ConsoleKeyInfo key) =>
        (key.Modifiers & ConsoleModifiers.Control) != 0;

    private static bool HasAlt(ConsoleKeyInfo key) =>
        (key.Modifiers & ConsoleModifiers.Alt) != 0;

    private static bool IsPrintableFilterInput(ConsoleKeyInfo key) =>
        key.Key != ConsoleKey.Spacebar &&
        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) == 0 &&
        key.KeyChar != '\0' &&
        !char.IsControl(key.KeyChar);
}
