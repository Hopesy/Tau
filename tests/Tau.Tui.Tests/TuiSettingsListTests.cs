using Tau.Tui.Abstractions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiSettingsListTests
{
    [Fact]
    public void SettingsList_RendersAlignedValuesDescriptionAndHint()
    {
        var list = new TuiSettingsList(
            [
                new TuiSettingItem(
                    "auto-compaction",
                    "Auto compaction",
                    "disabled",
                    "Compact automatically when token budget is exceeded.",
                    ["enabled", "disabled"]),
                new TuiSettingItem("steering-mode", "Steering mode", "immediate")
            ],
            maxVisible: 5);

        var lines = list.Render(72);

        Assert.StartsWith("> Auto compaction", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  Steering mode", lines[1], StringComparison.Ordinal);
        Assert.Equal(VisibleIndexOf(lines[0], "disabled"), VisibleIndexOf(lines[1], "immediate"));
        Assert.Contains("Compact automatically", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.Contains("Enter/Space to change", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsList_SearchFiltersWithFuzzyMatchAndShowsSearchHint()
    {
        var list = new TuiSettingsList(
            [
                new TuiSettingItem("auto-compaction", "Auto compaction", "enabled"),
                new TuiSettingItem("steering-mode", "Steering mode", "immediate"),
                new TuiSettingItem("theme", "Theme", "dark")
            ],
            maxVisible: 5,
            options: new TuiSettingsListOptions(EnableSearch: true));

        Assert.True(list.HandleInput(new ConsoleKeyInfo('s', ConsoleKey.S, shift: false, alt: false, control: false)).Consumed);
        Assert.True(list.HandleInput(new ConsoleKeyInfo('m', ConsoleKey.M, shift: false, alt: false, control: false)).Consumed);

        Assert.Equal("sm", list.SearchText);
        Assert.Equal(["steering-mode"], list.FilteredItems.Select(static item => item.Id));

        var lines = list.Render(60);

        Assert.Equal("> sm                                                        ", lines[0]);
        Assert.Contains("Steering mode", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.Contains("Type to search", lines[^1], StringComparison.Ordinal);

        list.SetSearchText("zzz");

        Assert.Empty(list.FilteredItems);
        Assert.Contains("No matching settings", string.Join('\n', list.Render(60)), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsList_CyclesValuesWithEnterOrSpaceAndRaisesChange()
    {
        var list = new TuiSettingsList(
            [
                new TuiSettingItem("thinking", "Thinking", "off", values: ["off", "low", "high"])
            ],
            maxVisible: 5);
        var changes = new List<(string Id, string Value)>();
        list.Changed += (id, value) => changes.Add((id, value));

        Assert.True(list.HandleInput(Key(ConsoleKey.Enter)).Consumed);
        Assert.Equal("low", list.SelectedItem?.CurrentValue);

        Assert.True(list.HandleInput(Key(ConsoleKey.Spacebar, keyChar: ' ')).Consumed);
        Assert.Equal("high", list.SelectedItem?.CurrentValue);

        Assert.Equal([("thinking", "low"), ("thinking", "high")], changes);
    }

    [Fact]
    public void SettingsList_DelegatesSubmenuAndRestoresSelectionAfterDone()
    {
        var submenu = new ScriptedSubmenu("low");
        var list = new TuiSettingsList(
            [
                new TuiSettingItem("theme", "Theme", "dark"),
                new TuiSettingItem(
                    "thinking",
                    "Thinking",
                    "off",
                    submenu: (_, done) =>
                    {
                        submenu.Done = done;
                        return submenu;
                    }),
                new TuiSettingItem("models", "Models", "all")
            ],
            maxVisible: 5);
        list.SetSelectedIndex(1);
        var changes = new List<(string Id, string Value)>();
        list.Changed += (id, value) => changes.Add((id, value));

        Assert.True(list.HandleInput(Key(ConsoleKey.Enter)).Consumed);
        Assert.True(list.IsSubmenuOpen);
        Assert.Equal(["submenu:low"], list.Render(40));

        Assert.True(list.HandleInput(Key(ConsoleKey.Enter)).Consumed);

        Assert.False(list.IsSubmenuOpen);
        Assert.Equal("thinking", list.SelectedItem?.Id);
        Assert.Equal("low", list.SelectedItem?.CurrentValue);
        Assert.Equal([("thinking", "low")], changes);
    }

    [Fact]
    public void SettingsList_CancelRaisesCancelled()
    {
        var list = new TuiSettingsList(
            [
                new TuiSettingItem("theme", "Theme", "dark")
            ],
            maxVisible: 5);
        var cancelled = false;
        list.Cancelled += () => cancelled = true;

        Assert.True(list.HandleInput(Key(ConsoleKey.Escape)).Consumed);

        Assert.True(cancelled);
    }

    private static int VisibleIndexOf(string line, string value)
    {
        var index = line.IndexOf(value, StringComparison.Ordinal);
        Assert.NotEqual(-1, index);
        return TuiText.VisibleWidth(line[..index]);
    }

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char keyChar = '\0',
        bool alt = false,
        bool control = false) =>
        new(keyChar, key, shift: false, alt: alt, control: control);

    private sealed class ScriptedSubmenu(string value) : ITuiInputComponent
    {
        public Action<string?>? Done { get; set; }

        public IReadOnlyList<string> Render(int width) => [$"submenu:{value}"];

        public void Invalidate()
        {
        }

        public TuiInputResult HandleInput(ConsoleKeyInfo key)
        {
            Done?.Invoke(value);
            return TuiInputResult.Handled;
        }
    }
}
