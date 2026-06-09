using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.CodingAgent.Runtime;

[JsonConverter(typeof(CodingAgentPackageSourceJsonConverter))]
public sealed record CodingAgentPackageSource(
    string Source,
    IReadOnlyList<string>? Extensions = null,
    IReadOnlyList<string>? Skills = null,
    IReadOnlyList<string>? Prompts = null,
    IReadOnlyList<string>? Themes = null)
{
    public bool IsFiltered =>
        Extensions is not null ||
        Skills is not null ||
        Prompts is not null ||
        Themes is not null;
}

public sealed record CodingAgentConfiguredPackage(
    string Source,
    string Scope,
    bool Filtered,
    string? InstalledPath);

public sealed record CodingAgentPackageDiagnostic(
    string Severity,
    string Message,
    string Source,
    string Scope);

public sealed record CodingAgentPackageResources(
    IReadOnlyList<string> ExtensionPaths,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptPaths,
    IReadOnlyList<string> ThemePaths,
    IReadOnlyList<CodingAgentPackageDiagnostic> Diagnostics);

public sealed class CodingAgentPackageResourceState
{
    private CodingAgentPackageResources _resources;

    public CodingAgentPackageResourceState(CodingAgentPackageResources? resources = null)
    {
        _resources = resources ?? new CodingAgentPackageResources([], [], [], [], []);
    }

    public IReadOnlyList<string> ExtensionPaths => _resources.ExtensionPaths;
    public IReadOnlyList<string> SkillPaths => _resources.SkillPaths;
    public IReadOnlyList<string> PromptPaths => _resources.PromptPaths;
    public IReadOnlyList<string> ThemePaths => _resources.ThemePaths;
    public IReadOnlyList<CodingAgentPackageDiagnostic> Diagnostics => _resources.Diagnostics;

    public void Update(CodingAgentPackageResources resources)
    {
        _resources = resources;
    }
}

public sealed class CodingAgentPackageManager
{
    private readonly string _cwd;

    public CodingAgentPackageManager(
        string? cwd = null,
        string? userSettingsPath = null,
        string? projectSettingsPath = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        UserSettingsPath = string.IsNullOrWhiteSpace(userSettingsPath)
            ? GetDefaultUserSettingsPath()
            : Path.GetFullPath(userSettingsPath);
        ProjectSettingsPath = string.IsNullOrWhiteSpace(projectSettingsPath)
            ? CodingAgentSettingsStore.GetDefaultPath()
            : Path.GetFullPath(projectSettingsPath);
    }

    public string UserSettingsPath { get; }

    public string ProjectSettingsPath { get; }

    public static string GetDefaultUserSettingsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.CurrentDirectory, ".tau", "coding-agent-settings.json")
            : Path.Combine(home, ".tau", "coding-agent-settings.json");
    }

    public IReadOnlyList<CodingAgentConfiguredPackage> ListConfiguredPackages()
    {
        var packages = new List<CodingAgentConfiguredPackage>();
        AddConfiguredPackages(packages, "user", UserSettingsPath);

        if (!PathsEqual(UserSettingsPath, ProjectSettingsPath))
        {
            AddConfiguredPackages(packages, "project", ProjectSettingsPath);
        }

        return packages;
    }

    public bool AddSource(string source, bool local)
    {
        var normalized = NormalizeSource(source);
        if (normalized is null)
        {
            return false;
        }

        var store = CreateStore(local);
        var snapshot = store.Load();
        var packages = NormalizePackageSources(snapshot.Packages).ToList();
        if (packages.Any(package => package.Source.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        packages.Add(new CodingAgentPackageSource(normalized));
        store.Save(snapshot with { Packages = packages });
        return true;
    }

    public bool RemoveSource(string source, bool local)
    {
        var normalized = NormalizeSource(source);
        if (normalized is null)
        {
            return false;
        }

        var store = CreateStore(local);
        var snapshot = store.Load();
        var before = NormalizePackageSources(snapshot.Packages).ToArray();
        var after = before
            .Where(package => !package.Source.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (after.Length == before.Length)
        {
            return false;
        }

        store.Save(snapshot with { Packages = after.Length == 0 ? null : after });
        return true;
    }

    public bool ContainsSource(string source)
    {
        var normalized = NormalizeSource(source);
        return normalized is not null &&
            ListConfiguredPackages().Any(package => package.Source.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public CodingAgentPackageResources ResolveResources()
    {
        var extensions = new List<string>();
        var skills = new List<string>();
        var prompts = new List<string>();
        var themes = new List<string>();
        var diagnostics = new List<CodingAgentPackageDiagnostic>();

        foreach (var configured in EnumerateConfiguredSources())
        {
            if (!TryResolveLocalPackageRoot(configured.Source.Source, out var root))
            {
                diagnostics.Add(new CodingAgentPackageDiagnostic(
                    "warning",
                    "non-local package source is configured but Tau does not install npm/git packages in this baseline",
                    configured.Source.Source,
                    configured.Scope));
                continue;
            }

            if (!Directory.Exists(root))
            {
                diagnostics.Add(new CodingAgentPackageDiagnostic(
                    "warning",
                    "local package source does not exist",
                    configured.Source.Source,
                    configured.Scope));
                continue;
            }

            AddPackageResources(configured.Source, root, configured.Scope, extensions, skills, prompts, themes, diagnostics);
        }

        return new CodingAgentPackageResources(
            DistinctPaths(extensions),
            DistinctPaths(skills),
            DistinctPaths(prompts),
            DistinctPaths(themes),
            diagnostics.ToArray());
    }

    private void AddConfiguredPackages(ICollection<CodingAgentConfiguredPackage> packages, string scope, string settingsPath)
    {
        foreach (var source in NormalizePackageSources(new CodingAgentSettingsStore(settingsPath).Load().Packages))
        {
            packages.Add(new CodingAgentConfiguredPackage(
                source.Source,
                scope,
                source.IsFiltered,
                TryResolveLocalPackageRoot(source.Source, out var root) && Directory.Exists(root) ? root : null));
        }
    }

    private IEnumerable<(CodingAgentPackageSource Source, string Scope)> EnumerateConfiguredSources()
    {
        foreach (var source in NormalizePackageSources(new CodingAgentSettingsStore(UserSettingsPath).Load().Packages))
        {
            yield return (source, "user");
        }

        if (PathsEqual(UserSettingsPath, ProjectSettingsPath))
        {
            yield break;
        }

        foreach (var source in NormalizePackageSources(new CodingAgentSettingsStore(ProjectSettingsPath).Load().Packages))
        {
            yield return (source, "project");
        }
    }

    private CodingAgentSettingsStore CreateStore(bool local) =>
        new(local ? ProjectSettingsPath : UserSettingsPath);

    private void AddPackageResources(
        CodingAgentPackageSource source,
        string root,
        string scope,
        ICollection<string> extensions,
        ICollection<string> skills,
        ICollection<string> prompts,
        ICollection<string> themes,
        ICollection<CodingAgentPackageDiagnostic> diagnostics)
    {
        if (source.IsFiltered)
        {
            AddConfiguredResourceEntries(root, source.Extensions, "extensions", source, scope, extensions, diagnostics);
            AddConfiguredResourceEntries(root, source.Skills, "skills", source, scope, skills, diagnostics);
            AddConfiguredResourceEntries(root, source.Prompts, "prompts", source, scope, prompts, diagnostics);
            AddConfiguredResourceEntries(root, source.Themes, "themes", source, scope, themes, diagnostics);
            return;
        }

        var manifest = ReadManifest(root, source, scope, diagnostics);
        if (manifest is not null)
        {
            AddConfiguredResourceEntries(root, manifest.Extensions, "extensions", source, scope, extensions, diagnostics);
            AddConfiguredResourceEntries(root, manifest.Skills, "skills", source, scope, skills, diagnostics);
            AddConfiguredResourceEntries(root, manifest.Prompts, "prompts", source, scope, prompts, diagnostics);
            AddConfiguredResourceEntries(root, manifest.Themes, "themes", source, scope, themes, diagnostics);
            return;
        }

        AddConventionDirectory(root, "extensions", extensions);
        AddConventionDirectory(root, "skills", skills);
        AddConventionDirectory(root, "prompts", prompts);
        AddConventionDirectory(root, "themes", themes);
    }

    private static void AddConventionDirectory(string root, string name, ICollection<string> target)
    {
        var directory = Path.Combine(root, name);
        if (Directory.Exists(directory))
        {
            target.Add(directory);
        }
    }

    private void AddConfiguredResourceEntries(
        string root,
        IReadOnlyList<string>? entries,
        string resourceType,
        CodingAgentPackageSource source,
        string scope,
        ICollection<string> target,
        ICollection<CodingAgentPackageDiagnostic> diagnostics)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry) || IsOverridePattern(entry))
            {
                continue;
            }

            if (entry.Contains('*', StringComparison.Ordinal) || entry.Contains('?', StringComparison.Ordinal))
            {
                diagnostics.Add(new CodingAgentPackageDiagnostic(
                    "warning",
                    $"package {resourceType} glob patterns are not resolved in this baseline: {entry}",
                    source.Source,
                    scope));
                continue;
            }

            target.Add(ResolvePath(entry, root));
        }
    }

    private static bool IsOverridePattern(string value) =>
        value.StartsWith("!", StringComparison.Ordinal) ||
        value.StartsWith("+", StringComparison.Ordinal) ||
        value.StartsWith("-", StringComparison.Ordinal);

    private CodingAgentPackageSource? ReadManifest(
        string root,
        CodingAgentPackageSource source,
        string scope,
        ICollection<CodingAgentPackageDiagnostic> diagnostics)
    {
        var packageJsonPath = Path.Combine(root, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("pi", out var pi) || pi.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new CodingAgentPackageSource(
                source.Source,
                ReadStringArray(pi, "extensions"),
                ReadStringArray(pi, "skills"),
                ReadStringArray(pi, "prompts"),
                ReadStringArray(pi, "themes"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodingAgentPackageDiagnostic(
                "warning",
                $"package manifest could not be read: {ex.Message}",
                source.Source,
                scope));
            return null;
        }
    }

    private bool TryResolveLocalPackageRoot(string source, out string root)
    {
        root = string.Empty;
        var normalized = source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? source["file:".Length..]
            : source;

        if (!IsLocalSource(normalized))
        {
            return false;
        }

        root = ResolvePath(normalized, _cwd);
        return true;
    }

    private static bool IsLocalSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("git:", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.IsPathFullyQualified(source) ||
            source.StartsWith(".", StringComparison.Ordinal) ||
            source.StartsWith("~", StringComparison.Ordinal) ||
            source.Contains('/', StringComparison.Ordinal) ||
            source.Contains('\\', StringComparison.Ordinal);
    }

    private static IReadOnlyList<CodingAgentPackageSource> NormalizePackageSources(
        IReadOnlyList<CodingAgentPackageSource>? packages)
    {
        if (packages is null || packages.Count == 0)
        {
            return [];
        }

        var results = new List<CodingAgentPackageSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            var source = NormalizeSource(package.Source);
            if (source is null || !seen.Add(source))
            {
                continue;
            }

            results.Add(package with
            {
                Source = source,
                Extensions = NormalizeStringListPreserveEmpty(package.Extensions),
                Skills = NormalizeStringListPreserveEmpty(package.Skills),
                Prompts = NormalizeStringListPreserveEmpty(package.Prompts),
                Themes = NormalizeStringListPreserveEmpty(package.Themes)
            });
        }

        return results;
    }

    private static string? NormalizeSource(string? source) =>
        string.IsNullOrWhiteSpace(source) ? null : source.Trim();

    private static string[]? NormalizeStringListPreserveEmpty(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();
    }

    private static string[] DistinctPaths(IEnumerable<string> paths) =>
        paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string>? ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
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

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

public static class CodingAgentPackageCli
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "install",
        "remove",
        "uninstall",
        "update",
        "list",
        "config"
    };

    public static bool TryHandle(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CodingAgentPackageManager? packageManager,
        out int exitCode)
    {
        exitCode = 0;
        if (args.Count == 0 || !Commands.Contains(args[0]))
        {
            return false;
        }

        packageManager ??= new CodingAgentPackageManager();
        var command = args[0].Equals("uninstall", StringComparison.OrdinalIgnoreCase) ? "remove" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        var parsed = Parse(command, rest);

        if (parsed.Help)
        {
            output.WriteLine(GetHelp(command));
            return true;
        }

        if (parsed.InvalidOption is not null)
        {
            error.WriteLine($"Unknown option {parsed.InvalidOption} for \"{command}\".");
            error.WriteLine($"Use \"tau --help\" or \"{GetUsage(command)}\".");
            exitCode = 1;
            return true;
        }

        switch (command)
        {
            case "install":
                if (string.IsNullOrWhiteSpace(parsed.Source))
                {
                    error.WriteLine("Missing install source.");
                    error.WriteLine($"Usage: {GetUsage(command)}");
                    exitCode = 1;
                    return true;
                }

                packageManager.AddSource(parsed.Source, parsed.Local);
                output.WriteLine($"Installed {parsed.Source}");
                return true;

            case "remove":
                if (string.IsNullOrWhiteSpace(parsed.Source))
                {
                    error.WriteLine("Missing remove source.");
                    error.WriteLine($"Usage: {GetUsage(command)}");
                    exitCode = 1;
                    return true;
                }

                if (!packageManager.RemoveSource(parsed.Source, parsed.Local))
                {
                    error.WriteLine($"No matching package found for {parsed.Source}");
                    exitCode = 1;
                    return true;
                }

                output.WriteLine($"Removed {parsed.Source}");
                return true;

            case "list":
                PrintList(packageManager, output);
                return true;

            case "update":
                if (!string.IsNullOrWhiteSpace(parsed.Source) && !packageManager.ContainsSource(parsed.Source))
                {
                    error.WriteLine($"No matching package found for {parsed.Source}");
                    exitCode = 1;
                    return true;
                }

                output.WriteLine(string.IsNullOrWhiteSpace(parsed.Source)
                    ? "Updated packages"
                    : $"Updated {parsed.Source}");
                return true;

            case "config":
                PrintConfig(packageManager, output);
                return true;

            default:
                return false;
        }
    }

    private static ParsedPackageCommand Parse(string command, IReadOnlyList<string> args)
    {
        var local = false;
        var help = false;
        string? invalidOption = null;
        string? source = null;

        foreach (var arg in args)
        {
            if (arg is "-h" or "--help")
            {
                help = true;
                continue;
            }

            if (arg is "-l" or "--local")
            {
                if (command is "install" or "remove")
                {
                    local = true;
                }
                else
                {
                    invalidOption ??= arg;
                }

                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                invalidOption ??= arg;
                continue;
            }

            source ??= arg;
        }

        return new ParsedPackageCommand(source, local, help, invalidOption);
    }

    private static void PrintList(CodingAgentPackageManager manager, TextWriter output)
    {
        var packages = manager.ListConfiguredPackages();
        if (packages.Count == 0)
        {
            output.WriteLine("No packages installed.");
            return;
        }

        PrintScope("User packages:", packages.Where(static package => package.Scope == "user"), output);
        if (packages.Any(static package => package.Scope == "user") &&
            packages.Any(static package => package.Scope == "project"))
        {
            output.WriteLine();
        }

        PrintScope("Project packages:", packages.Where(static package => package.Scope == "project"), output);
    }

    private static void PrintScope(string heading, IEnumerable<CodingAgentConfiguredPackage> packages, TextWriter output)
    {
        var items = packages.ToArray();
        if (items.Length == 0)
        {
            return;
        }

        output.WriteLine(heading);
        foreach (var package in items)
        {
            output.WriteLine($"  {(package.Filtered ? package.Source + " (filtered)" : package.Source)}");
            if (!string.IsNullOrWhiteSpace(package.InstalledPath))
            {
                output.WriteLine($"    {package.InstalledPath}");
            }
        }
    }

    private static void PrintConfig(CodingAgentPackageManager manager, TextWriter output)
    {
        var resources = manager.ResolveResources();
        output.WriteLine($"user settings: {manager.UserSettingsPath}");
        output.WriteLine($"project settings: {manager.ProjectSettingsPath}");
        output.WriteLine($"packages: {manager.ListConfiguredPackages().Count}");
        output.WriteLine(
            $"resources: {resources.ExtensionPaths.Count} extensions, {resources.SkillPaths.Count} skills, {resources.PromptPaths.Count} prompts, {resources.ThemePaths.Count} themes");
        output.WriteLine($"issues: {resources.Diagnostics.Count}");
    }

    private static string GetUsage(string command) =>
        command switch
        {
            "install" => "tau install <source> [-l]",
            "remove" => "tau remove <source> [-l]",
            "update" => "tau update [source]",
            "list" => "tau list",
            "config" => "tau config",
            _ => "tau"
        };

    private static string GetHelp(string command) =>
        command switch
        {
            "install" => """
                Usage:
                  tau install <source> [-l]

                Install a package source and add it to settings.

                Options:
                  -l, --local    Install project-locally (.tau/coding-agent-settings.json)

                Examples:
                  tau install npm:@foo/bar
                  tau install git:github.com/user/repo
                  tau install ./local/path
                """,
            "remove" => """
                Usage:
                  tau remove <source> [-l]

                Remove a package source from settings.
                Alias: tau uninstall <source> [-l]

                Options:
                  -l, --local    Remove from project settings (.tau/coding-agent-settings.json)
                """,
            "update" => """
                Usage:
                  tau update [source]

                Refresh configured package sources. Tau currently resolves local package resources and leaves npm/git install execution open.
                """,
            "list" => """
                Usage:
                  tau list

                List installed package sources from user and project settings.
                """,
            "config" => """
                Usage:
                  tau config

                Show Tau package settings paths and resolved package resource counts.
                """,
            _ => GetUsage(command)
        };

    private sealed record ParsedPackageCommand(
        string? Source,
        bool Local,
        bool Help,
        string? InvalidOption);
}

internal sealed class CodingAgentPackageSourceJsonConverter : JsonConverter<CodingAgentPackageSource>
{
    public override CodingAgentPackageSource? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new CodingAgentPackageSource(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("package source must be a string or object");
        }

        string? source = null;
        IReadOnlyList<string>? extensions = null;
        IReadOnlyList<string>? skills = null;
        IReadOnlyList<string>? prompts = null;
        IReadOnlyList<string>? themes = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new CodingAgentPackageSource(source ?? string.Empty, extensions, skills, prompts, themes);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("invalid package source object");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("invalid package source object");
            }

            switch (propertyName)
            {
                case "source":
                    source = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    break;
                case "extensions":
                    extensions = ReadStringArray(ref reader);
                    break;
                case "skills":
                    skills = ReadStringArray(ref reader);
                    break;
                case "prompts":
                    prompts = ReadStringArray(ref reader);
                    break;
                case "themes":
                    themes = ReadStringArray(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("unterminated package source object");
    }

    public override void Write(
        Utf8JsonWriter writer,
        CodingAgentPackageSource value,
        JsonSerializerOptions options)
    {
        if (!value.IsFiltered)
        {
            writer.WriteStringValue(value.Source);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("source", value.Source);
        WriteStringArray(writer, "extensions", value.Extensions);
        WriteStringArray(writer, "skills", value.Skills);
        WriteStringArray(writer, "prompts", value.Prompts);
        WriteStringArray(writer, "themes", value.Themes);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<string> ReadStringArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return [];
        }

        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return values;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                values.Add(reader.GetString() ?? string.Empty);
            }
            else
            {
                reader.Skip();
            }
        }

        throw new JsonException("unterminated string array");
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
