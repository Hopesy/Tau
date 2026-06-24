using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class ImageModelCatalogTests
{
    [Fact]
    public void GetProviders_IncludesOpenRouterImageProvider()
    {
        var catalog = new ImageModelCatalog();

        var providers = catalog.GetProviders();

        Assert.Equal(["openrouter"], providers);
    }

    [Fact]
    public void GetModels_LoadsGeneratedOpenRouterImageCatalog()
    {
        var catalog = new ImageModelCatalog();

        var models = catalog.GetModels("openrouter");
        var ids = models.Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(34, models.Count);
        Assert.Contains("black-forest-labs/flux.2-flex", ids);
        Assert.Contains("google/gemini-2.5-flash-image", ids);
        Assert.Contains("openai/gpt-5.4-image-2", ids);
        Assert.Contains("openrouter/auto", ids);
        Assert.Contains("x-ai/grok-imagine-image-quality", ids);
    }

    [Fact]
    public void GetModel_ReturnsGeneratedModelWithImageMetadata()
    {
        var catalog = new ImageModelCatalog();

        var model = catalog.GetModel("openrouter", "google/gemini-2.5-flash-image");

        Assert.Equal("openrouter-images", model.Api);
        Assert.Equal("openrouter", model.Provider);
        Assert.Equal("https://openrouter.ai/api/v1", model.BaseUrl);
        Assert.Contains("image", model.InputModalities);
        Assert.Contains("text", model.InputModalities);
        Assert.Equal(["image", "text"], model.OutputModalities);
        Assert.Equal(0.3m, model.Cost!.Value.InputPerMillion);
        Assert.Equal(2.5m, model.Cost.Value.OutputPerMillion);
        Assert.Equal(0.03m, model.Cost.Value.CacheReadPerMillion);
        Assert.Equal(0.08333333333333334m, model.Cost.Value.CacheWritePerMillion);
    }

    [Fact]
    public void GetModel_LoadsOpenRouterAutoWithNegativeSentinelPricing()
    {
        var catalog = new ImageModelCatalog();

        var model = catalog.GetModel("openrouter", "openrouter/auto");

        Assert.Equal(["text", "image"], model.OutputModalities);
        Assert.Equal(-1_000_000m, model.Cost!.Value.InputPerMillion);
        Assert.Equal(-1_000_000m, model.Cost.Value.OutputPerMillion);
    }

    [Fact]
    public void RegisterModel_AddsOrReplacesCustomImageModel()
    {
        var catalog = new ImageModelCatalog();
        var model = new ImagesModel
        {
            Id = "custom/image",
            Name = "Custom Image",
            Api = "openrouter-images",
            Provider = "custom-provider",
            BaseUrl = "https://example.invalid/v1",
            InputModalities = ["text"],
            OutputModalities = ["image"],
            Cost = new ModelCost(1m, 2m, 0.1m, 0.2m)
        };

        catalog.RegisterModel(model);

        Assert.Contains("custom-provider", catalog.GetProviders());
        Assert.Same(model, catalog.GetModel("custom-provider", "custom/image"));
        Assert.Same(model, catalog.TryGetModel("custom-provider", "custom/image"));
    }

    [Fact]
    public void GetModel_ThrowsForUnknownImageModel()
    {
        var catalog = new ImageModelCatalog();

        var ex = Assert.Throws<KeyNotFoundException>(() => catalog.GetModel("openrouter", "missing"));

        Assert.Contains("openrouter/missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_MatchGeneratedOpenRouterImageCatalog()
    {
        var catalog = new ImageModelCatalog();

        Assert.Equal("openrouter", ImageModelCatalog.GetDefaultProviderId());
        var defaultModel = ImageModelCatalog.GetDefaultModelId("openrouter");

        Assert.Equal("openrouter/auto", defaultModel);
        Assert.NotNull(catalog.TryGetModel(ImageModelCatalog.GetDefaultProviderId(), defaultModel));
    }
}
