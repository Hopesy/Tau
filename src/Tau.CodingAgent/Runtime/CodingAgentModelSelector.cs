using Tau.Ai;
using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentModelSelectorState(
    IReadOnlyList<Model> AvailableModels,
    IReadOnlyList<Model>? ScopedModels,
    Model CurrentModel,
    string? InitialFilter);

public enum CodingAgentModelSelectorScope
{
    All,
    Scoped
}

public sealed class CodingAgentModelSelectorComponent : ITuiInputComponent
{
    private readonly CodingAgentModelSelectorState _state;
    private readonly int _maxVisible;
    private string _filter;
    private TuiSelectList _selector;
    private CodingAgentModelSelectorScope _scope;

    public CodingAgentModelSelectorComponent(
        CodingAgentModelSelectorState state,
        int maxVisible = 10)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _maxVisible = Math.Max(1, maxVisible);
        _filter = string.IsNullOrWhiteSpace(state.InitialFilter)
            ? string.Empty
            : state.InitialFilter.Trim();
        _scope = HasScopedModels ? CodingAgentModelSelectorScope.Scoped : CodingAgentModelSelectorScope.All;
        _selector = CreateSelectList(includeFooterHint: false);
    }

    public event Action<TuiSelectItem>? Selected;
    public event Action? Cancelled;

    public CodingAgentModelSelectorScope Scope => _scope;
    public string Filter => _filter;
    public bool HasScopedModels => _state.ScopedModels is { Count: > 0 };
    public IReadOnlyList<TuiSelectItem> FilteredItems => _selector.FilteredItems;
    public TuiSelectItem? SelectedItem => _selector.SelectedItem;

    public void Invalidate()
    {
        _selector.Invalidate();
    }

    public IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        lines.Add(RenderRule("Model Selector", width));
        lines.Add(TuiText.TruncateToWidth($"Search: {_filter}", width, string.Empty));

        if (HasScopedModels)
        {
            var scopeText = _scope == CodingAgentModelSelectorScope.Scoped
                ? "Scope: all | [scoped]"
                : "Scope: [all] | scoped";
            lines.Add(TuiText.TruncateToWidth(scopeText, width, string.Empty));
            lines.Add(TuiText.TruncateToWidth("Tab: switch scope (all/scoped)", width, string.Empty));
        }

        lines.AddRange(_selector.Render(width));
        if (ResolveSelectedModel() is { } selected)
        {
            var name = string.IsNullOrWhiteSpace(selected.Name)
                ? $"{selected.Provider}/{selected.Id}"
                : selected.Name;
            lines.Add(TuiText.TruncateToWidth($"  Model Name: {name}", width, string.Empty));
        }

        lines.Add(TuiText.TruncateToWidth(CodingAgentModelSelector.AuthFilteringFooterHint, width, string.Empty));
        lines.Add(RenderRule(string.Empty, width));
        return lines;
    }

    public TuiInputResult HandleInput(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Tab && HasScopedModels)
        {
            _scope = _scope == CodingAgentModelSelectorScope.Scoped
                ? CodingAgentModelSelectorScope.All
                : CodingAgentModelSelectorScope.Scoped;
            _selector = CreateSelectList(includeFooterHint: false);
            return TuiInputResult.Handled;
        }

        var selectorResult = _selector.HandleInput(key);
        if (selectorResult.Consumed)
        {
            return selectorResult;
        }

        if (key.Key == ConsoleKey.Backspace && _filter.Length > 0)
        {
            _filter = _filter[..^1];
            _selector.SetFilter(_filter);
            return TuiInputResult.Handled;
        }

        if (IsPrintableSearchInput(key))
        {
            _filter += key.KeyChar;
            _selector.SetFilter(_filter);
            return TuiInputResult.Handled;
        }

        return TuiInputResult.Ignored;
    }

    private TuiSelectList CreateSelectList(bool includeFooterHint)
    {
        var selector = CodingAgentModelSelector.CreateSelectList(
            _state with
            {
                ScopedModels = _scope == CodingAgentModelSelectorScope.Scoped
                    ? _state.ScopedModels
                    : null,
                InitialFilter = _filter
            },
            _maxVisible,
            includeFooterHint);
        selector.Selected += item => Selected?.Invoke(item);
        selector.Cancelled += () => Cancelled?.Invoke();
        return selector;
    }

    private Model? ResolveSelectedModel()
    {
        var selected = _selector.SelectedItem;
        if (selected is null)
        {
            return null;
        }

        return GetActiveModels().FirstOrDefault(model =>
            CodingAgentModelSelector.FormatModelId(model).Equals(selected.Value, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<Model> GetActiveModels() =>
        _scope == CodingAgentModelSelectorScope.Scoped && HasScopedModels
            ? _state.ScopedModels!
            : _state.AvailableModels;

    private static bool IsPrintableSearchInput(ConsoleKeyInfo key) =>
        (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) == 0 &&
        key.KeyChar != '\0' &&
        !char.IsControl(key.KeyChar);

    private static string RenderRule(string title, int width)
    {
        if (width <= 1)
        {
            return "-";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return new string('-', width);
        }

        var text = $" {title.Trim()} ";
        var textWidth = TuiText.VisibleWidth(text);
        if (textWidth >= width)
        {
            return TuiText.TruncateToWidth(title, width, string.Empty);
        }

        var remaining = width - textWidth;
        var left = remaining / 2;
        var right = remaining - left;
        return new string('-', left) + text + new string('-', right);
    }
}

public static class CodingAgentModelSelector
{
    public const string AuthFilteringFooterHint = "Only showing models with configured auth";

    public static Func<CodingAgentModelSelectorState, CancellationToken, Task<string?>> CreateConsoleSelector(
        IConsoleKeyReader keyReader,
        bool synchronizedOutput = true)
    {
        ArgumentNullException.ThrowIfNull(keyReader);

        return (state, cancellationToken) => SelectAsync(
            state,
            keyReader,
            TuiAnsiRenderSurface.ForConsole(synchronizedOutput),
            cancellationToken);
    }

    public static Func<CodingAgentModelSelectorState, CancellationToken, Task<string?>> CreateCompositionSelector(
        TuiCompositionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return async (state, cancellationToken) =>
        {
            var selector = CreateComponent(state);
            if (selector.FilteredItems.Count == 0 && !selector.HasScopedModels)
            {
                return null;
            }

            string? selected = null;
            var cancelled = false;
            void OnSelected(TuiSelectItem item) => selected = item.Value;
            void OnCancelled() => cancelled = true;

            selector.Selected += OnSelected;
            selector.Cancelled += OnCancelled;
            var width = Math.Max(1, Math.Min(96, session.Viewport.Width));
            var handle = session.OpenOverlay(
                selector,
                new TuiTranscriptOverlayOptions(
                    Width: width,
                    Row: Math.Max(0, Math.Min(1, session.Viewport.MessageHeight - 1)),
                    Column: Math.Max(0, (session.Viewport.Width - width) / 2)));
            try
            {
                if (!session.IsStarted)
                {
                    session.Start();
                }
                else
                {
                    session.Render(force: true);
                }

                while (selected is null && !cancelled)
                {
                    await session.ReadInputAsync(cancellationToken).ConfigureAwait(false);
                }

                return cancelled ? null : selected;
            }
            finally
            {
                session.CloseOverlay(handle);
                selector.Selected -= OnSelected;
                selector.Cancelled -= OnCancelled;
            }
        };
    }

    public static TuiSelectList CreateSelectList(
        CodingAgentModelSelectorState state,
        int maxVisible = 10) =>
        CreateSelectList(state, maxVisible, includeFooterHint: true);

    public static CodingAgentModelSelectorComponent CreateComponent(
        CodingAgentModelSelectorState state,
        int maxVisible = 10) =>
        new(state, maxVisible);

    internal static TuiSelectList CreateSelectList(
        CodingAgentModelSelectorState state,
        int maxVisible,
        bool includeFooterHint)
    {
        ArgumentNullException.ThrowIfNull(state);

        var source = state.ScopedModels is { Count: > 0 }
            ? state.ScopedModels
            : state.AvailableModels;
        var items = source
            .Select(static model => new TuiSelectItem(
                FormatModelId(model),
                model.Id,
                string.IsNullOrWhiteSpace(model.Name)
                    ? model.Provider
                    : $"{model.Provider} | {model.Name}"))
            .ToArray();
        var selector = new TuiSelectList(
            items,
            maxVisible: maxVisible,
            layout: new TuiSelectListLayout(
                MinPrimaryColumnWidth: 26,
                MaxPrimaryColumnWidth: 44,
                FooterHint: includeFooterHint ? AuthFilteringFooterHint : null));

        if (!string.IsNullOrWhiteSpace(state.InitialFilter))
        {
            selector.SetFilter(state.InitialFilter.Trim());
        }

        var current = FormatModelId(state.CurrentModel);
        var index = selector.FilteredItems
            .Select((item, itemIndex) => (item, itemIndex))
            .FirstOrDefault(pair => pair.item.Value.Equals(current, StringComparison.OrdinalIgnoreCase))
            .itemIndex;
        if (selector.FilteredItems.Count > 0 &&
            selector.FilteredItems[index].Value.Equals(current, StringComparison.OrdinalIgnoreCase))
        {
            selector.SetSelectedIndex(index);
        }

        return selector;
    }

    public static async Task<string?> SelectAsync(
        CodingAgentModelSelectorState state,
        IConsoleKeyReader keyReader,
        ITuiRenderSurface surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyReader);
        ArgumentNullException.ThrowIfNull(surface);

        var selector = CreateComponent(state);
        if (selector.FilteredItems.Count == 0 && !selector.HasScopedModels)
        {
            return null;
        }

        string? selected = null;
        var cancelled = false;
        void OnSelected(TuiSelectItem item) => selected = item.Value;
        void OnCancelled() => cancelled = true;

        selector.Selected += OnSelected;
        selector.Cancelled += OnCancelled;
        try
        {
            var host = new TuiOverlayHost(selector, keyReader, surface);
            host.Render(force: true);
            while (selected is null && !cancelled)
            {
                await host.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            }

            return cancelled ? null : selected;
        }
        finally
        {
            selector.Selected -= OnSelected;
            selector.Cancelled -= OnCancelled;
        }
    }

    internal static string FormatModelId(Model model) => $"{model.Provider}/{model.Id}";
}
