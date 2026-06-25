using Tau.Ai.Auth;

namespace Tau.Ai.Registry;

public readonly record struct ResolvedModelSelection(string Provider, string ModelId)
{
    public string CanonicalReference => $"{Provider}/{ModelId}";
}

public sealed class ModelCatalog
{
    private const string DefaultProviderId = "openai";
    private static readonly IReadOnlyDictionary<string, string> DefaultModelIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = "claude-opus-4-6",
        ["openai"] = "gpt-5.4",
        ["google"] = "gemini-2.5-pro",
        ["azure-openai-responses"] = "gpt-5.2",
        ["openai-codex"] = "gpt-5.4",
        ["github-copilot"] = "gpt-4o",
        ["mistral"] = "devstral-medium-latest",
        ["google-vertex"] = "gemini-3-pro-preview",
        ["google-gemini-cli"] = "gemini-2.5-pro",
        ["google-antigravity"] = "gemini-3.1-pro-high",
        ["amazon-bedrock"] = "us.anthropic.claude-opus-4-6-v1",
        ["ant-ling"] = "Ling-2.6-1T",
        ["cloudflare-workers-ai"] = "@cf/meta/llama-4-scout-17b-16e-instruct",
        ["deepseek"] = "deepseek-v4-pro",
        ["huggingface"] = "moonshotai/Kimi-K2-Instruct",
        ["moonshotai"] = "kimi-k2-thinking-turbo",
        ["moonshotai-cn"] = "kimi-k2-thinking-turbo",
        ["nvidia"] = "nvidia/nemotron-3-ultra-550b-a55b",
        ["openrouter"] = "anthropic/claude-sonnet-4.6",
        ["together"] = "moonshotai/Kimi-K2.6",
        ["xai"] = "grok-4.3",
        ["xiaomi"] = "mimo-v2.5-pro",
        ["xiaomi-token-plan-ams"] = "mimo-v2.5-pro",
        ["xiaomi-token-plan-cn"] = "mimo-v2.5-pro",
        ["xiaomi-token-plan-sgp"] = "mimo-v2.5-pro",
        ["zai"] = "glm-4.7",
        ["zai-coding-cn"] = "glm-4.7",
        ["groq"] = "openai/gpt-oss-120b",
        ["cerebras"] = "gpt-oss-120b"
    };

    private readonly Dictionary<string, Dictionary<string, Model>> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProviderAuthResolver _authResolver;

    public ModelCatalog(ProviderAuthResolver? authResolver = null, ModelConfigurationStore? configurationStore = null)
    {
        _authResolver = authResolver ?? new ProviderAuthResolver();

        foreach (var catalog in new[] { BuiltInModels.Catalog, GeneratedBuiltInModels.Catalog })
        {
            foreach (var (provider, models) in catalog)
            {
                if (!_models.TryGetValue(provider, out var bucket))
                {
                    bucket = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);
                    _models[provider] = bucket;
                }

                foreach (var (modelId, model) in models)
                {
                    bucket[modelId] = model;
                }
            }
        }

        (configurationStore ?? new ModelConfigurationStore()).ApplyTo(_models);
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

    public string ResolveProvider(string? providerHint, string? defaultProvider = null)
    {
        var provider = NormalizeProviderHint(providerHint, defaultProvider);
        if (TryGetCanonicalProvider(provider, out var canonicalProvider))
        {
            return canonicalProvider;
        }

        throw new KeyNotFoundException($"Provider '{provider}' is not registered.");
    }

    public ResolvedModelSelection ResolveSelection(string? providerHint = null, string? modelHint = null, string? defaultProvider = null)
    {
        var normalizedModelHint = NormalizeHint(modelHint);
        var normalizedProviderHint = NormalizeHint(providerHint);

        if (IsDefaultKeyword(normalizedProviderHint))
        {
            normalizedProviderHint = null;
        }

        if (IsDefaultKeyword(normalizedModelHint))
        {
            normalizedModelHint = null;
        }

        if (TryResolveCanonicalReference(normalizedProviderHint, normalizedModelHint, defaultProvider, out var canonicalSelection))
        {
            return canonicalSelection;
        }

        var resolvedProvider = ResolveProvider(normalizedProviderHint, defaultProvider);
        if (string.IsNullOrWhiteSpace(normalizedModelHint))
        {
            return new ResolvedModelSelection(resolvedProvider, GetDefaultModelId(resolvedProvider));
        }

        if (TryGetModel(resolvedProvider, normalizedModelHint) is not null)
        {
            return new ResolvedModelSelection(resolvedProvider, normalizedModelHint);
        }

        if (string.IsNullOrWhiteSpace(normalizedProviderHint))
        {
            var exactMatches = _models
                .Where(entry => entry.Value.ContainsKey(normalizedModelHint))
                .Select(entry => entry.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (exactMatches.Length == 1)
            {
                return new ResolvedModelSelection(exactMatches[0], normalizedModelHint);
            }

            if (exactMatches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Model '{normalizedModelHint}' is ambiguous. Specify provider explicitly: {string.Join(", ", exactMatches.Select(provider => $"{provider}/{normalizedModelHint}"))}.");
            }
        }

        throw new KeyNotFoundException($"Model '{resolvedProvider}/{normalizedModelHint}' is not registered.");
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

    public static string GetDefaultProviderId() => DefaultProviderId;

    public static string GetDefaultModelId(string providerId)
    {
        if (DefaultModelIds.TryGetValue(providerId, out var modelId))
        {
            return modelId;
        }

        return DefaultModelIds[DefaultProviderId];
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
        decimal outputCost,
        ModelCompatibility? compat = null) =>
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
            Cost = new ModelCost(inputCost, outputCost, inputCost / 10m, inputCost),
            Compat = compat
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

    private bool TryResolveCanonicalReference(
        string? providerHint,
        string? modelHint,
        string? defaultProvider,
        out ResolvedModelSelection selection)
    {
        selection = default;
        if (string.IsNullOrWhiteSpace(modelHint))
        {
            return false;
        }

        var slashIndex = modelHint.IndexOf('/');
        if (slashIndex < 0)
        {
            return false;
        }

        var referencedProvider = modelHint[..slashIndex].Trim();
        var referencedModel = modelHint[(slashIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(referencedProvider) || string.IsNullOrWhiteSpace(referencedModel))
        {
            return false;
        }

        var canonicalProvider = ResolveProvider(referencedProvider, defaultProvider);
        if (string.IsNullOrWhiteSpace(providerHint))
        {
            if (TryGetModel(canonicalProvider, referencedModel) is null)
            {
                return false;
            }

            selection = new ResolvedModelSelection(canonicalProvider, referencedModel);
            return true;
        }

        var resolvedProvider = ResolveProvider(providerHint, defaultProvider);
        if (!resolvedProvider.Equals(canonicalProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Provider hint '{resolvedProvider}' conflicts with model reference '{canonicalProvider}/{referencedModel}'.");
        }

        if (TryGetModel(resolvedProvider, referencedModel) is null)
        {
            return false;
        }

        selection = new ResolvedModelSelection(resolvedProvider, referencedModel);
        return true;
    }

    private static string NormalizeHint(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsDefaultKeyword(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Equals("default", StringComparison.OrdinalIgnoreCase);

    private string NormalizeProviderHint(string? providerHint, string? defaultProvider)
    {
        var candidate = NormalizeHint(providerHint);
        if (string.IsNullOrWhiteSpace(candidate) || IsDefaultKeyword(candidate))
        {
            candidate = NormalizeHint(defaultProvider);
        }

        if (string.IsNullOrWhiteSpace(candidate) || IsDefaultKeyword(candidate))
        {
            candidate = DefaultProviderId;
        }

        return candidate;
    }

    private bool TryGetCanonicalProvider(string providerHint, out string canonicalProvider)
    {
        foreach (var provider in _models.Keys)
        {
            if (provider.Equals(providerHint, StringComparison.OrdinalIgnoreCase))
            {
                canonicalProvider = provider;
                return true;
            }
        }

        canonicalProvider = string.Empty;
        return false;
    }
}
