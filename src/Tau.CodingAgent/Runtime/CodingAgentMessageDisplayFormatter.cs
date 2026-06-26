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
}
