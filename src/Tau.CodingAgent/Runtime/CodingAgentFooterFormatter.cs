using System.Text;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentFooterFormatter
{
    public static string FormatLeft(string? baseStatus, CodingAgentFooterDataProvider? footerDataProvider)
    {
        var left = SanitizeStatusText(baseStatus);
        if (left.Length == 0)
        {
            left = "ready";
        }

        if (footerDataProvider is null)
        {
            return left;
        }

        var branch = SanitizeStatusText(footerDataProvider.GetGitBranch());
        if (branch.Length > 0)
        {
            left = $"{left} ({branch})";
        }

        var extensionStatuses = footerDataProvider
            .GetExtensionStatuses()
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => SanitizeStatusText(pair.Value))
            .Where(static text => text.Length > 0)
            .ToArray();
        return extensionStatuses.Length == 0
            ? left
            : $"{left} | {string.Join(' ', extensionStatuses)}";
    }

    public static string FormatRight(
        Model model,
        ThinkingLevel? thinkingLevel,
        CodingAgentFooterDataProvider? footerDataProvider,
        CodingAgentSessionStats? stats = null,
        bool autoCompactEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(model);

        var modelText = SanitizeStatusText(model.Id);
        if (modelText.Length == 0)
        {
            modelText = "no-model";
        }

        if (model.Reasoning)
        {
            modelText = $"{modelText} (thinking {CodingAgentThinkingLevels.Format(thinkingLevel)})";
        }

        var right = footerDataProvider?.GetAvailableProviderCount() > 1
            ? $"({SanitizeStatusText(model.Provider)}) {modelText}"
            : modelText;
        var statsText = FormatStats(stats, autoCompactEnabled);
        return statsText.Length == 0
            ? right
            : $"{statsText} {right}";
    }

    public static string FormatStats(CodingAgentSessionStats? stats, bool autoCompactEnabled = true)
    {
        if (stats is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (stats.Tokens.Input > 0)
        {
            parts.Add($"in{FormatTokens(stats.Tokens.Input)}");
        }

        if (stats.Tokens.Output > 0)
        {
            parts.Add($"out{FormatTokens(stats.Tokens.Output)}");
        }

        if (stats.Tokens.CacheRead > 0)
        {
            parts.Add($"R{FormatTokens(stats.Tokens.CacheRead)}");
        }

        if (stats.Tokens.CacheWrite > 0)
        {
            parts.Add($"W{FormatTokens(stats.Tokens.CacheWrite)}");
        }

        if (stats.CostRecords > 0)
        {
            parts.Add($"${stats.Cost.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        var context = FormatContext(stats, autoCompactEnabled);
        if (context.Length > 0)
        {
            parts.Add(context);
        }

        return string.Join(' ', parts);
    }

    internal static string FormatTokens(int count)
    {
        count = Math.Max(0, count);
        if (count < 1000)
        {
            return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (count < 10_000)
        {
            return $"{(count / 1000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}k";
        }

        if (count < 1_000_000)
        {
            return $"{(int)Math.Round(count / 1000d)}k";
        }

        if (count < 10_000_000)
        {
            return $"{(count / 1_000_000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}M";
        }

        return $"{(int)Math.Round(count / 1_000_000d)}M";
    }

    private static string FormatContext(CodingAgentSessionStats stats, bool autoCompactEnabled)
    {
        var contextWindow = stats.ContextWindowTokens.GetValueOrDefault();
        var estimate = Math.Max(0, stats.EstimatedTokens);
        if (contextWindow <= 0)
        {
            return estimate > 0 ? $"~{FormatTokens(estimate)}" : string.Empty;
        }

        var percent = Math.Clamp((double)estimate / contextWindow * 100d, 0d, 999.9d);
        var suffix = autoCompactEnabled ? "(auto)" : string.Empty;
        return $"{percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%/{FormatTokens(contextWindow)}{suffix}";
    }

    internal static string SanitizeStatusText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }
}
