using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentProviderDisplayNamesTests
{
    [Theory]
    [InlineData("anthropic", "Anthropic")]
    [InlineData("amazon-bedrock", "Amazon Bedrock")]
    [InlineData("openai", "OpenAI")]
    [InlineData("openrouter", "OpenRouter")]
    [InlineData("google", "Google Gemini")]
    [InlineData("google-vertex", "Google Vertex AI")]
    [InlineData("nvidia", "NVIDIA NIM")]
    [InlineData("opencode", "OpenCode Zen")]
    [InlineData("opencode-go", "OpenCode Go")]
    [InlineData("xai", "xAI")]
    [InlineData("zai", "ZAI Coding Plan (Global)")]
    [InlineData("zai-coding-cn", "ZAI Coding Plan (China)")]
    [InlineData("minimax-cn", "MiniMax (China)")]
    [InlineData("xiaomi-token-plan-sgp", "Xiaomi MiMo Token Plan (Singapore)")]
    public void Resolve_KnownProvider_ReturnsUpstreamDisplayName(string providerId, string expected)
    {
        Assert.Equal(expected, CodingAgentProviderDisplayNames.Resolve(providerId));
        Assert.Equal(expected, CodingAgentProviderDisplayNames.TryGetBuiltIn(providerId));
        Assert.True(CodingAgentProviderDisplayNames.IsBuiltInProvider(providerId));
    }

    [Theory]
    [InlineData("custom-provider")]
    [InlineData("my-local-proxy")]
    public void Resolve_UnknownProvider_FallsBackToProviderId(string providerId)
    {
        Assert.Equal(providerId, CodingAgentProviderDisplayNames.Resolve(providerId));
        Assert.Null(CodingAgentProviderDisplayNames.TryGetBuiltIn(providerId));
        Assert.False(CodingAgentProviderDisplayNames.IsBuiltInProvider(providerId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OpenAI")]
    [InlineData("OPENAI")]
    public void Lookup_IsCaseSensitiveAndIdKeyed(string providerId)
    {
        // Upstream keys the table by exact provider id; only lowercase canonical ids match.
        Assert.Null(CodingAgentProviderDisplayNames.TryGetBuiltIn(providerId));
        Assert.False(CodingAgentProviderDisplayNames.IsBuiltInProvider(providerId));
        Assert.Equal(providerId, CodingAgentProviderDisplayNames.Resolve(providerId));
    }
}
