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

    [Fact]
    public void CreateBuiltInImagesRegistry_RegistersOpenRouterImageApi()
    {
        var registry = BuiltInProviders.CreateBuiltInImagesRegistry();

        Assert.Contains("openrouter-images", registry.RegisteredApis);
        Assert.Equal("openrouter-images", registry.Get("openrouter-images").Api);
    }

    [Fact]
    public void RegisterAllImages_RegistersOpenRouterImageApi()
    {
        var registry = new ImagesProviderRegistry();

        BuiltInProviders.RegisterAllImages(registry);

        Assert.Contains("openrouter-images", registry.RegisteredApis);
        Assert.Equal("openrouter-images", registry.Get("openrouter-images").Api);
    }
}
