using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed class TuiSettingItem
{
    public TuiSettingItem(
        string id,
        string label,
        string currentValue,
        string? description = null,
        IReadOnlyList<string>? values = null,
        Func<string, Action<string?>, ITuiInputComponent>? submenu = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Setting id is required.", nameof(id)) : id;
        Label = label ?? throw new ArgumentNullException(nameof(label));
        CurrentValue = currentValue ?? string.Empty;
        Description = description;
        Values = values?.ToArray() ?? [];
        Submenu = submenu;
    }

    public string Id { get; }
    public string Label { get; }
    public string CurrentValue { get; private set; }
    public string? Description { get; }
    public IReadOnlyList<string> Values { get; }
    public Func<string, Action<string?>, ITuiInputComponent>? Submenu { get; }

    public void SetCurrentValue(string value) => CurrentValue = value ?? string.Empty;
}

public sealed class TuiSettingsListTheme
{
    public static TuiSettingsListTheme Plain { get; } = new();

    public Func<string, bool, string> Label { get; init; } = static (text, _) => text;
    public Func<string, bool, string> Value { get; init; } = static (text, _) => text;
    public Func<string, string> Description { get; init; } = static text => text;
    public Func<string, string> Hint { get; init; } = static text => text;
    public string Cursor { get; init; } = "> ";
}

public sealed record TuiSettingsListOptions(bool EnableSearch = false);

public sealed class TuiSettingsList : ITuiInputComponent
{
    private readonly List<TuiSettingItem> _items;
    private readonly List<TuiSettingItem> _filteredItems;
    private readonly int _maxVisible;
    private readonly TuiSettingsListTheme _theme;
    private readonly bool _searchEnabled;
    private string _searchText = string.Empty;
    private int _selectedIndex;
    private ITuiInputComponent? _submenuComponent;
    private int? _submenuItemIndex;

    public TuiSettingsList(
        IEnumerable<TuiSettingItem> items,
        int maxVisible,
        TuiSettingsListTheme? theme = null,
        TuiSettingsListOptions? options = null)
    {
        _items = items?.ToList() ?? throw new ArgumentNullException(nameof(items));
        _filteredItems = [.. _items];
        _maxVisible = Math.Max(1, maxVisible);
        _theme = theme ?? TuiSettingsListTheme.Plain;
        _searchEnabled = options?.EnableSearch ?? false;
    }

    public event Action<string, string>? Changed;
    public event Action? Cancelled;

    public int SelectedIndex => _selectedIndex;
    public string SearchText => _searchText;
    public IReadOnlyList<TuiSettingItem> FilteredItems => _filteredItems;
    public TuiSettingItem? SelectedItem => _filteredItems.Count == 0 ? null : _filteredItems[_selectedIndex];
    public bool IsSubmenuOpen => _submenuComponent is not null;

    public void UpdateValue(string id, string newValue)
    {
        var item = _items.FirstOrDefault(candidate =>
            candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        item?.SetCurrentValue(newValue);
    }

    public void SetSearchText(string searchText)
    {
        if (!_searchEnabled)
        {
            return;
        }

        _searchText = searchText ?? string.Empty;
        ApplyFilter();
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

    public void Invalidate() => _submenuComponent?.Invalidate();

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        if (_submenuComponent is not null)
        {
            return _submenuComponent.Render(width);
        }

        var lines = new List<string>();
        if (_searchEnabled)
        {
            lines.Add(RenderSearchLine(width));
            lines.Add(string.Empty);
        }

        if (_items.Count == 0)
        {
            lines.Add(_theme.Hint(TuiText.TruncateToWidth("  No settings available", width, string.Empty)));
            AddHintLine(lines, width);
            return lines;
        }

        if (_filteredItems.Count == 0)
        {
            lines.Add(_theme.Hint(TuiText.TruncateToWidth("  No matching settings", width, string.Empty)));
            AddHintLine(lines, width);
            return lines;
        }

        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - (_maxVisible / 2), _filteredItems.Count - _maxVisible));
        var endIndex = Math.Min(startIndex + _maxVisible, _filteredItems.Count);
        var labelWidth = GetLabelColumnWidth();

        for (var i = startIndex; i < endIndex; i++)
        {
            lines.Add(RenderItem(_filteredItems[i], i == _selectedIndex, width, labelWidth));
        }

        if (startIndex > 0 || endIndex < _filteredItems.Count)
        {
            var scrollText = $"  ({_selectedIndex + 1}/{_filteredItems.Count})";
            lines.Add(_theme.Hint(TuiText.TruncateToWidth(scrollText, width - 2, string.Empty)));
        }

        if (SelectedItem is { Description: { Length: > 0 } description })
        {
            lines.Add(string.Empty);
            foreach (var line in TuiText.Wrap(description, Math.Max(1, width - 4)))
            {
                lines.Add(_theme.Description(TuiText.TruncateToWidth("  " + line, width, string.Empty)));
            }
        }

        AddHintLine(lines, width);
        return lines;
    }

    public TuiInputResult HandleInput(ConsoleKeyInfo key)
    {
        if (_submenuComponent is not null)
        {
            _submenuComponent.HandleInput(key);
            return TuiInputResult.Handled;
        }

        if (IsMoveUp(key))
        {
            MoveBy(-1);
            return TuiInputResult.Handled;
        }

        if (IsMoveDown(key))
        {
            MoveBy(1);
            return TuiInputResult.Handled;
        }

        if (IsActivate(key))
        {
            ActivateSelectedItem();
            return TuiInputResult.Handled;
        }

        if (IsCancel(key))
        {
            Cancelled?.Invoke();
            return TuiInputResult.Handled;
        }

        if (_searchEnabled)
        {
            if (key.Key == ConsoleKey.Backspace && _searchText.Length > 0)
            {
                SetSearchText(_searchText[..^1]);
                return TuiInputResult.Handled;
            }

            if (IsPrintableSearchInput(key))
            {
                SetSearchText(_searchText + key.KeyChar);
                return TuiInputResult.Handled;
            }
        }

        return TuiInputResult.Ignored;
    }

    private void ActivateSelectedItem()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        if (item.Submenu is not null)
        {
            _submenuItemIndex = _selectedIndex;
            _submenuComponent = item.Submenu(item.CurrentValue, selectedValue =>
            {
                if (selectedValue is not null)
                {
                    item.SetCurrentValue(selectedValue);
                    Changed?.Invoke(item.Id, selectedValue);
                }

                CloseSubmenu();
            });
            return;
        }

        if (item.Values.Count == 0)
        {
            return;
        }

        var currentIndex = -1;
        for (var i = 0; i < item.Values.Count; i++)
        {
            if (item.Values[i].Equals(item.CurrentValue, StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = i;
                break;
            }
        }

        var nextIndex = (currentIndex + 1) % item.Values.Count;
        var nextValue = item.Values[nextIndex];
        item.SetCurrentValue(nextValue);
        Changed?.Invoke(item.Id, nextValue);
    }

    private void CloseSubmenu()
    {
        _submenuComponent = null;
        if (_submenuItemIndex is { } index)
        {
            SetSelectedIndex(index);
            _submenuItemIndex = null;
        }
    }

    private void ApplyFilter()
    {
        _filteredItems.Clear();
        _filteredItems.AddRange(_searchText.Length == 0
            ? _items
            : TuiFuzzyMatcher.Filter(_items, _searchText, static item => item.Label));
        _selectedIndex = 0;
    }

    private void MoveBy(int delta)
    {
        if (_filteredItems.Count == 0)
        {
            return;
        }

        var next = _selectedIndex + delta;
        next = ((next % _filteredItems.Count) + _filteredItems.Count) % _filteredItems.Count;
        _selectedIndex = next;
    }

    private string RenderSearchLine(int width)
    {
        var search = "> " + _searchText;
        return TuiText.TruncateToWidth(search, width, string.Empty, pad: true);
    }

    private string RenderItem(TuiSettingItem item, bool selected, int width, int labelWidth)
    {
        var prefix = selected ? _theme.Cursor : "  ";
        var prefixWidth = TuiText.VisibleWidth(prefix);
        var labelPadded = item.Label + new string(' ', Math.Max(0, labelWidth - TuiText.VisibleWidth(item.Label)));
        var labelText = _theme.Label(labelPadded, selected);
        const string separator = "  ";
        var usedWidth = prefixWidth + labelWidth + TuiText.VisibleWidth(separator);
        var valueMaxWidth = Math.Max(0, width - usedWidth - 2);
        var value = TuiText.TruncateToWidth(item.CurrentValue, valueMaxWidth, string.Empty);
        var valueText = _theme.Value(value, selected);

        return TuiText.TruncateToWidth(prefix + labelText + separator + valueText, width, string.Empty);
    }

    private int GetLabelColumnWidth()
    {
        if (_items.Count == 0)
        {
            return 1;
        }

        return Math.Min(30, Math.Max(1, _items.Max(item => TuiText.VisibleWidth(item.Label))));
    }

    private void AddHintLine(List<string> lines, int width)
    {
        lines.Add(string.Empty);
        var hint = _searchEnabled
            ? "  Type to search | Enter/Space to change | Esc to cancel"
            : "  Enter/Space to change | Esc to cancel";
        lines.Add(_theme.Hint(TuiText.TruncateToWidth(hint, width, string.Empty)));
    }

    private static bool IsMoveUp(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.UpArrow || key.KeyChar is 'k' or 'K';

    private static bool IsMoveDown(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.DownArrow || key.KeyChar is 'j' or 'J';

    private static bool IsActivate(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar;

    private static bool IsCancel(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Escape || ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.C);

    private static bool IsPrintableSearchInput(ConsoleKeyInfo key) =>
        key.Key != ConsoleKey.Spacebar &&
        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) == 0 &&
        key.KeyChar != '\0' &&
        !char.IsControl(key.KeyChar);
}
