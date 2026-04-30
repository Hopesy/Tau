using Tau.Ai.Auth;

namespace Tau.Ai.Registry;

public sealed class ModelCatalog
{
    private readonly Dictionary<string, Dictionary<string, Model>> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProviderAuthResolver _authResolver;

    public ModelCatalog(ProviderAuthResolver? authResolver = null)
    {
        _authResolver = authResolver ?? new ProviderAuthResolver();

        foreach (var (provider, models) in BuiltInModels.Catalog)
        {
            _models[provider] = new Dictionary<string, Model>(models, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string> GetProviders() => [.. _models.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<Model> GetModels(string provider) =>
        _models.TryGetValue(provider, out var models)
            ? [.. models.Values.Select(_authResolver.ResolveModel)]
            : [];

    public Model GetModel(string provider, string modelId)
    {
        if (!_models.TryGetValue(provider, out var models) || !models.TryGetValue(modelId, out var model))
        {
            throw new KeyNotFoundException($"Model '{provider}/{modelId}' is not registered.");
        }

        return _authResolver.ResolveModel(model);
    }

    public Model? TryGetModel(string provider, string modelId)
    {
        if (!_models.TryGetValue(provider, out var models) || !models.TryGetValue(modelId, out var model))
        {
            return null;
        }

        return _authResolver.ResolveModel(model);
    }

    public void RegisterModel(Model model)
    {
        if (!_models.TryGetValue(model.Provider, out var models))
        {
            models = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);
            _models[model.Provider] = models;
        }

        models[model.Id] = model;
    }

    public static Model CreateOpenAiCompatibleModel(
        string provider,
        string id,
        string name,
        string baseUrl,
        bool reasoning,
        int contextWindow,
        int maxTokens,
        decimal inputCost,
        decimal outputCost) =>
        new()
        {
            Id = id,
            Name = name,
            Api = "openai-chat-completions",
            Provider = provider,
            BaseUrl = baseUrl,
            Reasoning = reasoning,
            ContextWindow = contextWindow,
            MaxOutputTokens = maxTokens,
            Cost = new ModelCost(inputCost, outputCost, inputCost / 10m, inputCost)
        };

    public static UsageCost CalculateCost(Model model, Usage usage)
    {
        if (model.Cost is null)
        {
            return default;
        }

        var cost = new UsageCost(
            Input: usage.InputTokens / 1_000_000m * model.Cost.Value.InputPerMillion,
            Output: usage.OutputTokens / 1_000_000m * model.Cost.Value.OutputPerMillion,
            CacheRead: usage.CacheReadTokens.GetValueOrDefault() / 1_000_000m * model.Cost.Value.CacheReadPerMillion.GetValueOrDefault(),
            CacheWrite: usage.CacheWriteTokens.GetValueOrDefault() / 1_000_000m * model.Cost.Value.CacheWritePerMillion.GetValueOrDefault());
        return ApplyServiceTierMultiplier(cost, usage.ServiceTier);
    }

    public static UsageCost CalculateCost(Model model, Usage usage, string? serviceTier) =>
        CalculateCost(model, usage with { ServiceTier = serviceTier });

    public static decimal GetServiceTierCostMultiplier(string? serviceTier) =>
        serviceTier?.Trim().ToLowerInvariant() switch
        {
            "flex" => 0.5m,
            "priority" => 2m,
            _ => 1m
        };

    private static UsageCost ApplyServiceTierMultiplier(UsageCost cost, string? serviceTier)
    {
        var multiplier = GetServiceTierCostMultiplier(serviceTier);
        return multiplier == 1m
            ? cost
            : new UsageCost(
                Input: cost.Input * multiplier,
                Output: cost.Output * multiplier,
                CacheRead: cost.CacheRead * multiplier,
                CacheWrite: cost.CacheWrite * multiplier);
    }

    public static bool SupportsXhigh(Model model)
    {
        return model.Id.Contains("gpt-5.2", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("gpt-5.3", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus-4-7", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus-4.7", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ModelsAreEqual(Model? left, Model? right)
    {
        return left is not null &&
               right is not null &&
               left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase) &&
               left.Provider.Equals(right.Provider, StringComparison.OrdinalIgnoreCase);
    }
}
