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
