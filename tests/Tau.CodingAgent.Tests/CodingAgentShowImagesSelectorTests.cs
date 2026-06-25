using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentShowImagesSelectorTests
{
    [Theory]
    [InlineData(true, "true", "Yes")]
    [InlineData(false, "false", "No")]
    public void CreateSelectList_UsesUpstreamLabelsDescriptionsAndCurrentSelection(
        bool currentValue,
        string expectedValue,
        string expectedLabel)
    {
        var selector = CodingAgentShowImagesSelector.CreateSelectList(currentValue);

        Assert.Equal(expectedValue, selector.SelectedItem?.Value);
        Assert.Equal(expectedLabel, selector.SelectedItem?.Label);
        Assert.Collection(
            selector.FilteredItems,
            item =>
            {
                Assert.Equal("true", item.Value);
                Assert.Equal("Yes", item.Label);
                Assert.Equal("Show images inline in terminal", item.Description);
            },
            item =>
            {
                Assert.Equal("false", item.Value);
                Assert.Equal("No", item.Label);
                Assert.Equal("Show text placeholder instead", item.Description);
            });
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    public void ParseValue_AcceptsSettingsAndUpstreamValues(string value, bool expected)
    {
        Assert.Equal(expected, CodingAgentShowImagesSelector.ParseValue(value));
    }

    [Fact]
    public void CreateSubmenu_ReturnsSettingsCompatibleValueOnSelection()
    {
        string? selected = null;
        var submenu = CodingAgentShowImagesSelector.CreateSubmenu(
            "true",
            value => selected = value);

        submenu.HandleInput(Key(ConsoleKey.DownArrow));
        submenu.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("false", selected);
    }

    [Fact]
    public void CreateSubmenu_ReturnsNullOnCancel()
    {
        var selected = "unchanged";
        var submenu = CodingAgentShowImagesSelector.CreateSubmenu(
            "false",
            value => selected = value);

        submenu.HandleInput(Key(ConsoleKey.Escape));

        Assert.Null(selected);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);
}
