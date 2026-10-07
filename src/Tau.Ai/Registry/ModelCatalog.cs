using Tau.Ai.Auth;

namespace Tau.Ai.Registry;

public readonly record struct ResolvedModelSelection(string Provider, string ModelId)
{
    public string CanonicalReference => $"{Provider}/{ModelId}";
}

public sealed partial class ModelCatalog
{
    private const string DefaultProviderId = "openai";
    private static readonly IReadOnlySet<string> RetiredXaiModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "grok-3",
        "grok-3-fast",
        "grok-4.20-0309-non-reasoning",
        "grok-4.20-0309-reasoning",
        "grok-code-fast-1"
    };
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
        ["cloudflare-ai-gateway"] = "claude-sonnet-4-6",
        ["cloudflare-workers-ai"] = "@cf/meta/llama-4-scout-17b-16e-instruct",
        ["deepseek"] = "deepseek-v4-pro",
        ["fireworks"] = "accounts/fireworks/models/kimi-k2p6",
        ["huggingface"] = "moonshotai/Kimi-K2-Instruct",
        ["moonshotai"] = "kimi-k2-thinking-turbo",
        ["moonshotai-cn"] = "kimi-k2-thinking-turbo",
        ["nvidia"] = "nvidia/nemotron-3-ultra-550b-a55b",
        ["opencode"] = "claude-sonnet-4-6",
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
        , ["baseten"] = "deepseek-ai/DeepSeek-V3"
        , ["kimi-coding"] = "kimi-k2.5"
        , ["minimax"] = "MiniMax-M2.5"
        , ["minimax-cn"] = "MiniMax-M2.5"
        , ["opencode-go"] = "kimi-k2.5"
        , ["qwen-token-plan"] = "qwen3-coder-plus"
        , ["qwen-token-plan-cn"] = "qwen3-coder-plus"
        , ["qwen-token-plan-individual"] = "qwen3-coder-plus"
        , ["vercel-ai-gateway"] = "anthropic/claude-sonnet-4"
        , ["radius"] = "default"
    };

    private volatile Dictionary<string, Dictionary<string, Model>> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProviderAuthResolver _authResolver;

    /// <summary>【AI】【模型配置】构建目录时使用的配置，请求必须复用同一来源。</summary>
    public ModelConfigurationStore ConfigurationStore { get; }

    /// <summary>【AI】【模型认证】与目录绑定的认证解析器，供会话和请求复用。</summary>
    public ProviderAuthResolver AuthResolver => _authResolver;

    /// <summary>【AI】【模型目录】使用同一配置和认证上下文构建模型目录。</summary>
    /// <param name="authResolver">可选认证解析器。</param>
    /// <param name="configurationStore">可选模型配置来源。</param>
    public ModelCatalog(ProviderAuthResolver? authResolver = null, ModelConfigurationStore? configurationStore = null)
    {
        ConfigurationStore = configurationStore ?? new ModelConfigurationStore();
        _authResolver = authResolver ?? new ProviderAuthResolver(configurationStore: ConfigurationStore);

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

        ConfigurationStore.ApplyTo(_models);
        _capabilityModels = CreateCapabilityModels();
    }

    public IReadOnlyList<string> GetProviders() => [.. _models.Keys.Concat(_virtualModels.Select(model => model.Provider)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<Model> GetModels(string provider) => GetModelsOfType(ModelTypes.Chat, provider);

    public Model GetModel(string provider, string modelId)
    {
        var routed = _virtualModels.FirstOrDefault(model => model.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (routed is not null) return routed;
        if (!_models.TryGetValue(provider, out var models) || !models.TryGetValue(modelId, out var model))
        {
            throw new KeyNotFoundException($"Model '{provider}/{modelId}' is not registered.");
        }

        return _authResolver.ResolveModel(model);
    }

    public Model? TryGetModel(string provider, string modelId)
    {
        var routed = _virtualModels.FirstOrDefault(model => model.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (routed is not null) return routed;
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
            var preferred = GetDefaultModelId(resolvedProvider);
            var available = GetModels(resolvedProvider);
            return new ResolvedModelSelection(resolvedProvider, available.FirstOrDefault(model => model.Id == preferred)?.Id
                ?? available.FirstOrDefault()?.Id ?? throw new KeyNotFoundException($"Provider '{resolvedProvider}' has no chat models."));
        }

        if (TryGetModel(resolvedProvider, normalizedModelHint) is not null)
        {
            return new ResolvedModelSelection(resolvedProvider, normalizedModelHint);
        }

        if (string.IsNullOrWhiteSpace(normalizedProviderHint))
        {
            var exactMatches = GetProviders()
                .Where(provider => TryGetModel(provider, normalizedModelHint) is not null)
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

    /// <summary>【AI】【模型注册】登记 SDK 模型，按类型隔离并重新应用当前扩展覆盖。</summary>
    /// <param name="model">聊天、图像或分类模型。</param>
    public void RegisterModel(Model model)
    {
        lock (_catalogGate)
        {
            if (ModelTypes.GetModelType(model) != ModelTypes.Chat)
            {
                if (!ModelTypes.IsKnown(model)) throw new ArgumentException($"Unknown model type: {model.Type}", nameof(model));
                _customCapabilityModels = ReplaceCapabilityModel(_customCapabilityModels, model);
                if (_runtimeCapabilityBaseline is not null)
                {
                    _runtimeCapabilityBaseline = ReplaceCapabilityModel(_runtimeCapabilityBaseline, model);
                    SetRuntimeProviders(_runtimeDefinitions);
                }
                else _capabilityModels = ReplaceCapabilityModel(_capabilityModels, model);
                return;
            }
            var next = CopyModels(_models);
            if (!next.TryGetValue(model.Provider, out var models)) next[model.Provider] = models = new(StringComparer.OrdinalIgnoreCase);
            models[model.Id] = model;
            if (!_customModels.TryGetValue(model.Provider, out var custom)) _customModels[model.Provider] = custom = new(StringComparer.OrdinalIgnoreCase);
            custom[model.Id] = model;
            _models = next;
            if (_runtimeBaseline is not null)
            {
                if (!_runtimeBaseline.TryGetValue(model.Provider, out var baseline)) _runtimeBaseline[model.Provider] = baseline = new(StringComparer.OrdinalIgnoreCase);
                baseline[model.Id] = model;
                SetRuntimeProviders(_runtimeDefinitions);
            }
        }
    }

    /// <summary>【AI】【模型注册】替换同类型、同提供方和同标识的单个模型。</summary>
    /// <param name="models">原始目录。</param><param name="model">新模型。</param><returns>独立目录。</returns>
    private static IReadOnlyList<Model> ReplaceCapabilityModel(IReadOnlyList<Model> models, Model model) =>
        [.. models.Where(item => ModelTypes.GetModelType(item) != ModelTypes.GetModelType(model) ||
            !item.Provider.Equals(model.Provider, StringComparison.OrdinalIgnoreCase) || !item.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase)), model];

    public static string GetDefaultProviderId() => DefaultProviderId;

    public static string GetDefaultModelId(string providerId)
    {
        if (DefaultModelIds.TryGetValue(providerId, out var modelId))
        {
            return modelId;
        }

        return DefaultModelIds[DefaultProviderId];
    }

    /// <summary>
    /// 判断模型是否只用于兼容旧配置，不应出现在正常的模型选择列表中。
    /// </summary>
    /// <param name="model">待判断的模型。</param>
    /// <returns>模型属于已退休 xAI 模型时返回 true，否则返回 false。</returns>
    private static bool IsRetiredModel(Model model) =>
        model.Provider.Equals("xai", StringComparison.OrdinalIgnoreCase) &&
        RetiredXaiModelIds.Contains(model.Id);

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

        // 1. 按本次请求的输入总量选择最高匹配的计费层
        var baseCost = model.Cost.Value;
        var inputTotal = (long)usage.InputTokens + usage.CacheReadTokens.GetValueOrDefault() + usage.CacheWriteTokens.GetValueOrDefault();
        var inputRate = baseCost.InputPerMillion;
        var outputRate = baseCost.OutputPerMillion;
        var cacheReadRate = baseCost.CacheReadPerMillion.GetValueOrDefault();
        var cacheWriteRate = baseCost.CacheWritePerMillion.GetValueOrDefault();
        var matchedThreshold = -1d;
        foreach (var tier in baseCost.Tiers ?? [])
        {
            if (inputTotal > tier.InputTokensAbove && tier.InputTokensAbove > matchedThreshold)
            {
                inputRate = tier.InputPerMillion;
                outputRate = tier.OutputPerMillion;
                cacheReadRate = tier.CacheReadPerMillion.GetValueOrDefault();
                cacheWriteRate = tier.CacheWritePerMillion.GetValueOrDefault();
                matchedThreshold = tier.InputTokensAbove;
            }
        }

        // 2. Anthropic 1h cache 写入按基础输入价格的两倍计费，其余写入使用缓存写入价格
        var longCacheWrite = usage.CacheWrite1hTokens.GetValueOrDefault();
        var shortCacheWrite = Math.Max(0, usage.CacheWriteTokens.GetValueOrDefault() - longCacheWrite);
        var cost = new UsageCost(
            Input: usage.InputTokens / 1_000_000m * inputRate,
            Output: usage.OutputTokens / 1_000_000m * outputRate,
            CacheRead: usage.CacheReadTokens.GetValueOrDefault() / 1_000_000m * cacheReadRate,
            CacheWrite: (shortCacheWrite * cacheWriteRate + longCacheWrite * inputRate * 2m) / 1_000_000m);
        return ApplyServiceTierMultiplier(cost, usage.ServiceTier);
    }

    /// <summary>
    /// 返回模型支持的 thinking level 列表，始终包含 off（不支持推理的模型只返回 off）。
    /// </summary>
    /// <param name="model">待检查的模型。</param>
    /// <returns>按从低到高排列的 level 名称。</returns>
    public static IReadOnlyList<string> GetSupportedThinkingLevels(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Reasoning)
        {
            return ["off"];
        }

        var levels = new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" };
        var result = new List<string>(levels.Length);
        foreach (var level in levels)
        {
            if (model.ThinkingLevelMap is not null &&
                model.ThinkingLevelMap.TryGetValue(level, out var mapped) &&
                mapped is null)
            {
                continue;
            }

            if ((level is "xhigh" or "max") &&
                (model.ThinkingLevelMap is null || !model.ThinkingLevelMap.ContainsKey(level)))
            {
                continue;
            }

            result.Add(level);
        }

        return result.Count == 0 ? ["off"] : result;
    }

    /// <summary>
    /// 将不受模型支持的 thinking level 钳制到最接近的可用级别。
    /// </summary>
    /// <param name="model">待匹配的模型。</param>
    /// <param name="level">请求的 thinking level。</param>
    /// <returns>模型支持的 level；没有可用值时返回 off。</returns>
    public static string ClampThinkingLevel(Model model, string level)
    {
        ArgumentNullException.ThrowIfNull(model);
        var available = GetSupportedThinkingLevels(model);
        if (available.Contains(level, StringComparer.OrdinalIgnoreCase))
        {
            return available.First(candidate => candidate.Equals(level, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" };
        var requested = Array.FindIndex(ordered, candidate => candidate.Equals(level, StringComparison.OrdinalIgnoreCase));
        if (requested < 0)
        {
            return available[0];
        }

        for (var index = requested; index < ordered.Length; index++)
        {
            var candidate = available.FirstOrDefault(item => item.Equals(ordered[index], StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return candidate;
            }
        }

        for (var index = requested - 1; index >= 0; index--)
        {
            var candidate = available.FirstOrDefault(item => item.Equals(ordered[index], StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return available[0];
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
        if (model.ThinkingLevelMap?.TryGetValue("xhigh", out var mapped) == true) return mapped is not null;
        return model.Id.Contains("gpt-5.2", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("gpt-5.3", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus-4-7", StringComparison.OrdinalIgnoreCase) ||
               model.Id.Contains("opus-4.7", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>【AI】【模型类型】比较模型的提供方、标识和能力类型。</summary>
    /// <param name="left">第一个模型。</param>
    /// <param name="right">第二个模型。</param>
    /// <returns>两者均存在且标识与类型一致时返回 true。</returns>
    public static bool ModelsAreEqual(Model? left, Model? right)
    {
        return left is not null &&
               right is not null &&
               left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase) &&
               left.Provider.Equals(right.Provider, StringComparison.OrdinalIgnoreCase) &&
               ModelTypes.GetModelType(left) == ModelTypes.GetModelType(right);
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
        foreach (var provider in GetProviders())
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
