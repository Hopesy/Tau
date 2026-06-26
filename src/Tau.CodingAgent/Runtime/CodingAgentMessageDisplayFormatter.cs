using System.Text.RegularExpressions;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentParsedSkillBlock(
    string Name,
    string Location,
    string Content,
    string? UserMessage);

public sealed record CodingAgentDisplayedMessage(
    string Kind,
    string Text);

public static partial class CodingAgentMessageDisplayFormatter
{
    public const string BranchSummaryKind = "branch-summary";
    public const string CompactionSummaryKind = "compaction-summary";
    public const string CustomKind = "custom";
    public const string SkillKind = "skill";
    public const string UserKind = "user";

    public static CodingAgentDisplayedMessage FormatCustomMessage(
        AgentCustomMessage message,
        bool expanded = true)
    {
        ArgumentNullException.ThrowIfNull(message);

        var label = $"[{message.CustomType}]";
        if (!expanded)
        {
            return new CodingAgentDisplayedMessage(CustomKind, label);
        }

        var content = ExtractText(message.Content);
        return new CodingAgentDisplayedMessage(
            CustomKind,
            string.IsNullOrWhiteSpace(content) ? label : label + "\n" + content);
    }

    public static IReadOnlyList<CodingAgentDisplayedMessage> FormatUserMessage(
        string text,
        bool expanded = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (TryFormatCompactionSummary(text, expanded, out var compactionMessage))
        {
            return [compactionMessage];
        }

        if (TryFormatBranchSummary(text, expanded, out var branchMessage))
        {
            return [branchMessage];
        }

        if (!TryParseSkillBlock(text, out var skillBlock))
        {
            return [new CodingAgentDisplayedMessage(UserKind, text)];
        }

        var messages = new List<CodingAgentDisplayedMessage>
        {
            new(SkillKind, FormatSkillBlock(skillBlock, expanded))
        };
        if (!string.IsNullOrWhiteSpace(skillBlock.UserMessage))
        {
            messages.Add(new CodingAgentDisplayedMessage(UserKind, skillBlock.UserMessage));
        }

        return messages;
    }

    public static bool TryParseSkillBlock(
        string text,
        out CodingAgentParsedSkillBlock skillBlock)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var match = SkillBlockRegex().Match(normalized);
        if (!match.Success)
        {
            skillBlock = default!;
            return false;
        }

        var userMessage = match.Groups["userMessage"].Success
            ? match.Groups["userMessage"].Value.Trim()
            : null;
        skillBlock = new CodingAgentParsedSkillBlock(
            match.Groups["name"].Value,
            match.Groups["location"].Value,
            match.Groups["content"].Value,
            string.IsNullOrWhiteSpace(userMessage) ? null : userMessage);
        return true;
    }

    public static CodingAgentDisplayedMessage FormatCompactionSummary(
        CodingAgentCompactionResult result,
        bool expanded = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CodingAgentDisplayedMessage(
            CompactionSummaryKind,
            FormatCompactionSummaryText(result.Summary, result.TokensBefore, expanded));
    }

    public static CodingAgentDisplayedMessage FormatBranchSummary(
        CodingAgentBranchSummaryResult result,
        bool expanded = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CodingAgentDisplayedMessage(
            BranchSummaryKind,
            FormatBranchSummaryText(result.Summary, expanded));
    }

    public static bool TryFormatCompactionSummary(
        string text,
        bool expanded,
        out CodingAgentDisplayedMessage message)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryExtractCompactionSummary(text, out var summary))
        {
            message = default!;
            return false;
        }

        message = new CodingAgentDisplayedMessage(
            CompactionSummaryKind,
            FormatCompactionSummaryText(summary, tokensBefore: 0, expanded));
        return true;
    }

    public static bool TryFormatBranchSummary(
        string text,
        bool expanded,
        out CodingAgentDisplayedMessage message)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryExtractBranchSummary(text, out var summary))
        {
            message = default!;
            return false;
        }

        message = new CodingAgentDisplayedMessage(
            BranchSummaryKind,
            FormatBranchSummaryText(summary, expanded));
        return true;
    }

    private static string FormatSkillBlock(
        CodingAgentParsedSkillBlock skillBlock,
        bool expanded)
    {
        if (!expanded)
        {
            return $"[skill] {skillBlock.Name}";
        }

        return $"[skill] {skillBlock.Name}\n{skillBlock.Content}";
    }

    private static string FormatCompactionSummaryText(
        string summary,
        int tokensBefore,
        bool expanded)
    {
        var tokenText = tokensBefore > 0
            ? tokensBefore.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        if (!expanded)
        {
            return $"[compaction] Compacted from {tokenText} tokens";
        }

        return $"[compaction] Compacted from {tokenText} tokens\n{summary.Trim()}";
    }

    private static string FormatBranchSummaryText(string summary, bool expanded)
    {
        if (!expanded)
        {
            return "[branch] Branch summary";
        }

        return $"[branch] Branch summary\n{summary.Trim()}";
    }

    private static bool TryExtractCompactionSummary(string text, out string summary)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var prefix = CodingAgentCompactionMessages.SummaryPrefix.Replace("\r\n", "\n", StringComparison.Ordinal);
        var suffix = CodingAgentCompactionMessages.SummarySuffix.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            summary = string.Empty;
            return false;
        }

        var contentStart = prefix.Length;
        var contentEnd = normalized.IndexOf(suffix, contentStart, StringComparison.Ordinal);
        summary = contentEnd < 0
            ? normalized[contentStart..].Trim()
            : normalized[contentStart..contentEnd].Trim();
        return summary.Length > 0;
    }

    private static bool TryExtractBranchSummary(string text, out string summary)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var match = BranchSummaryRegex().Match(normalized);
        if (!match.Success)
        {
            summary = string.Empty;
            return false;
        }

        summary = match.Groups["summary"].Value.Trim();
        return summary.Length > 0;
    }

    private static string ExtractText(IReadOnlyList<ContentBlock> content)
    {
        if (content.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            content.Select(FormatBlock).Where(static text => text.Length > 0));
    }

    private static string FormatBlock(ContentBlock block) =>
        block switch
        {
            TextContent text => text.Text,
            ImageContent image => $"[image:{image.MimeType}]",
            ThinkingContent thinking => thinking.Thinking,
            ToolCallContent toolCall => $"[tool:{toolCall.Name}] {toolCall.Arguments}",
            _ => $"[{block.Type}]"
        };

    [GeneratedRegex(
        "^<skill name=\"(?<name>[^\"]+)\" location=\"(?<location>[^\"]+)\">\n(?<content>[\\s\\S]*?)\n</skill>(?:\n\n(?<userMessage>[\\s\\S]+))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SkillBlockRegex();

    [GeneratedRegex(
        "^Branch summary from (?<source>[^:]+):\n\n(?<summary>[\\s\\S]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BranchSummaryRegex();
}
