using System.Text;
using System.Text.RegularExpressions;

namespace Tau.AgentCore.Harness;

public sealed record AgentPromptTemplate(
    string Name,
    string Description,
    string Content);

public sealed record AgentPromptTemplateDiagnostic(
    string Type,
    string Code,
    string Message,
    string Path);

public sealed record AgentSourcedPromptTemplate<TSource>(
    AgentPromptTemplate PromptTemplate,
    TSource Source);

public sealed record AgentSourcedPromptTemplateDiagnostic<TSource>(
    string Type,
    string Code,
    string Message,
    string Path,
    TSource Source);

public static partial class AgentHarnessPromptTemplates
{
    public static (IReadOnlyList<AgentPromptTemplate> PromptTemplates, IReadOnlyList<AgentPromptTemplateDiagnostic> Diagnostics)
        LoadPromptTemplates(string path) =>
        LoadPromptTemplates([path]);

    public static (IReadOnlyList<AgentPromptTemplate> PromptTemplates, IReadOnlyList<AgentPromptTemplateDiagnostic> Diagnostics)
        LoadPromptTemplates(IEnumerable<string> paths)
    {
        var promptTemplates = new List<AgentPromptTemplate>();
        var diagnostics = new List<AgentPromptTemplateDiagnostic>();
        foreach (var path in paths)
        {
            FileSystemInfo? info;
            try
            {
                if (File.Exists(path))
                    info = new FileInfo(path);
                else if (Directory.Exists(path))
                    info = new DirectoryInfo(path);
                else
                    continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Warning("file_info_failed", ex.Message, path));
                continue;
            }

            if (info is DirectoryInfo directory)
            {
                var result = LoadTemplatesFromDirectory(directory.FullName);
                promptTemplates.AddRange(result.PromptTemplates);
                diagnostics.AddRange(result.Diagnostics);
            }
            else if (info is FileInfo file && file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                var result = LoadTemplateFromFile(file.FullName);
                if (result.PromptTemplate is not null)
                    promptTemplates.Add(result.PromptTemplate);
                diagnostics.AddRange(result.Diagnostics);
            }
        }

        return (promptTemplates, diagnostics);
    }

    public static (
        IReadOnlyList<AgentSourcedPromptTemplate<TSource>> PromptTemplates,
        IReadOnlyList<AgentSourcedPromptTemplateDiagnostic<TSource>> Diagnostics) LoadSourcedPromptTemplates<TSource>(
        IEnumerable<(string Path, TSource Source)> inputs,
        Func<AgentPromptTemplate, TSource, AgentPromptTemplate>? mapPromptTemplate = null)
    {
        var promptTemplates = new List<AgentSourcedPromptTemplate<TSource>>();
        var diagnostics = new List<AgentSourcedPromptTemplateDiagnostic<TSource>>();
        foreach (var input in inputs)
        {
            var result = LoadPromptTemplates(input.Path);
            foreach (var promptTemplate in result.PromptTemplates)
            {
                promptTemplates.Add(new AgentSourcedPromptTemplate<TSource>(
                    mapPromptTemplate?.Invoke(promptTemplate, input.Source) ?? promptTemplate,
                    input.Source));
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(new AgentSourcedPromptTemplateDiagnostic<TSource>(
                    diagnostic.Type,
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Path,
                    input.Source));
            }
        }

        return (promptTemplates, diagnostics);
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
                    quote = null;
                else
                    current.Append(character);
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character is ' ' or '\t')
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
            args.Add(current.ToString());

        return args;
    }

    public static string SubstituteArgs(string content, IReadOnlyList<string> args)
    {
        var result = PositionalArgumentRegex().Replace(content, match =>
        {
            var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
            return index >= 0 && index < args.Count ? args[index] : string.Empty;
        });

        result = SlicedArgumentsRegex().Replace(result, match =>
        {
            var start = Math.Max(0, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1);
            if (match.Groups[2].Success)
            {
                var length = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                return string.Join(" ", args.Skip(start).Take(length));
            }

            return string.Join(" ", args.Skip(start));
        });

        var allArgs = string.Join(" ", args);
        return result
            .Replace("$ARGUMENTS", allArgs, StringComparison.Ordinal)
            .Replace("$@", allArgs, StringComparison.Ordinal);
    }

    public static string FormatPromptTemplateInvocation(
        AgentPromptTemplate promptTemplate,
        IReadOnlyList<string>? args = null) =>
        SubstituteArgs(promptTemplate.Content, args ?? []);

    private static (IReadOnlyList<AgentPromptTemplate> PromptTemplates, IReadOnlyList<AgentPromptTemplateDiagnostic> Diagnostics)
        LoadTemplatesFromDirectory(string directory)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], [Warning("list_failed", ex.Message, directory)]);
        }

        var promptTemplates = new List<AgentPromptTemplate>();
        var diagnostics = new List<AgentPromptTemplateDiagnostic>();
        foreach (var file in files)
        {
            var result = LoadTemplateFromFile(file);
            if (result.PromptTemplate is not null)
                promptTemplates.Add(result.PromptTemplate);
            diagnostics.AddRange(result.Diagnostics);
        }

        return (promptTemplates, diagnostics);
    }

    private static (AgentPromptTemplate? PromptTemplate, IReadOnlyList<AgentPromptTemplateDiagnostic> Diagnostics)
        LoadTemplateFromFile(string filePath)
    {
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

        var body = parsed.Body;
        var firstLine = body
            .Split('\n')
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
        var description = parsed.Frontmatter.TryGetValue("description", out var configuredDescription)
            ? configuredDescription
            : string.Empty;
        if (string.IsNullOrEmpty(description) && firstLine is not null)
            description = firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;

        return (
            new AgentPromptTemplate(
                Path.GetFileNameWithoutExtension(filePath),
                description,
                body),
            []);
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

    private static AgentPromptTemplateDiagnostic Warning(string code, string message, string path) =>
        new("warning", code, message, path);

    [GeneratedRegex(@"\$(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PositionalArgumentRegex();

    [GeneratedRegex(@"\$\{@:(\d+)(?::(\d+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex SlicedArgumentsRegex();
}
