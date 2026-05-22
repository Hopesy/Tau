using System.Text;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSkill(
    string Name,
    string Description,
    string Content,
    string FilePath,
    string BaseDirectory,
    string Scope,
    bool DisableModelInvocation);

public sealed class CodingAgentSkillStore
{
    public const string SkillPathsEnvironmentVariable = "TAU_CODING_AGENT_SKILL_PATHS";

    private readonly string _cwd;
    private readonly string _userSkillsDirectory;
    private readonly IReadOnlyList<string> _explicitPaths;
    private readonly Func<IReadOnlyList<string>>? _additionalPathsProvider;
    private readonly bool _includeDefaults;

    public CodingAgentSkillStore(
        string? cwd = null,
        string? userSkillsDirectory = null,
        IReadOnlyList<string>? explicitPaths = null,
        Func<IReadOnlyList<string>>? additionalPathsProvider = null,
        bool includeDefaults = true)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _userSkillsDirectory = string.IsNullOrWhiteSpace(userSkillsDirectory)
            ? GetDefaultUserSkillsDirectory()
            : Path.GetFullPath(userSkillsDirectory);
        _explicitPaths = explicitPaths ?? GetConfiguredSkillPaths();
        _additionalPathsProvider = additionalPathsProvider;
        _includeDefaults = includeDefaults;
    }

    public IReadOnlyList<CodingAgentSkill> Load()
    {
        var skills = new List<CodingAgentSkill>();
        if (_includeDefaults)
        {
            skills.AddRange(LoadFromDirectory(_userSkillsDirectory, "user"));
            skills.AddRange(LoadFromDirectory(Path.Combine(_cwd, ".tau", "skills"), "project"));
        }

        foreach (var path in GetExplicitPaths())
        {
            var resolved = ResolvePath(path, _cwd);
            if (Directory.Exists(resolved))
            {
                skills.AddRange(LoadFromDirectory(resolved, "path"));
            }
            else if (File.Exists(resolved) && Path.GetExtension(resolved).Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                var skill = LoadFromFile(resolved, "path");
                if (skill is not null)
                {
                    skills.Add(skill);
                }
            }
        }

        return skills
            .GroupBy(static skill => skill.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static skill => skill.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private IEnumerable<string> GetExplicitPaths()
    {
        foreach (var path in _explicitPaths)
        {
            yield return path;
        }

        if (_additionalPathsProvider is null)
        {
            yield break;
        }

        foreach (var path in _additionalPathsProvider())
        {
            yield return path;
        }
    }

    public bool TryExpand(string input, out string expanded, out CodingAgentSkill? skill)
    {
        expanded = input;
        skill = null;
        if (!input.StartsWith("/skill:", StringComparison.Ordinal))
        {
            return false;
        }

        const string skillPrefix = "/skill:";
        var spaceIndex = input.IndexOf(' ');
        var name = spaceIndex < 0 ? input[skillPrefix.Length..] : input[skillPrefix.Length..spaceIndex];
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        skill = Load().FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.Ordinal));
        if (skill is null)
        {
            return false;
        }

        var args = spaceIndex < 0 ? string.Empty : input[(spaceIndex + 1)..].Trim();
        var skillBlock = $"""
            <skill name="{skill.Name}" location="{skill.FilePath}">
            References are relative to {skill.BaseDirectory}.

            {skill.Content.Trim()}
            </skill>
            """;
        expanded = string.IsNullOrWhiteSpace(args) ? skillBlock : $"{skillBlock}\n\n{args}";
        return true;
    }

    public static string FormatForSystemPrompt(IReadOnlyList<CodingAgentSkill> skills)
    {
        var visibleSkills = skills
            .Where(static skill => !skill.DisableModelInvocation)
            .ToArray();
        if (visibleSkills.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("The following skills provide specialized instructions for specific tasks.");
        builder.AppendLine("Use the read_file tool to load a skill file when the task matches its description.");
        builder.AppendLine("When a skill file references a relative path, resolve it against the skill directory and use that absolute path in tool commands.");
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

    private IEnumerable<CodingAgentSkill> LoadFromDirectory(string directory, string scope)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            var rootSkill = Path.Combine(directory, "SKILL.md");
            files = File.Exists(rootSkill)
                ? [rootSkill]
                : Directory.EnumerateFiles(directory, "SKILL.md", SearchOption.AllDirectories)
                    .Where(static file => !IsIgnoredSkillPath(file))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var skill = LoadFromFile(file, scope);
            if (skill is not null)
            {
                yield return skill;
            }
        }
    }

    private static CodingAgentSkill? LoadFromFile(string filePath, string scope)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (metadata, body) = ParseFrontmatter(raw);
        if (!metadata.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var baseDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
        var name = metadata.TryGetValue("name", out var configuredName) && !string.IsNullOrWhiteSpace(configuredName)
            ? configuredName.Trim()
            : Path.GetFileName(baseDirectory);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var disableModelInvocation = metadata.TryGetValue("disable-model-invocation", out var disableValue)
            && bool.TryParse(disableValue, out var disabled)
            && disabled;

        return new CodingAgentSkill(
            name,
            description.Trim(),
            body,
            Path.GetFullPath(filePath),
            Path.GetFullPath(baseDirectory),
            scope,
            disableModelInvocation);
    }

    private static (IReadOnlyDictionary<string, string> Metadata, string Body) ParseFrontmatter(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), normalized);
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), normalized);
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frontmatter = normalized[4..end];
        foreach (var line in frontmatter.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(key))
            {
                metadata[key] = value;
            }
        }

        return (metadata, normalized[(end + 5)..]);
    }

    private static IReadOnlyList<string> GetConfiguredSkillPaths()
    {
        var configured = Environment.GetEnvironmentVariable(SkillPathsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        return configured
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string ResolvePath(string path, string cwd)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(cwd, path));
    }

    private static string GetDefaultUserSkillsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tau", "skills");

    private static bool IsIgnoredSkillPath(string file)
    {
        var parts = Path.GetFullPath(file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(static part =>
            part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || part.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
