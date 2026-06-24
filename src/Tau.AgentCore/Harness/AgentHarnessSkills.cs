using System.Text;

namespace Tau.AgentCore.Harness;

public sealed record AgentHarnessSkill(
    string Name,
    string Description,
    string Content,
    string FilePath,
    bool DisableModelInvocation = false);

public static class AgentHarnessSkills
{
    public static string FormatSkillInvocation(AgentHarnessSkill skill, string? additionalInstructions = null)
    {
        var skillBlock = $"""
            <skill name="{skill.Name}" location="{skill.FilePath}">
            References are relative to {Path.GetDirectoryName(skill.FilePath) ?? "."}.

            {skill.Content}
            </skill>
            """;

        return string.IsNullOrWhiteSpace(additionalInstructions)
            ? skillBlock
            : skillBlock + "\n\n" + additionalInstructions;
    }

    public static string FormatSkillsForSystemPrompt(IReadOnlyList<AgentHarnessSkill> skills)
    {
        var visibleSkills = skills
            .Where(static skill => !skill.DisableModelInvocation)
            .ToArray();
        if (visibleSkills.Length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("The following skills provide specialized instructions for specific tasks.");
        builder.AppendLine("Read the full skill file when the task matches its description.");
        builder.AppendLine("When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.");
        builder.AppendLine();
        builder.AppendLine("<available_skills>");

        foreach (var skill in visibleSkills)
        {
            builder.AppendLine("  <skill>");
            builder.Append("    <name>").Append(EscapeXml(skill.Name)).AppendLine("</name>");
            builder.Append("    <description>").Append(EscapeXml(skill.Description)).AppendLine("</description>");
            builder.Append("    <location>").Append(EscapeXml(skill.FilePath)).AppendLine("</location>");
            builder.AppendLine("  </skill>");
        }

        builder.Append("</available_skills>");
        return builder.ToString();
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
