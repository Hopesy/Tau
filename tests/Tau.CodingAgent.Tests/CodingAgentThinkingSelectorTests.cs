using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentThinkingSelectorTests
{
    [Fact]
    public void CreateSelectList_UsesAvailableLevelsDescriptionsAndCurrentSelection()
    {
        var state = new CodingAgentThinkingSelectorState(
            ThinkingLevel.High,
            CodingAgentThinkingSelector.DefaultLevels);

        var selector = CodingAgentThinkingSelector.CreateSelectList(state);

        Assert.Equal(6, selector.FilteredItems.Count);
        Assert.Equal("high", selector.SelectedItem?.Value);
        Assert.Collection(
            selector.FilteredItems,
            item =>
            {
                Assert.Equal("off", item.Value);
                Assert.Equal("No reasoning", item.Description);
            },
            item =>
            {
                Assert.Equal("minimal", item.Value);
                Assert.Equal("Very brief reasoning (~1k tokens)", item.Description);
            },
            item =>
            {
                Assert.Equal("low", item.Value);
                Assert.Equal("Light reasoning (~2k tokens)", item.Description);
            },
            item =>
            {
                Assert.Equal("medium", item.Value);
                Assert.Equal("Moderate reasoning (~8k tokens)", item.Description);
            },
            item =>
            {
                Assert.Equal("high", item.Value);
                Assert.Equal("Deep reasoning (~16k tokens)", item.Description);
            },
            item =>
            {
                Assert.Equal("xhigh", item.Value);
                Assert.Equal("Maximum reasoning (~32k tokens)", item.Description);
            });
    }

    [Fact]
    public void CreateSelectList_NormalizesAliasesAndDropsUnsupportedLevels()
    {
        var state = new CodingAgentThinkingSelectorState(
            null,
            ["none", "med", "extra-high", "turbo", "medium"]);

        var selector = CodingAgentThinkingSelector.CreateSelectList(state);

        Assert.Equal(["off", "medium", "xhigh"], selector.FilteredItems.Select(item => item.Value));
        Assert.Equal("off", selector.SelectedItem?.Value);
    }
}
