using System.Text;

namespace Tau.AgentCore.Harness;

internal sealed record AgentSkillIgnoreRule(string Pattern, bool Negated);

public sealed record AgentHarnessSkill(
    string Name,
    string Description,
    string Content,
    string FilePath,
    bool DisableModelInvocation = false);

public sealed record AgentSkillDiagnostic(
    string Type,
    string Code,
    string Message,
    string Path);

public sealed record AgentSourcedSkill<TSource>(
    AgentHarnessSkill Skill,
    TSource Source);

public sealed record AgentSourcedSkillDiagnostic<TSource>(
    string Type,
    string Code,
    string Message,
    string Path,
    TSource Source);

public static class AgentHarnessSkills
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;
    private static readonly string[] IgnoreFileNames = [".gitignore", ".ignore", ".fdignore"];

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

    public static (IReadOnlyList<AgentHarnessSkill> Skills, IReadOnlyList<AgentSkillDiagnostic> Diagnostics) LoadSkills(
        string dir) =>
        LoadSkills([dir]);

    public static (IReadOnlyList<AgentHarnessSkill> Skills, IReadOnlyList<AgentSkillDiagnostic> Diagnostics) LoadSkills(
        IEnumerable<string> dirs)
    {
        var skills = new List<AgentHarnessSkill>();
        var diagnostics = new List<AgentSkillDiagnostic>();
        foreach (var dir in dirs)
        {
            DirectoryInfo rootInfo;
            try
            {
                if (!Directory.Exists(dir))
                    continue;

                rootInfo = new DirectoryInfo(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Warning("file_info_failed", ex.Message, dir));
                continue;
            }

            var result = LoadSkillsFromDirectory(rootInfo.FullName, includeRootFiles: true, [], rootInfo.FullName);
            skills.AddRange(result.Skills);
            diagnostics.AddRange(result.Diagnostics);
        }

        return (skills, diagnostics);
    }

    public static (
        IReadOnlyList<AgentSourcedSkill<TSource>> Skills,
        IReadOnlyList<AgentSourcedSkillDiagnostic<TSource>> Diagnostics) LoadSourcedSkills<TSource>(
        IEnumerable<(string Path, TSource Source)> inputs,
        Func<AgentHarnessSkill, TSource, AgentHarnessSkill>? mapSkill = null)
    {
        var skills = new List<AgentSourcedSkill<TSource>>();
        var diagnostics = new List<AgentSourcedSkillDiagnostic<TSource>>();
        foreach (var input in inputs)
        {
            var result = LoadSkills(input.Path);
            foreach (var skill in result.Skills)
            {
                skills.Add(new AgentSourcedSkill<TSource>(
                    mapSkill?.Invoke(skill, input.Source) ?? skill,
                    input.Source));
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(new AgentSourcedSkillDiagnostic<TSource>(
                    diagnostic.Type,
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Path,
                    input.Source));
            }
        }

        return (skills, diagnostics);
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

    private static (IReadOnlyList<AgentHarnessSkill> Skills, IReadOnlyList<AgentSkillDiagnostic> Diagnostics)
        LoadSkillsFromDirectory(
            string dir,
            bool includeRootFiles,
            IReadOnlyList<AgentSkillIgnoreRule> inheritedIgnoreRules,
            string rootDir)
    {
        var skills = new List<AgentHarnessSkill>();
        var diagnostics = new List<AgentSkillDiagnostic>();

        if (!Directory.Exists(dir))
            return (skills, diagnostics);

        var ignoreRules = inheritedIgnoreRules.Concat(LoadIgnoreRules(dir, rootDir, diagnostics)).ToArray();

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Warning("list_failed", ex.Message, dir));
            return (skills, diagnostics);
        }

        var rootSkill = entries
            .OfType<FileInfo>()
            .FirstOrDefault(static file => file.Name.Equals("SKILL.md", StringComparison.Ordinal));
        if (rootSkill is not null && !IsIgnored(rootSkill.FullName, isDirectory: false, rootDir, ignoreRules))
        {
            var result = LoadSkillFromFile(rootSkill.FullName);
            if (result.Skill is not null)
                skills.Add(result.Skill);
            diagnostics.AddRange(result.Diagnostics);
            return (skills, diagnostics);
        }

        foreach (var entry in entries.OrderBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            if (entry.Name.StartsWith(".", StringComparison.Ordinal) ||
                entry.Name.Equals("node_modules", StringComparison.Ordinal))
            {
                continue;
            }

            var isDirectory = Directory.Exists(entry.FullName);
            if (IsIgnored(entry.FullName, isDirectory, rootDir, ignoreRules))
                continue;

            if (isDirectory)
            {
                var result = LoadSkillsFromDirectory(entry.FullName, includeRootFiles: false, ignoreRules, rootDir);
                skills.AddRange(result.Skills);
                diagnostics.AddRange(result.Diagnostics);
                continue;
            }

            if (!includeRootFiles ||
                !entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(entry.FullName))
            {
                continue;
            }

            var fileResult = LoadSkillFromFile(entry.FullName);
            if (fileResult.Skill is not null)
                skills.Add(fileResult.Skill);
            diagnostics.AddRange(fileResult.Diagnostics);
        }

        return (skills, diagnostics);
    }

    private static IReadOnlyList<AgentSkillIgnoreRule> LoadIgnoreRules(
        string dir,
        string rootDir,
        ICollection<AgentSkillDiagnostic> diagnostics)
    {
        var rules = new List<AgentSkillIgnoreRule>();
        var relativeDir = RelativePath(rootDir, dir);
        var prefix = string.IsNullOrEmpty(relativeDir) ? string.Empty : relativeDir + "/";

        foreach (var filename in IgnoreFileNames)
        {
            var ignorePath = Path.Combine(dir, filename);
            if (!File.Exists(ignorePath))
                continue;

            string content;
            try
            {
                content = File.ReadAllText(ignorePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Warning("read_failed", ex.Message, ignorePath));
                continue;
            }

            foreach (var rawLine in content.Split(["\r\n", "\n"], StringSplitOptions.None))
            {
                var rule = PrefixIgnorePattern(rawLine, prefix);
                if (rule is not null)
                    rules.Add(rule);
            }
        }

        return rules;
    }

    private static AgentSkillIgnoreRule? PrefixIgnorePattern(string line, string prefix)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return null;
        if (trimmed.StartsWith("#", StringComparison.Ordinal) && !trimmed.StartsWith("\\#", StringComparison.Ordinal))
            return null;

        var pattern = line.Trim();
        var negated = false;
        if (pattern.StartsWith("!", StringComparison.Ordinal))
        {
            negated = true;
            pattern = pattern[1..];
        }
        else if (pattern.StartsWith("\\!", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        if (pattern.StartsWith("\\#", StringComparison.Ordinal))
            pattern = pattern[1..];
        if (pattern.StartsWith("/", StringComparison.Ordinal))
            pattern = pattern[1..];

        var prefixed = string.IsNullOrEmpty(prefix) ? pattern : prefix + pattern;
        return string.IsNullOrWhiteSpace(prefixed)
            ? null
            : new AgentSkillIgnoreRule(NormalizePath(prefixed), negated);
    }

    private static (AgentHarnessSkill? Skill, IReadOnlyList<AgentSkillDiagnostic> Diagnostics) LoadSkillFromFile(
        string filePath)
    {
        var diagnostics = new List<AgentSkillDiagnostic>();
        string rawContent;
        try
        {
            rawContent = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, [Warning("read_failed", ex.Message, filePath)]);
        }

        var parsed = ParseFrontmatter(rawContent);
        if (parsed.Error is not null)
            return (null, [Warning("parse_failed", parsed.Error.Message, filePath)]);

        var skillDir = Path.GetDirectoryName(filePath) ?? ".";
        var parentDirName = Path.GetFileName(skillDir);
        parsed.Frontmatter.TryGetValue("description", out var description);
        foreach (var error in ValidateDescription(description))
            diagnostics.Add(Warning("invalid_metadata", error, filePath));

        var name = parsed.Frontmatter.TryGetValue("name", out var frontmatterName) &&
            !string.IsNullOrWhiteSpace(frontmatterName)
                ? frontmatterName
                : parentDirName;
        foreach (var error in ValidateName(name, parentDirName))
            diagnostics.Add(Warning("invalid_metadata", error, filePath));

        if (string.IsNullOrWhiteSpace(description))
            return (null, diagnostics);

        var disabled = parsed.Frontmatter.TryGetValue("disable-model-invocation", out var disabledText) &&
            bool.TryParse(disabledText, out var parsedDisabled) &&
            parsedDisabled;

        return (
            new AgentHarnessSkill(
                name,
                description,
                parsed.Body,
                Path.GetFullPath(filePath),
                disabled),
            diagnostics);
    }

    private static (IReadOnlyDictionary<string, string> Frontmatter, string Body, Exception? Error) ParseFrontmatter(
        string content)
    {
        try
        {
            var normalized = content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            if (!normalized.StartsWith("---", StringComparison.Ordinal))
                return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), normalized, null);

            var endIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIndex < 0)
                return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), normalized, null);

            var frontmatter = normalized[4..endIndex];
            var body = normalized[(endIndex + 4)..].Trim();
            return (ParseSimpleYamlMapping(frontmatter), body, null);
        }
        catch (Exception ex) when (ex is FormatException)
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), string.Empty, ex);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseSimpleYamlMapping(string content)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
                throw new FormatException($"Invalid frontmatter line: {line}");

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0)
                metadata[key] = value;
        }

        return metadata;
    }

    private static IEnumerable<string> ValidateName(string name, string parentDirName)
    {
        if (!name.Equals(parentDirName, StringComparison.Ordinal))
            yield return $"name \"{name}\" does not match parent directory \"{parentDirName}\"";
        if (name.Length > MaxNameLength)
            yield return $"name exceeds {MaxNameLength} characters ({name.Length})";
        if (!IsValidSkillName(name))
            yield return "name contains invalid characters (must be lowercase a-z, 0-9, hyphens only)";
        if (name.StartsWith("-", StringComparison.Ordinal) || name.EndsWith("-", StringComparison.Ordinal))
            yield return "name must not start or end with a hyphen";
        if (name.Contains("--", StringComparison.Ordinal))
            yield return "name must not contain consecutive hyphens";
    }

    private static IEnumerable<string> ValidateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            yield return "description is required";
        else if (description.Length > MaxDescriptionLength)
            yield return $"description exceeds {MaxDescriptionLength} characters ({description.Length})";
    }

    private static bool IsValidSkillName(string name) =>
        name.Length > 0 &&
        name.All(static character =>
            character is >= 'a' and <= 'z' ||
            character is >= '0' and <= '9' ||
            character == '-');

    private static bool IsIgnored(
        string path,
        bool isDirectory,
        string rootDir,
        IReadOnlyList<AgentSkillIgnoreRule> rules)
    {
        var relative = RelativePath(rootDir, path);
        if (isDirectory && !relative.EndsWith("/", StringComparison.Ordinal))
            relative += "/";

        var ignored = false;
        foreach (var rule in rules)
        {
            if (IgnoreRuleMatches(rule.Pattern, relative))
                ignored = !rule.Negated;
        }

        return ignored;
    }

    private static bool IgnoreRuleMatches(string pattern, string relativePath)
    {
        var normalizedPattern = NormalizePath(pattern);
        var normalizedPath = NormalizePath(relativePath).TrimStart('/');
        var pathWithoutTrailingSlash = normalizedPath.TrimEnd('/');

        if (normalizedPattern.EndsWith("/", StringComparison.Ordinal))
        {
            var directoryPattern = normalizedPattern.TrimEnd('/');
            return normalizedPattern.Contains('/', StringComparison.Ordinal)
                ? pathWithoutTrailingSlash.Equals(directoryPattern, StringComparison.Ordinal) ||
                  normalizedPath.StartsWith(directoryPattern + "/", StringComparison.Ordinal)
                : normalizedPath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => GlobMatch(directoryPattern, segment, slashSensitive: false));
        }

        if (normalizedPattern.Contains('/', StringComparison.Ordinal))
            return GlobMatch(normalizedPattern, pathWithoutTrailingSlash, slashSensitive: true);

        return normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => GlobMatch(normalizedPattern, segment, slashSensitive: false));
    }

    private static bool GlobMatch(string pattern, string text, bool slashSensitive)
    {
        var memo = new Dictionary<(int Pattern, int Text), bool>();
        return Match(0, 0);

        bool Match(int patternIndex, int textIndex)
        {
            if (memo.TryGetValue((patternIndex, textIndex), out var cached))
                return cached;

            bool result;
            if (patternIndex == pattern.Length)
            {
                result = textIndex == text.Length;
            }
            else if (pattern[patternIndex] == '*')
            {
                var isDoubleStar = patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*';
                var nextPatternIndex = patternIndex + (isDoubleStar ? 2 : 1);
                result = Match(nextPatternIndex, textIndex);
                for (var i = textIndex; !result && i < text.Length; i++)
                {
                    if (slashSensitive && !isDoubleStar && text[i] == '/')
                        break;

                    result = Match(nextPatternIndex, i + 1);
                }
            }
            else if (textIndex < text.Length &&
                (pattern[patternIndex] == '?' ||
                 pattern[patternIndex] == text[textIndex]) &&
                (!slashSensitive || pattern[patternIndex] != '?' || text[textIndex] != '/'))
            {
                result = Match(patternIndex + 1, textIndex + 1);
            }
            else
            {
                result = false;
            }

            memo[(patternIndex, textIndex)] = result;
            return result;
        }
    }

    private static string RelativePath(string rootDir, string path)
    {
        var relative = Path.GetRelativePath(rootDir, path);
        return relative == "."
            ? string.Empty
            : NormalizePath(relative);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static AgentSkillDiagnostic Warning(string code, string message, string path) =>
        new("warning", code, message, path);
}
