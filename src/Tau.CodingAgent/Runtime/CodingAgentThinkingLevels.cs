using Tau.Ai;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentThinkingLevels
{
    public static readonly IReadOnlyList<string> DefaultLevels =
        ["off", "minimal", "low", "medium", "high", "xhigh"];

    private static readonly IReadOnlyList<string> LevelsWithoutXhigh =
        ["off", "minimal", "low", "medium", "high"];

    private static readonly IReadOnlyList<string> OffOnlyLevels = ["off"];

    public static IReadOnlyList<string> AvailableForModel(Model model)
    {
        if (!model.Reasoning)
        {
            return OffOnlyLevels;
        }

        return ModelCatalog.SupportsXhigh(model)
            ? DefaultLevels
            : LevelsWithoutXhigh;
    }

    public static bool TryParse(string? value, out ThinkingLevel? level)
    {
        level = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
            case "none":
                return true;
            case "minimal":
                level = ThinkingLevel.Minimal;
                return true;
            case "low":
                level = ThinkingLevel.Low;
                return true;
            case "medium":
            case "med":
                level = ThinkingLevel.Medium;
                return true;
            case "high":
                level = ThinkingLevel.High;
                return true;
            case "xhigh":
            case "extrahigh":
            case "extra-high":
                level = ThinkingLevel.ExtraHigh;
                return true;
            default:
                return false;
        }
    }

    public static ThinkingLevel? ParseOrNull(string? value) =>
        TryParse(value, out var level) ? level : null;

    public static ThinkingLevel? ClampForModel(Model model, ThinkingLevel? requested)
    {
        if (requested is null)
        {
            return null;
        }

        if (!model.Reasoning)
        {
            return null;
        }

        return requested == ThinkingLevel.ExtraHigh && !ModelCatalog.SupportsXhigh(model)
            ? ThinkingLevel.High
            : requested;
    }

    public static ThinkingLevel? CycleForModel(Model model, ThinkingLevel? current)
    {
        if (!model.Reasoning)
        {
            return null;
        }

        var clamped = ClampForModel(model, current);
        return clamped switch
        {
            null => ThinkingLevel.Low,
            ThinkingLevel.Minimal => ThinkingLevel.Low,
            ThinkingLevel.Low => ThinkingLevel.Medium,
            ThinkingLevel.Medium => ThinkingLevel.High,
            ThinkingLevel.High => ModelCatalog.SupportsXhigh(model) ? ThinkingLevel.ExtraHigh : null,
            ThinkingLevel.ExtraHigh => null,
            _ => ThinkingLevel.Low
        };
    }

    public static string Format(ThinkingLevel? level) => level switch
    {
        null => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        _ => "off"
    };

    public static string? FormatRaw(ThinkingLevel? level) =>
        level is null ? null : Format(level);
}
