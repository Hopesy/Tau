using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

internal readonly record struct CodingAgentScopedModelEntry(Model Model, string? ThinkingLevel)
{
    public string ModelId => CodingAgentScopedModelPatterns.FormatModelId(Model);
    public string Pattern => CodingAgentScopedModelPatterns.FormatPattern(Model, ThinkingLevel);
}

internal static class CodingAgentScopedModelPatterns
{
    public static bool TryResolve(
        string reference,
        IReadOnlyList<Model> availableModels,
        out CodingAgentScopedModelEntry entry,
        out string? error)
    {
        entry = default;
        var trimmed = reference.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "model reference cannot be empty";
            return false;
        }

        if (TryResolveModelReference(trimmed, availableModels, out var exactMatch, out error))
        {
            entry = new CodingAgentScopedModelEntry(exactMatch, null);
            return true;
        }

        var lastColonIndex = trimmed.LastIndexOf(':');
        if (lastColonIndex <= 0 || lastColonIndex == trimmed.Length - 1)
        {
            return false;
        }

        var suffix = trimmed[(lastColonIndex + 1)..].Trim();
        if (!TryNormalizeThinkingLevel(suffix, out var thinkingLevel))
        {
            return false;
        }

        var modelReference = trimmed[..lastColonIndex].Trim();
        if (!TryResolveModelReference(modelReference, availableModels, out var model, out error))
        {
            return false;
        }

        entry = new CodingAgentScopedModelEntry(model, thinkingLevel);
        return true;
    }

    public static string FormatPattern(Model model, string? thinkingLevel)
    {
        var id = FormatModelId(model);
        return string.IsNullOrWhiteSpace(thinkingLevel) ? id : $"{id}:{thinkingLevel}";
    }

    public static string FormatModelId(Model model) => $"{model.Provider}/{model.Id}";

    public static bool SameModel(Model left, Model right) =>
        left.Provider.Equals(right.Provider, StringComparison.OrdinalIgnoreCase) &&
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase);

    public static ThinkingLevel? ParseThinkingLevelOrNull(string value)
    {
        return CodingAgentThinkingLevels.ParseOrNull(value);
    }

    public static bool TryNormalizeThinkingLevel(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        normalized = value.Trim().ToLowerInvariant() switch
        {
            "off" or "none" => "off",
            "minimal" => "minimal",
            "low" => "low",
            "medium" or "med" => "medium",
            "high" => "high",
            "xhigh" or "extrahigh" or "extra-high" => "xhigh",
            _ => string.Empty
        };
        return normalized.Length > 0;
    }

    private static bool TryResolveModelReference(
        string reference,
        IReadOnlyList<Model> availableModels,
        out Model model,
        out string? error)
    {
        model = null!;
        error = null;
        var trimmed = reference.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "model reference cannot be empty";
            return false;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var parts = trimmed.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                error = $"model '{reference}' is not registered";
                return false;
            }

            var match = availableModels.SingleOrDefault(candidate =>
                candidate.Provider.Equals(parts[0], StringComparison.OrdinalIgnoreCase) &&
                candidate.Id.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"model '{reference}' is not registered";
                return false;
            }

            model = match;
            return true;
        }

        var matches = availableModels
            .Where(candidate => candidate.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            error = $"model '{reference}' is not registered";
            return false;
        }

        if (matches.Length > 1)
        {
            error = $"model '{reference}' is ambiguous; use provider/model";
            return false;
        }

        model = matches[0];
        return true;
    }
}
