using System.Text;
using Tau.Ai;
using Tau.Tui.Components;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentFooterFormatter
{
    public static string FormatLeft(string? baseStatus, CodingAgentFooterDataProvider? footerDataProvider)
    {
        return FormatLeft(baseStatus, footerDataProvider, includeGitBranch: true);
    }

    public static string FormatDefaultLeft(
        string cwd,
        string? home,
        string? sessionName,
        CodingAgentFooterDataProvider? footerDataProvider)
    {
        var location = FormatLocation(cwd, home, sessionName, footerDataProvider);
        return FormatLeft(location, footerDataProvider, includeGitBranch: false);
    }

    public static IReadOnlyList<TuiStatusBarLine> FormatDefaultLines(
        string cwd,
        string? home,
        string? sessionName,
        Model model,
        ThinkingLevel? thinkingLevel,
        CodingAgentFooterDataProvider? footerDataProvider,
        CodingAgentSessionStats? stats = null,
        bool autoCompactEnabled = true)
    {
        var customFooterLines = FormatCustomFooterLines(footerDataProvider);
        if (customFooterLines.Count > 0)
        {
            return customFooterLines;
        }

        var lines = new List<TuiStatusBarLine>
        {
            new(FormatLocation(cwd, home, sessionName, footerDataProvider), string.Empty),
            new(
                FormatStats(stats, autoCompactEnabled),
                FormatModelRight(model, thinkingLevel, footerDataProvider))
        };

        var extensionStatuses = FormatExtensionStatuses(footerDataProvider);
        if (extensionStatuses.Length > 0)
        {
            lines.Add(new TuiStatusBarLine(extensionStatuses, string.Empty));
        }

        return lines;
    }

    public static string FormatLocation(
        string cwd,
        string? home,
        string? sessionName,
        CodingAgentFooterDataProvider? footerDataProvider)
    {
        var location = SanitizeStatusText(FormatCwdForFooter(cwd, home));
        if (location.Length == 0)
        {
            location = ".";
        }

        var branch = SanitizeStatusText(footerDataProvider?.GetGitBranch());
        if (branch.Length > 0)
        {
            location = $"{location} ({branch})";
        }

        var sanitizedSessionName = SanitizeStatusText(sessionName);
        if (sanitizedSessionName.Length > 0)
        {
            location = $"{location} • {sanitizedSessionName}";
        }

        return location;
    }

    public static string FormatCwdForFooter(string cwd, string? home)
    {
        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(home))
        {
            return cwd;
        }

        string resolvedCwd;
        string resolvedHome;
        try
        {
            resolvedCwd = Path.GetFullPath(cwd);
            resolvedHome = Path.GetFullPath(home);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return cwd;
        }

        string relativeToHome;
        try
        {
            relativeToHome = Path.GetRelativePath(resolvedHome, resolvedCwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return cwd;
        }

        if (relativeToHome == ".")
        {
            return "~";
        }

        var isInsideHome = relativeToHome.Length > 0 &&
            !Path.IsPathRooted(relativeToHome) &&
            !relativeToHome.Equals("..", StringComparison.Ordinal) &&
            !relativeToHome.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relativeToHome.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        return isInsideHome
            ? $"~{Path.DirectorySeparatorChar}{relativeToHome}"
            : cwd;
    }

    private static string FormatLeft(
        string? baseStatus,
        CodingAgentFooterDataProvider? footerDataProvider,
        bool includeGitBranch)
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

        if (includeGitBranch)
        {
            var branch = SanitizeStatusText(footerDataProvider.GetGitBranch());
            if (branch.Length > 0)
            {
                left = $"{left} ({branch})";
            }
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
        var right = FormatModelRight(model, thinkingLevel, footerDataProvider);
        var statsText = FormatStats(stats, autoCompactEnabled);
        return statsText.Length == 0
            ? right
            : $"{statsText} {right}";
    }

    public static string FormatModelRight(
        Model model,
        ThinkingLevel? thinkingLevel,
        CodingAgentFooterDataProvider? footerDataProvider)
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

        return footerDataProvider?.GetAvailableProviderCount() > 1
            ? $"({SanitizeStatusText(model.Provider)}) {modelText}"
            : modelText;
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

    private static string FormatExtensionStatuses(CodingAgentFooterDataProvider? footerDataProvider)
    {
        if (footerDataProvider is null)
        {
            return string.Empty;
        }

        var extensionStatuses = footerDataProvider
            .GetExtensionStatuses()
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => SanitizeStatusText(pair.Value))
            .Where(static text => text.Length > 0)
            .ToArray();
        return string.Join(' ', extensionStatuses);
    }

    private static IReadOnlyList<TuiStatusBarLine> FormatCustomFooterLines(CodingAgentFooterDataProvider? footerDataProvider)
    {
        if (footerDataProvider is null)
        {
            return [];
        }

        var lines = footerDataProvider
            .GetCustomFooterLines()
            ?.Select(SanitizeStatusText)
            .Where(static text => text.Length > 0)
            .Select(static text => new TuiStatusBarLine(text, string.Empty))
            .ToArray();
        return lines is { Length: > 0 } ? lines : [];
    }
}
