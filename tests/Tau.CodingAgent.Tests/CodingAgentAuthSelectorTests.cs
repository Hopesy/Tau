using Tau.Ai.Auth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentAuthSelectorTests
{
    [Fact]
    public void CreateSelectList_ReflectsAuthStatusAndCurrentProvider()
    {
        var state = new CodingAgentAuthSelectorState(
            "google",
            [
                new ProviderAuthStatus("openai", true, "environment", false, false, "Credentials are available."),
                new ProviderAuthStatus("google", false, "none", false, true, "No credentials found."),
                new ProviderAuthStatus("anthropic", true, "auth.json oauth", true, true, "OAuth credentials found in auth.json.")
            ]);

        var selector = CodingAgentAuthSelector.CreateSelectList(state);

        Assert.Equal("google", selector.SelectedItem?.Value);
        Assert.Equal(3, selector.FilteredItems.Count);
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == "openai" &&
            item.Description == "configured via environment");
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == "google" &&
            item.Description == "missing via none, login available");
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == "anthropic" &&
            item.Description == "configured via auth.json oauth, oauth, login available");
    }
}
