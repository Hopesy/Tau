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
        // 1. 值保持原始 provider id，用于选择和登录
        // 2. 标签渲染 provider-display-names.ts 对齐的上游显示名
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == "openai" &&
            item.Label == "OpenAI" &&
            item.Description == "configured via environment");
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == "google" &&
            item.Label == "Google Gemini" &&
            item.Description == "missing via none, login available");
        Assert.Contains(selector.FilteredItems, item =>
            item.Value == "anthropic" &&
            item.Label == "Anthropic" &&
            item.Description == "configured via auth.json oauth, oauth, login available");
    }

    [Fact]
    public void CreateSelectList_UnknownProvider_FallsBackToProviderIdLabel()
    {
        var state = new CodingAgentAuthSelectorState(
            "custom-provider",
            [
                new ProviderAuthStatus("custom-provider", true, "environment", false, false, "Credentials are available.")
            ]);

        var selector = CodingAgentAuthSelector.CreateSelectList(state);

        Assert.Contains(selector.FilteredItems, item =>
            item.Value == "custom-provider" &&
            item.Label == "custom-provider");
    }
}
