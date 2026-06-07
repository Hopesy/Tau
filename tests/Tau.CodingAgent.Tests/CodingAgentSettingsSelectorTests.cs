using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public class CodingAgentSettingsSelectorTests
{
    [Fact]
    public void CreateSelectList_ReflectsCurrentSettingsAndRuntimeState()
    {
        var state = new CodingAgentSettingsSelectorState(
            "settings.json",
            new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                "labeled-only",
                DefaultThinkingLevel: "low",
                SteeringMode: "all",
                FollowUpMode: "one-at-a-time",
                AutoCompactionEnabled: false,
                Theme: "solarized"),
            new Model { Provider = "openai", Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-responses" },
            ThinkingLevel.High,
            AutoCompactionEnabled: false,
            CurrentTheme: "solarized");

        var selector = CodingAgentSettingsSelector.CreateSelectList(state);

        Assert.Equal(7, selector.FilteredItems.Count);
        Assert.Collection(
            selector.FilteredItems,
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.AutoCompactionAction, item.Value);
                Assert.Equal("Auto compaction", item.Label);
                Assert.Equal("effective disabled, setting disabled", item.Description);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.SteeringModeAction, item.Value);
                Assert.Equal("all", item.Description);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.FollowUpModeAction, item.Value);
                Assert.Equal("one-at-a-time", item.Description);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.TreeFilterModeAction, item.Value);
                Assert.Equal("labeled-only", item.Description);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ThinkingLevelAction, item.Value);
                Assert.Equal("current high, default low", item.Description);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ScopedModelsAction, item.Value);
                Assert.Equal("Scoped models", item.Label);
                Assert.Equal("all enabled", item.Description);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ThemeAction, item.Value);
                Assert.Equal("solarized", item.Description);
            });
    }

    [Fact]
    public void CreateSettingsList_ReflectsUpstreamSettingsListSurface()
    {
        var state = new CodingAgentSettingsSelectorState(
            "settings.json",
            new CodingAgentSettingsSnapshot(
                "openai",
                "gpt-5.4",
                "labeled-only",
                DefaultThinkingLevel: "low",
                EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro"],
                SteeringMode: "all",
                FollowUpMode: "one-at-a-time",
                AutoCompactionEnabled: false,
                Theme: "solarized",
                QuietStartup: true,
                CollapseChangelog: true,
                EnableInstallTelemetry: false,
                TerminalShowImages: false,
                TerminalClearOnShrink: true,
                ImagesAutoResize: false,
                ImagesBlockImages: true,
                ShowHardwareCursor: true,
                EditorPaddingX: 2,
                AutocompleteMaxVisible: 10),
            new Model
            {
                Provider = "openai",
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-responses",
                Reasoning = true
            },
            ThinkingLevel.High,
            AutoCompactionEnabled: false,
            CurrentTheme: "solarized");

        var selector = CodingAgentSettingsSelector.CreateSettingsList(state);

        Assert.Equal(17, selector.FilteredItems.Count);
        Assert.Collection(
            selector.FilteredItems,
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.AutoCompactionAction, item.Id);
                Assert.Equal("Auto compaction", item.Label);
                Assert.Equal("false", item.CurrentValue);
                Assert.Equal(["true", "false"], item.Values);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.TerminalShowImagesAction, item.Id);
                Assert.Equal("Show images", item.Label);
                Assert.Equal("false", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ImagesAutoResizeAction, item.Id);
                Assert.Equal("false", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ImagesBlockImagesAction, item.Id);
                Assert.Equal("true", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ShowHardwareCursorAction, item.Id);
                Assert.Equal("true", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.EditorPaddingAction, item.Id);
                Assert.Equal("2", item.CurrentValue);
                Assert.Equal(["0", "1", "2", "3"], item.Values);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.AutocompleteMaxVisibleAction, item.Id);
                Assert.Equal("10", item.CurrentValue);
                Assert.Equal(["3", "5", "7", "10", "15", "20"], item.Values);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.TerminalClearOnShrinkAction, item.Id);
                Assert.Equal("true", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.SteeringModeAction, item.Id);
                Assert.Equal("all", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.FollowUpModeAction, item.Id);
                Assert.Equal("one-at-a-time", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.TreeFilterModeAction, item.Id);
                Assert.Equal("labeled-only", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ThinkingLevelAction, item.Id);
                Assert.Equal("high", item.CurrentValue);
                Assert.Contains("xhigh", item.Values);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.QuietStartupAction, item.Id);
                Assert.Equal("true", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.CollapseChangelogAction, item.Id);
                Assert.Equal("true", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.InstallTelemetryAction, item.Id);
                Assert.Equal("false", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ScopedModelsAction, item.Id);
                Assert.Equal("2 enabled", item.CurrentValue);
            },
            item =>
            {
                Assert.Equal(CodingAgentSettingsSelector.ThemeAction, item.Id);
                Assert.Equal("solarized", item.CurrentValue);
            });
    }

    [Fact]
    public async Task SelectSettingsListAsync_ReturnsChangedSettingPayload()
    {
        var state = new CodingAgentSettingsSelectorState(
            "settings.json",
            new CodingAgentSettingsSnapshot(null, null, AutoCompactionEnabled: false),
            new Model
            {
                Provider = "openai",
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-responses",
                Reasoning = true
            },
            null,
            AutoCompactionEnabled: false,
            CurrentTheme: "dark");
        var keyReader = new ScriptedKeyReader(Key(ConsoleKey.Enter));
        var surface = new CapturingRenderSurface(width: 80, height: 20);

        var selected = await CodingAgentSettingsSelector.SelectSettingsListAsync(state, keyReader, surface);

        Assert.Equal(
            CodingAgentSettingsSelector.FormatSelection(CodingAgentSettingsSelector.AutoCompactionAction, "true"),
            selected);
        Assert.NotEmpty(surface.Diffs);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);

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
