using Tau.Ai;
using Tau.CodingAgent.Runtime;

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
}
