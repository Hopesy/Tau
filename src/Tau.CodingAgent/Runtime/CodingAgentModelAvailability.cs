using Tau.Ai;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentModelAvailability
{
    public static IReadOnlyList<Model> GetRegisteredModels(ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var models = new List<Model>();
        foreach (var provider in catalog.GetProviders())
        {
            models.AddRange(catalog.GetModels(provider));
        }

        return models;
    }

    public static IReadOnlyList<Model> GetRegisteredModels(ICodingAgentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var models = new List<Model>();
        foreach (var provider in runner.GetProviders())
        {
            models.AddRange(runner.GetModels(provider));
        }

        return models;
    }

    public static IReadOnlyList<Model> GetAuthConfiguredModels(
        ICodingAgentRunner runner,
        IReadOnlyList<Model>? registeredModels = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var source = registeredModels ?? GetRegisteredModels(runner);
        var providerStatuses = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var models = new List<Model>();
        foreach (var model in source)
        {
            if (!providerStatuses.TryGetValue(model.Provider, out var isConfigured))
            {
                isConfigured = runner.GetAuthStatus(model.Provider).IsConfigured;
                providerStatuses[model.Provider] = isConfigured;
            }

            if (isConfigured)
            {
                models.Add(model);
            }
        }

        return models;
    }

    public static string FormatModelId(Model model) => $"{model.Provider}/{model.Id}";

    public static bool TryResolveScopedModelEntries(
        IReadOnlyList<string>? patterns,
        IReadOnlyList<Model> registeredModels,
        out IReadOnlyList<CodingAgentScopedModelEntry> entries,
        out string? error)
    {
        entries = [];
        error = null;
        if (patterns is null || patterns.Count == 0)
        {
            return true;
        }

        var resolved = new List<CodingAgentScopedModelEntry>();
        foreach (var pattern in patterns)
        {
            var trimmed = pattern.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (TryResolveWildcardScopedModelEntries(trimmed, registeredModels, resolved, out error))
            {
                continue;
            }

            if (error is not null)
            {
                return false;
            }

            if (!CodingAgentScopedModelPatterns.TryResolve(trimmed, registeredModels, out var entry, out error))
            {
                return false;
            }

            if (!resolved.Any(existing => SameModel(existing.Model, entry.Model)))
            {
                resolved.Add(entry);
            }
        }

        if (resolved.Count == 0)
        {
            error = "model scope did not resolve any registered models";
            return false;
        }

        entries = resolved;
        return true;
    }

    public static List<CodingAgentScopedModelEntry> GetModelCycleCandidates(
        IReadOnlyList<string>? enabledModels,
        IReadOnlyList<Model> registeredModels,
        IReadOnlyList<Model> authConfiguredModels,
        out bool isScoped)
    {
        if (enabledModels is null || enabledModels.Count == 0)
        {
            isScoped = false;
            return authConfiguredModels
                .Select(static model => new CodingAgentScopedModelEntry(model, null))
                .ToList();
        }

        var candidates = new List<CodingAgentScopedModelEntry>();
        foreach (var enabledModel in enabledModels)
        {
            if (!CodingAgentScopedModelPatterns.TryResolve(enabledModel, registeredModels, out var entry, out _))
            {
                continue;
            }

            var model = authConfiguredModels.FirstOrDefault(candidate => SameModel(candidate, entry.Model));
            if (model is not null && !candidates.Any(candidate => SameModel(candidate.Model, model)))
            {
                candidates.Add(new CodingAgentScopedModelEntry(model, entry.ThinkingLevel));
            }
        }

        if (candidates.Count == 0)
        {
            isScoped = false;
            return authConfiguredModels
                .Select(static model => new CodingAgentScopedModelEntry(model, null))
                .ToList();
        }

        isScoped = true;
        return candidates;
    }

    private static bool SameModel(Model left, Model right) =>
        left.Provider.Equals(right.Provider, StringComparison.OrdinalIgnoreCase) &&
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveWildcardScopedModelEntries(
        string pattern,
        IReadOnlyList<Model> registeredModels,
        List<CodingAgentScopedModelEntry> resolved,
        out string? error)
    {
        error = null;
        var modelPattern = pattern;
        string? thinkingLevel = null;
        var lastColonIndex = pattern.LastIndexOf(':');
        if (lastColonIndex > 0 && lastColonIndex < pattern.Length - 1 &&
            CodingAgentScopedModelPatterns.TryNormalizeThinkingLevel(pattern[(lastColonIndex + 1)..], out var normalizedThinking))
        {
            modelPattern = pattern[..lastColonIndex].Trim();
            thinkingLevel = normalizedThinking;
        }

        if (!modelPattern.Contains('*', StringComparison.Ordinal) &&
            !modelPattern.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        var matches = registeredModels
            .Where(model => MatchesModelPattern(modelPattern, model))
            .ToArray();
        if (matches.Length == 0)
        {
            error = $"model pattern '{pattern}' did not match any registered models";
            return false;
        }

        foreach (var model in matches)
        {
            if (!resolved.Any(existing => SameModel(existing.Model, model)))
            {
                resolved.Add(new CodingAgentScopedModelEntry(model, thinkingLevel));
            }
        }

        return true;
    }

    private static bool MatchesModelPattern(string pattern, Model model)
    {
        var fullId = FormatModelId(model);
        if (WildcardMatch(pattern, fullId))
        {
            return true;
        }

        return !pattern.Contains('/', StringComparison.Ordinal) && WildcardMatch(pattern, model.Id);
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;
        var normalizedPattern = pattern.ToLowerInvariant();
        var normalizedValue = value.ToLowerInvariant();

        while (valueIndex < normalizedValue.Length)
        {
            if (patternIndex < normalizedPattern.Length &&
                (normalizedPattern[patternIndex] == '?' ||
                 normalizedPattern[patternIndex] == normalizedValue[valueIndex]))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < normalizedPattern.Length && normalizedPattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                matchIndex = valueIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++matchIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < normalizedPattern.Length && normalizedPattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == normalizedPattern.Length;
    }
}
