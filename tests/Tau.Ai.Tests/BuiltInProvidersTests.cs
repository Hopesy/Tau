using Tau.Ai.Providers;

namespace Tau.Ai.Tests;

public sealed class BuiltInProvidersTests
{
    [Fact]
    public void RegisterAll_RegistersExtendedApiSurface()
    {
        var registry = new ProviderRegistry();

        BuiltInProviders.RegisterAll(registry);

        Assert.Contains("openai-chat-completions", registry.RegisteredApis);
        Assert.Contains("openai-responses", registry.RegisteredApis);
        Assert.Contains("openai-codex-responses", registry.RegisteredApis);
        Assert.Contains("azure-openai-responses", registry.RegisteredApis);
        Assert.Contains("mistral-conversations", registry.RegisteredApis);
        Assert.Contains("google-vertex", registry.RegisteredApis);
        Assert.Contains("google-gemini-cli", registry.RegisteredApis);
        Assert.Contains("bedrock-converse-stream", registry.RegisteredApis);
    }
}
