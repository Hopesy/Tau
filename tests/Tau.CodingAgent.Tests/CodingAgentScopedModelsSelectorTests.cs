using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentScopedModelsSelectorTests
{
    [Fact]
    public void CreateSelectList_ReflectsEnabledModelsAndCurrentModel()
    {
        var openAi = new Model
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
        var state = new CodingAgentScopedModelsSelectorState(
            "settings.json",
            [openAi, google],
            ["google/gemini-2.5-pro:high"],
            openAi);

        var selector = CodingAgentScopedModelsSelector.CreateSelectList(state);

        Assert.Equal(["google/gemini-2.5-pro"], selector.SelectedValues);
        Assert.Equal(2, selector.FilteredItems.Count);
        Assert.Equal("openai/gpt-5.4", selector.SelectedItem?.Value);
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == "google/gemini-2.5-pro" &&
            item.Description == "Gemini 2.5 Pro" &&
            item.Group == "google");
    }
}
