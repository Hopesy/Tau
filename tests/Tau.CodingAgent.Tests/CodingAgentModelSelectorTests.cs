using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentModelSelectorTests
{
    [Fact]
    public void CreateSelectList_UsesScopedModelsCurrentSelectionAndInitialFilter()
    {
        var openai = new Model
        {
            Provider = "openai",
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses"
        };
        var google = new Model
        {
            Provider = "google",
            Id = "gemini-2.5-pro",
            Name = "Gemini 2.5 Pro",
            Api = "google-gemini"
        };

        var selector = CodingAgentModelSelector.CreateSelectList(
            new CodingAgentModelSelectorState(
                [openai, google],
                [google],
                google,
                "gemini"));

        var item = Assert.Single(selector.FilteredItems);
        Assert.Equal("google/gemini-2.5-pro", item.Value);
        Assert.Equal("gemini-2.5-pro", item.Label);
        Assert.Equal(0, selector.SelectedIndex);
    }

    [Fact]
    public void CreateSelectList_UsesAllModelsWhenScopeMissing()
    {
        var openai = new Model
        {
            Provider = "openai",
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses"
        };
        var google = new Model
        {
            Provider = "google",
            Id = "gemini-2.5-pro",
            Name = "Gemini 2.5 Pro",
            Api = "google-gemini"
        };

        var selector = CodingAgentModelSelector.CreateSelectList(
            new CodingAgentModelSelectorState(
                [openai, google],
                null,
                openai,
                null));

        Assert.Equal(2, selector.FilteredItems.Count);
        Assert.Equal("openai/gpt-5.4", selector.SelectedItem?.Value);
    }

    [Fact]
    public void CreateSelectList_RendersConfiguredAuthFooterHint()
    {
        var openai = new Model
        {
            Provider = "openai",
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses"
        };

        var selector = CodingAgentModelSelector.CreateSelectList(
            new CodingAgentModelSelectorState(
                [openai],
                null,
                openai,
                null));

        var lines = selector.Render(80);

        Assert.Contains(CodingAgentModelSelector.AuthFilteringFooterHint, lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void CreateComponent_UsesScopedScopeByDefaultAndRendersSelectedModelDetail()
    {
        var openai = Model("openai", "gpt-5.4", "GPT-5.4");
        var google = Model("google", "gemini-2.5-pro", "Gemini 2.5 Pro");

        var component = CodingAgentModelSelector.CreateComponent(
            new CodingAgentModelSelectorState(
                [openai, google],
                [google],
                google,
                null));

        var lines = component.Render(80);

        Assert.Equal(CodingAgentModelSelectorScope.Scoped, component.Scope);
        Assert.Equal(["google/gemini-2.5-pro"], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.Contains("Model Selector", lines[0], StringComparison.Ordinal);
        Assert.Contains("Search: ", lines, StringComparer.Ordinal);
        Assert.Contains("Scope: all | [scoped]", lines, StringComparer.Ordinal);
        Assert.Contains("Tab: switch scope (all/scoped)", lines, StringComparer.Ordinal);
        Assert.Contains("  Model Name: Gemini 2.5 Pro", lines, StringComparer.Ordinal);
        Assert.Contains(CodingAgentModelSelector.AuthFilteringFooterHint, lines, StringComparer.Ordinal);
        Assert.Equal(new string('-', 80), lines[^1]);
    }

    [Fact]
    public void Component_TabTogglesBetweenScopedAndAllCandidates()
    {
        var openai = Model("openai", "gpt-5.4", "GPT-5.4");
        var google = Model("google", "gemini-2.5-pro", "Gemini 2.5 Pro");
        var component = CodingAgentModelSelector.CreateComponent(
            new CodingAgentModelSelectorState(
                [openai, google],
                [google],
                google,
                null));

        var firstToggle = component.HandleInput(Key(ConsoleKey.Tab));

        Assert.Equal(CodingAgentModelSelectorScope.All, component.Scope);
        Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.Equal("google/gemini-2.5-pro", component.SelectedItem?.Value);
        var allLines = component.Render(80);
        Assert.True(firstToggle.Consumed);
        Assert.Contains("Scope: [all] | scoped", allLines, StringComparer.Ordinal);
        Assert.Contains("  Model Name: Gemini 2.5 Pro", allLines, StringComparer.Ordinal);

        var secondToggle = component.HandleInput(Key(ConsoleKey.Tab));

        Assert.True(secondToggle.Consumed);
        Assert.Equal(CodingAgentModelSelectorScope.Scoped, component.Scope);
        Assert.Equal(["google/gemini-2.5-pro"], component.FilteredItems.Select(item => item.Value).ToArray());
    }

    [Fact]
    public void Component_TabIsIgnoredWhenNoScopedModelsExist()
    {
        var openai = Model("openai", "gpt-5.4", "GPT-5.4");
        var google = Model("google", "gemini-2.5-pro", "Gemini 2.5 Pro");
        var component = CodingAgentModelSelector.CreateComponent(
            new CodingAgentModelSelectorState(
                [openai, google],
                null,
                openai,
                null));

        var result = component.HandleInput(Key(ConsoleKey.Tab));
        var lines = component.Render(80);

        Assert.False(result.Consumed);
        Assert.Equal(CodingAgentModelSelectorScope.All, component.Scope);
        Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.DoesNotContain(lines, line => line.Contains("Scope:", StringComparison.Ordinal));
        Assert.Contains(CodingAgentModelSelector.AuthFilteringFooterHint, lines, StringComparer.Ordinal);
    }

    [Fact]
    public void Component_SearchInputFiltersAndBackspaceRestoresCandidates()
    {
        var openai = Model("openai", "gpt-5.4", "GPT-5.4");
        var google = Model("google", "gemini-2.5-pro", "Gemini 2.5 Pro");
        var component = CodingAgentModelSelector.CreateComponent(
            new CodingAgentModelSelectorState(
                [openai, google],
                null,
                openai,
                null));

        Assert.True(component.HandleInput(CharKey('g')).Consumed);
        Assert.Equal("g", component.Filter);
        Assert.Equal(["google/gemini-2.5-pro", "openai/gpt-5.4"], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.Contains("Search: g", component.Render(80), StringComparer.Ordinal);

        Assert.True(component.HandleInput(CharKey('e')).Consumed);
        Assert.Equal("ge", component.Filter);
        Assert.Equal(["google/gemini-2.5-pro", "openai/gpt-5.4"], component.FilteredItems.Select(item => item.Value).ToArray());
        Assert.Contains("Search: ge", component.Render(80), StringComparer.Ordinal);

        Assert.True(component.HandleInput(Key(ConsoleKey.Backspace)).Consumed);
        Assert.Equal("g", component.Filter);
        Assert.Equal(["google/gemini-2.5-pro", "openai/gpt-5.4"], component.FilteredItems.Select(item => item.Value).ToArray());
    }

    [Fact]
    public async Task SelectAsync_SearchInputFiltersAndSelectsMatchingModel()
    {
        var openai = Model("openai", "gpt-5.4", "GPT-5.4");
        var google = Model("google", "gemini-2.5-pro", "Gemini 2.5 Pro");
        var state = new CodingAgentModelSelectorState(
            [openai, google],
            null,
            openai,
            null);
        var keyReader = new ScriptedKeyReader(
            CharKey('g'),
            CharKey('e'),
            Key(ConsoleKey.Enter));
        var surface = new CapturingRenderSurface(width: 80, height: 20);

        var selected = await CodingAgentModelSelector.SelectAsync(state, keyReader, surface);

        Assert.Equal("google/gemini-2.5-pro", selected);
        AssertDiffContains(surface.Diffs[0], "Search: ");
        AssertDiffContains(surface.Diffs[1], "Search: g");
        AssertDiffContains(surface.Diffs[2], "Search: ge");
    }

    [Fact]
    public async Task SelectAsync_TabCanSelectFromAllScope()
    {
        var openai = Model("openai", "gpt-5.4", "GPT-5.4");
        var google = Model("google", "gemini-2.5-pro", "Gemini 2.5 Pro");
        var state = new CodingAgentModelSelectorState(
            [openai, google],
            [google],
            google,
            null);
        var keyReader = new ScriptedKeyReader(
            Key(ConsoleKey.Tab),
            Key(ConsoleKey.DownArrow),
            Key(ConsoleKey.Enter));
        var surface = new CapturingRenderSurface(width: 80, height: 20);

        var selected = await CodingAgentModelSelector.SelectAsync(state, keyReader, surface);

        Assert.Equal("openai/gpt-5.4", selected);
        AssertDiffContains(surface.Diffs[0], "Scope: all | [scoped]");
        AssertDiffContains(surface.Diffs[1], "Scope: [all] | scoped");
    }

    private static Model Model(string provider, string id, string name) =>
        new()
        {
            Provider = provider,
            Id = id,
            Name = name,
            Api = $"{provider}-api"
        };

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo CharKey(char value) =>
        new(value, (ConsoleKey)char.ToUpperInvariant(value), shift: false, alt: false, control: false);

    private static void AssertDiffContains(TuiRenderDiff diff, string expected) =>
        Assert.Contains(diff.Operations, operation => operation.Text.Contains(expected, StringComparison.Ordinal));

    private sealed class ScriptedKeyReader(params ConsoleKeyInfo[] keys) : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_keys.Dequeue());
        }
    }

    private sealed class CapturingRenderSurface(int width, int height) : ITuiRenderSurface
    {
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public List<TuiRenderDiff> Diffs { get; } = [];

        public void Apply(TuiRenderDiff diff) => Diffs.Add(diff);
    }
}
