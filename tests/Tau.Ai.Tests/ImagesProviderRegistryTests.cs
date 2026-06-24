using Tau.Ai.Providers;

namespace Tau.Ai.Tests;

public sealed class ImagesProviderRegistryTests
{
    [Fact]
    public void Register_Factory_DefersConstructionUntilFirstResolution()
    {
        var registry = new ImagesProviderRegistry();
        var constructed = 0;

        registry.Register("openrouter-images", () =>
        {
            constructed++;
            return new FakeImagesProvider("openrouter-images");
        });

        Assert.Equal(0, constructed);
        Assert.Contains("openrouter-images", registry.RegisteredApis);

        var first = registry.Get("openrouter-images");
        var second = registry.Get("openrouter-images");

        Assert.Equal(1, constructed);
        Assert.Same(first, second);
    }

    [Fact]
    public void Register_ReplacesExistingProviderForSameApi()
    {
        var registry = new ImagesProviderRegistry();
        var original = new FakeImagesProvider("openrouter-images");
        var replacement = new FakeImagesProvider("openrouter-images");

        registry.Register("openrouter-images", original);
        registry.Register("openrouter-images", replacement);

        Assert.Same(replacement, registry.Get("openrouter-images"));
        Assert.Single(registry.RegisteredApis, "openrouter-images");
    }

    [Fact]
    public void UnregisterBySource_RemovesOnlyMatchingSource()
    {
        var registry = new ImagesProviderRegistry();
        registry.Register("source-a", new FakeImagesProvider("source-a"), sourceId: "package-a");
        registry.Register("source-b", new FakeImagesProvider("source-b"), sourceId: "package-b");
        registry.Register("unsourced", new FakeImagesProvider("unsourced"));

        registry.UnregisterBySource("package-a");

        Assert.Null(registry.TryGet("source-a"));
        Assert.NotNull(registry.TryGet("source-b"));
        Assert.NotNull(registry.TryGet("unsourced"));
    }

    [Fact]
    public void Get_ThrowsWhenApiNotRegistered()
    {
        var registry = new ImagesProviderRegistry();

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Get("missing"));
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_RemovesAllProviders()
    {
        var registry = new ImagesProviderRegistry();
        registry.Register("a", new FakeImagesProvider("a"));
        registry.Register("b", new FakeImagesProvider("b"));

        registry.Clear();

        Assert.Empty(registry.RegisteredApis);
        Assert.Null(registry.TryGet("a"));
        Assert.Null(registry.TryGet("b"));
    }

    [Fact]
    public async Task ImageFunctions_RejectsProviderWhoseApiDoesNotMatchModelApi()
    {
        var registry = new ImagesProviderRegistry();
        registry.Register("openrouter-images", new FakeImagesProvider("other-images"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImageFunctions.GenerateImagesAsync(
                registry,
                new ImagesModel
                {
                    Provider = "openrouter",
                    Id = "openrouter/test-image",
                    Name = "OpenRouter Test Image",
                    Api = "openrouter-images"
                },
                new ImagesContext([new TextContent("draw")]),
                new ImagesOptions()));

        Assert.Contains(
            "Mismatched image provider API: other-images expected openrouter-images.",
            ex.Message,
            StringComparison.Ordinal);
    }

    private sealed class FakeImagesProvider(string api) : IImagesProvider
    {
        public string Api { get; } = api;

        public Task<AssistantImages> GenerateImagesAsync(
            ImagesModel model,
            ImagesContext context,
            ImagesOptions options) =>
            throw new NotSupportedException("GenerateImagesAsync is not exercised by registry tests.");
    }
}
