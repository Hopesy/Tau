using System.Text;

namespace Tau.Mom;

internal sealed record ChannelWorkspaceLayout(
    string WorkspaceDirectory,
    string ChannelDirectory,
    string WorkspaceMemoryPath,
    string ChannelMemoryPath,
    string ChannelLogPath,
    string ChannelStatusPath,
    string ChannelContextPath,
    string PromptDebugPath,
    string AttachmentsDirectory,
    string AttachmentManifestPath,
    string ScratchDirectory,
    string WorkspaceSkillsDirectory,
    string ChannelSkillsDirectory,
    string SystemLogPath,
    string EventsDirectory)
{
    public static ChannelWorkspaceLayout For(MomOptions options, string workingDirectory)
    {
        var channelDirectory = Path.GetFullPath(workingDirectory);
        var workspaceDirectory = Directory.GetParent(channelDirectory)?.FullName ?? channelDirectory;
        var attachmentsDirectory = Path.Combine(channelDirectory, "attachments");
        return new ChannelWorkspaceLayout(
            workspaceDirectory,
            channelDirectory,
            Path.Combine(workspaceDirectory, "MEMORY.md"),
            Path.Combine(channelDirectory, "MEMORY.md"),
            Path.Combine(channelDirectory, "log.jsonl"),
            Path.Combine(channelDirectory, "status.json"),
            Path.Combine(channelDirectory, ChannelSessionStore.ContextFileName),
            Path.Combine(channelDirectory, ChannelPromptDebugStore.PromptFileName),
            attachmentsDirectory,
            Path.Combine(attachmentsDirectory, "attachments.jsonl"),
            Path.Combine(channelDirectory, "scratch"),
            Path.Combine(workspaceDirectory, "skills"),
            Path.Combine(channelDirectory, "skills"),
            Path.Combine(workspaceDirectory, "SYSTEM.md"),
            Path.GetFullPath(options.EventsPath));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(WorkspaceDirectory);
        Directory.CreateDirectory(ChannelDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(ScratchDirectory);
        Directory.CreateDirectory(WorkspaceSkillsDirectory);
        Directory.CreateDirectory(ChannelSkillsDirectory);
        Directory.CreateDirectory(EventsDirectory);
    }

    public string? ReadSystemLog()
    {
        return ReadTextFile(SystemLogPath);
    }

    public string BuildSkillInventory(Func<string, string>? mapPath = null)
    {
        var entries = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);
        AddSkills(entries, WorkspaceSkillsDirectory, "workspace");
        AddSkills(entries, ChannelSkillsDirectory, "channel");

        if (entries.Count == 0)
        {
            return "(no skills installed yet)";
        }

        var builder = new StringBuilder();
        builder.AppendLine("The following skills provide specialized instructions for specific tasks.");
        builder.AppendLine("Use the read tool to load a skill's file when the task matches its description.");
        builder.AppendLine("When a skill file references a relative path, resolve it against the skill directory and use that path in bash commands.");
        builder.AppendLine("<available_skills>");

        foreach (var entry in entries.Values
            .Where(static skill => !skill.DisableModelInvocation)
            .OrderBy(static skill => skill.Source, StringComparer.Ordinal)
            .ThenBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            var location = mapPath is null ? entry.Path : mapPath(entry.Path);
            builder.AppendLine("  <skill>");
            builder.Append("    <name>").Append(EscapeXml(entry.Name)).AppendLine("</name>");
            builder.Append("    <description>").Append(EscapeXml(entry.Description)).AppendLine("</description>");
            builder.Append("    <source>").Append(EscapeXml(entry.Source)).AppendLine("</source>");
            builder.Append("    <location>").Append(EscapeXml(location)).AppendLine("</location>");
            builder.AppendLine("  </skill>");
        }

        builder.Append("</available_skills>");

        return builder.ToString().TrimEnd();
    }

    private static void AddSkills(IDictionary<string, SkillEntry> entries, string directory, string source)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var skillFile in Directory.EnumerateFiles(directory, "SKILL.md", SearchOption.AllDirectories))
            {
                if (TryReadSkill(skillFile, source) is { } skill)
                {
                    entries[skill.Name] = skill;
                }
            }
        }
        catch
        {
            // Skill docs are optional local context. Ignore unreadable trees and keep delegation running.
        }
    }

    private static SkillEntry? TryReadSkill(string path, string source)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        var name = Path.GetFileName(Path.GetDirectoryName(path)) ?? "skill";
        var description = "No description.";
        var disableModelInvocation = false;

        using var reader = new StringReader(content);
        if (string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.Trim();
                if (string.Equals(trimmed, "---", StringComparison.Ordinal))
                {
                    break;
                }

                var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var key = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    name = value;
                }
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    description = value;
                }
                else if (key.Equals("disable-model-invocation", StringComparison.OrdinalIgnoreCase) &&
                         bool.TryParse(value, out var disabled))
                {
                    disableModelInvocation = disabled;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(description) || description.Equals("No description.", StringComparison.Ordinal))
        {
            return null;
        }

        return new SkillEntry(name, description, source, path, disableModelInvocation);
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static string? ReadTextFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    private sealed record SkillEntry(string Name, string Description, string Source, string Path, bool DisableModelInvocation);
}
