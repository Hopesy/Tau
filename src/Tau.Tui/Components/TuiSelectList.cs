using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.Tui.Components;

public sealed record TuiSelectItem(string Value, string Label, string? Description = null);

public sealed record TuiSelectListLayout(
    int MinPrimaryColumnWidth = 32,
    int MaxPrimaryColumnWidth = 32,
    string? FooterHint = null);

public sealed class TuiSelectListTheme
{
    public static TuiSelectListTheme Plain { get; } = new();

    public Func<string, string> SelectedText { get; init; } = static text => text;
    public Func<string, string> Description { get; init; } = static text => text;
    public Func<string, string> ScrollInfo { get; init; } = static text => text;
    public Func<string, string> NoMatch { get; init; } = static text => text;
    public Func<string, string> FooterHint { get; init; } = static text => text;
}

public sealed class TuiSelectList : ITuiInputComponent
{
    private const int PrimaryColumnGap = 2;
    private const int MinDescriptionWidth = 10;

    private readonly List<TuiSelectItem> _items;
    private readonly List<TuiSelectItem> _filteredItems;
    private readonly int _maxVisible;
    private readonly TuiSelectListTheme _theme;
    private readonly TuiSelectListLayout _layout;
    private string _filter = string.Empty;
    private int _selectedIndex;

    public TuiSelectList(
        IEnumerable<TuiSelectItem> items,
        int maxVisible = 5,
        TuiSelectListTheme? theme = null,
        TuiSelectListLayout? layout = null)
    {
        _items = items?.ToList() ?? throw new ArgumentNullException(nameof(items));
        _filteredItems = [.. _items];
        _maxVisible = Math.Max(1, maxVisible);
        _theme = theme ?? TuiSelectListTheme.Plain;
        _layout = layout ?? new TuiSelectListLayout();
    }

    public event Action<TuiSelectItem>? Selected;
    public event Action? Cancelled;
    public event Action<TuiSelectItem>? SelectionChanged;

    public int SelectedIndex => _selectedIndex;
    public IReadOnlyList<TuiSelectItem> FilteredItems => _filteredItems;
    public TuiSelectItem? SelectedItem => _filteredItems.Count == 0 ? null : _filteredItems[_selectedIndex];

    public void SetFilter(string filter)
    {
        _filter = filter ?? string.Empty;
        _filteredItems.Clear();
        _filteredItems.AddRange(_items.Where(MatchesFilter));
        _selectedIndex = 0;
        NotifySelectionChanged();
    }

    public void SetSelectedIndex(int index)
    {
        if (_filteredItems.Count == 0)
        {
            _selectedIndex = 0;
            return;
        }

        var next = Math.Clamp(index, 0, _filteredItems.Count - 1);
        if (next == _selectedIndex)
        {
            return;
        }

        _selectedIndex = next;
        NotifySelectionChanged();
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
            lines.Add(_theme.NoMatch(TuiText.TruncateToWidth("  No matching items", width, string.Empty)));
            AddFooterHint(lines, width);
            return lines;
        }

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
            lines.Add(_theme.ScrollInfo(TuiText.TruncateToWidth(scrollText, Math.Max(1, width), string.Empty)));
        }

        AddFooterHint(lines, width);
        return lines;
    }

    public TuiInputResult HandleInput(ConsoleKeyInfo key)
    {
        if (IsCancel(key))
        {
            Cancelled?.Invoke();
            return TuiInputResult.Handled;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            if (SelectedItem is { } selected)
            {
                Selected?.Invoke(selected);
            }

            return TuiInputResult.Handled;
        }

        if (_filteredItems.Count == 0)
        {
            return TuiInputResult.Ignored;
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

        return TuiInputResult.Ignored;
    }

    private bool MatchesFilter(TuiSelectItem item)
    {
        if (_filter.Length == 0)
        {
            return true;
        }

        return item.Value.StartsWith(_filter, StringComparison.OrdinalIgnoreCase)
            || item.Label.StartsWith(_filter, StringComparison.OrdinalIgnoreCase);
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

    private string RenderItem(TuiSelectItem item, bool selected, int width, int primaryColumnWidth)
    {
        var prefix = selected ? "> " : "  ";
        var prefixWidth = TuiText.VisibleWidth(prefix);
        var description = item.Description is null ? null : TuiText.NormalizeSingleLine(item.Description);

        if (!string.IsNullOrEmpty(description) && width > 40)
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
                if (selected)
                {
                    return _theme.SelectedText(prefix + primary + spacing + truncatedDescription);
                }

                return prefix + primary + _theme.Description(spacing + truncatedDescription);
            }
        }

        var maxWidth = Math.Max(1, width - prefixWidth - 2);
        var value = TuiText.TruncateToWidth(DisplayValue(item), maxWidth, string.Empty);
        return selected ? _theme.SelectedText(prefix + value) : prefix + value;
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

    private void AddFooterHint(List<string> lines, int width)
    {
        var footer = TuiText.NormalizeSingleLine(_layout.FooterHint);
        if (footer.Length == 0)
        {
            return;
        }

        lines.Add(_theme.FooterHint(TuiText.TruncateToWidth(footer, Math.Max(1, width), string.Empty)));
    }

    private void NotifySelectionChanged()
    {
        if (SelectedItem is { } item)
        {
            SelectionChanged?.Invoke(item);
        }
    }

    private static string DisplayValue(TuiSelectItem item) =>
        string.IsNullOrEmpty(item.Label) ? item.Value : item.Label;

    private static bool IsMoveUp(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.UpArrow || key.KeyChar is 'k' or 'K';

    private static bool IsMoveDown(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.DownArrow || key.KeyChar is 'j' or 'J';

    private static bool IsCancel(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Escape || ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.C);
}
