using System.Text;
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentPromptTemplate(
    string Name,
    string Description,
    string? ArgumentHint,
    string Content,
    string FilePath,
    string Scope);

public sealed partial class CodingAgentPromptTemplateStore
{
    public const string PromptPathsEnvironmentVariable = "TAU_CODING_AGENT_PROMPT_PATHS";

    private readonly string _cwd;
    private readonly string _userPromptsDirectory;
    private readonly IReadOnlyList<string> _explicitPaths;
    private readonly Func<IReadOnlyList<string>>? _additionalPathsProvider;
    private readonly bool _includeDefaults;

    public CodingAgentPromptTemplateStore(
        string? cwd = null,
        string? userPromptsDirectory = null,
        IReadOnlyList<string>? explicitPaths = null,
        Func<IReadOnlyList<string>>? additionalPathsProvider = null,
        bool includeDefaults = true)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _userPromptsDirectory = string.IsNullOrWhiteSpace(userPromptsDirectory)
            ? GetDefaultUserPromptsDirectory()
            : Path.GetFullPath(userPromptsDirectory);
        _explicitPaths = explicitPaths ?? GetConfiguredPromptPaths();
        _additionalPathsProvider = additionalPathsProvider;
        _includeDefaults = includeDefaults;
    }

    public IReadOnlyList<CodingAgentPromptTemplate> Load()
    {
        var templates = new List<CodingAgentPromptTemplate>();
        if (_includeDefaults)
        {
            templates.AddRange(LoadFromDirectory(_userPromptsDirectory, "user"));
            templates.AddRange(LoadFromDirectory(Path.Combine(_cwd, ".tau", "prompts"), "project"));
        }

        foreach (var path in GetExplicitPaths())
        {
            var resolved = ResolvePath(path, _cwd);
            if (Directory.Exists(resolved))
            {
                templates.AddRange(LoadFromDirectory(resolved, "path"));
            }
            else if (File.Exists(resolved) && Path.GetExtension(resolved).Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                var template = LoadFromFile(resolved, "path");
                if (template is not null)
                {
                    templates.Add(template);
                }
            }
        }

        return templates
            .GroupBy(static template => template.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static template => template.Name, StringComparer.Ordinal)
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

    public bool TryExpand(string input, out string expanded, out CodingAgentPromptTemplate? template)
    {
        expanded = input;
        template = null;
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var spaceIndex = input.IndexOf(' ');
        var name = spaceIndex < 0 ? input[1..] : input[1..spaceIndex];
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        template = Load().FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.Ordinal));
        if (template is null)
        {
            return false;
        }

        var args = ParseCommandArgs(spaceIndex < 0 ? string.Empty : input[(spaceIndex + 1)..]);
        expanded = SubstituteArgs(template.Content, args);
        return true;
    }

    public static IReadOnlyList<string> ParseCommandArgs(string argsString)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        foreach (var character in argsString)
        {
            if (quote is { } activeQuote)
            {
                if (character == activeQuote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    public static string SubstituteArgs(string content, IReadOnlyList<string> args)
    {
        var result = PositionalArgumentRegex().Replace(content, match =>
        {
            var index = int.Parse(match.Groups[1].Value) - 1;
            return index >= 0 && index < args.Count ? args[index] : string.Empty;
        });

        result = SlicedArgumentsRegex().Replace(result, match =>
        {
            var start = Math.Max(0, int.Parse(match.Groups[1].Value) - 1);
            if (match.Groups[2].Success)
            {
                var length = int.Parse(match.Groups[2].Value);
                return string.Join(" ", args.Skip(start).Take(length));
            }

            return string.Join(" ", args.Skip(start));
        });

        var allArgs = string.Join(" ", args);
        return result
            .Replace("$ARGUMENTS", allArgs, StringComparison.Ordinal)
            .Replace("$@", allArgs, StringComparison.Ordinal);
    }

    private IEnumerable<CodingAgentPromptTemplate> LoadFromDirectory(string directory, string scope)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var template = LoadFromFile(file, scope);
            if (template is not null)
            {
                yield return template;
            }
        }
    }

    private static CodingAgentPromptTemplate? LoadFromFile(string filePath, string scope)
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
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var description = metadata.TryGetValue("description", out var configuredDescription)
            ? configuredDescription
            : FirstContentLine(body);
        var argumentHint = metadata.TryGetValue("argument-hint", out var configuredHint)
            ? configuredHint
            : null;

        return new CodingAgentPromptTemplate(
            name,
            string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim(),
            string.IsNullOrWhiteSpace(argumentHint) ? null : argumentHint.Trim(),
            body,
            Path.GetFullPath(filePath),
            scope);
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

    private static string FirstContentLine(string content)
    {
        var line = content
            .Split('\n')
            .Select(static value => value.Trim())
            .FirstOrDefault(static value => value.Length > 0);
        if (line is null)
        {
            return string.Empty;
        }

        return line.Length <= 60 ? line : line[..60] + "...";
    }

    private static IReadOnlyList<string> GetConfiguredPromptPaths()
    {
        var configured = Environment.GetEnvironmentVariable(PromptPathsEnvironmentVariable);
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

    private static string GetDefaultUserPromptsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tau", "prompts");

    [GeneratedRegex(@"\$(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PositionalArgumentRegex();

    [GeneratedRegex(@"\$\{@:(\d+)(?::(\d+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex SlicedArgumentsRegex();
}
