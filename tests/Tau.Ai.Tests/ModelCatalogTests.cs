using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public void GetProviders_IncludesPortedProviderFamilies()
    {
        var catalog = new ModelCatalog();

        var providers = catalog.GetProviders();

        Assert.Contains("openai", providers);
        Assert.Contains("azure-openai-responses", providers);
        Assert.Contains("openai-codex", providers);
        Assert.Contains("mistral", providers);
        Assert.Contains("google-vertex", providers);
        Assert.Contains("google-gemini-cli", providers);
        Assert.Contains("amazon-bedrock", providers);
    }

    [Fact]
    public void GetModel_ReturnsExpectedModel()
    {
        var catalog = new ModelCatalog();

        var model = catalog.GetModel("openai-codex", "gpt-5.2-codex");

        Assert.Equal("openai-codex-responses", model.Api);
        Assert.Equal("openai-codex", model.Provider);
        Assert.True(model.Reasoning);
    }

    [Fact]
    public void CalculateCost_ReturnsActualUsageCost()
    {
        var model = new Model
        {
            Id = "test",
            Name = "Test",
            Api = "openai-chat-completions",
            Provider = "openai",
            Cost = new ModelCost(2m, 8m, 0.5m, 1m)
        };

        var cost = ModelCatalog.CalculateCost(model, new Usage(500_000, 250_000, 100_000, 50_000));

        Assert.Equal(1.0m, cost.Input);
        Assert.Equal(2.0m, cost.Output);
        Assert.Equal(0.05m, cost.CacheRead);
        Assert.Equal(0.05m, cost.CacheWrite);
        Assert.Equal(3.10m, cost.Total);
    }

    [Fact]
    public void CalculateCost_AppliesResponsesServiceTierMultiplier()
    {
        var model = new Model
        {
            Id = "test",
            Name = "Test",
            Api = "openai-responses",
            Provider = "openai",
            Cost = new ModelCost(2m, 8m, 0.5m, 1m)
        };

        var priority = ModelCatalog.CalculateCost(model, new Usage(500_000, 250_000, 100_000, 50_000, "priority"));
        var flex = ModelCatalog.CalculateCost(model, new Usage(500_000, 250_000, 100_000, 50_000, "flex"));

        Assert.Equal(6.20m, priority.Total);
        Assert.Equal(1.55m, flex.Total);
        Assert.Equal(2m, ModelCatalog.GetServiceTierCostMultiplier("priority"));
        Assert.Equal(0.5m, ModelCatalog.GetServiceTierCostMultiplier("flex"));
    }

    [Fact]
    public void SupportsXhigh_ReturnsTrue_ForGpt54AndOpus46()
    {
        var gpt = new Model { Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-chat-completions", Provider = "openai" };
        var opus = new Model { Id = "claude-opus-4-6", Name = "Claude Opus 4.6", Api = "anthropic-messages", Provider = "anthropic" };

        Assert.True(ModelCatalog.SupportsXhigh(gpt));
        Assert.True(ModelCatalog.SupportsXhigh(opus));
    }
}
