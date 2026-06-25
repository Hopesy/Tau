using System.Diagnostics;
using System.Text.RegularExpressions;
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

public sealed record CodingAgentPackageCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);

public sealed record CodingAgentPackageCommandResult(
    int ExitCode,
    string StandardOutput = "",
    string StandardError = "");

public interface ICodingAgentPackageCommandRunner
{
    CodingAgentPackageCommandResult Run(CodingAgentPackageCommand command);
}

public sealed class SystemCodingAgentPackageCommandRunner : ICodingAgentPackageCommandRunner
{
    public CodingAgentPackageCommandResult Run(CodingAgentPackageCommand command)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            startInfo.WorkingDirectory = command.WorkingDirectory;
        }

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"Package command failed to start: {command.FileName}", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        return new CodingAgentPackageCommandResult(process.ExitCode, stdout.Result, stderr.Result);
    }
}

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
    private const string ProjectConfigDirectoryName = ".tau";

    private readonly string _cwd;
    private readonly ICodingAgentPackageCommandRunner _commandRunner;
    private string? _globalNpmRoot;
    private string? _globalNpmRootCommandKey;

    public CodingAgentPackageManager(
        string? cwd = null,
        string? userSettingsPath = null,
        string? projectSettingsPath = null,
        ICodingAgentPackageCommandRunner? commandRunner = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        UserSettingsPath = string.IsNullOrWhiteSpace(userSettingsPath)
            ? GetDefaultUserSettingsPath()
            : Path.GetFullPath(userSettingsPath);
        ProjectSettingsPath = string.IsNullOrWhiteSpace(projectSettingsPath)
            ? CodingAgentSettingsStore.GetDefaultPath()
            : Path.GetFullPath(projectSettingsPath);
        UserInstallDirectory = Path.GetDirectoryName(UserSettingsPath) ?? Path.Combine(_cwd, ProjectConfigDirectoryName);
        _commandRunner = commandRunner ?? new SystemCodingAgentPackageCommandRunner();
    }

    public string UserSettingsPath { get; }

    public string ProjectSettingsPath { get; }

    public string UserInstallDirectory { get; }

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

    public void InstallAndPersist(string source, bool local)
    {
        Install(source, local);
        AddSource(source, local);
    }

    public void Install(string source, bool local)
    {
        var normalized = NormalizeSource(source) ?? throw new ArgumentException("Package source is required.", nameof(source));
        var parsed = ParseSource(normalized);
        var scope = local ? "project" : "user";

        switch (parsed)
        {
            case NpmPackageSource npm:
                InstallNpm(npm, scope, temporary: false);
                break;
            case GitPackageSource git:
                InstallGit(git, scope);
                break;
            case LocalPackageSource localSource:
                var resolved = ResolvePath(localSource.Path, _cwd);
                if (!Directory.Exists(resolved))
                {
                    throw new DirectoryNotFoundException($"Path does not exist: {resolved}");
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported install source: {normalized}");
        }
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
        var scope = local ? "project" : "user";
        if (packages.Any(package => SourcesMatch(package.Source, normalized, scope)))
        {
            return false;
        }

        packages.Add(new CodingAgentPackageSource(normalized));
        store.Save(snapshot with { Packages = packages });
        return true;
    }

    public bool RemoveAndPersist(string source, bool local)
    {
        Remove(source, local);
        return RemoveSource(source, local);
    }

    public void Remove(string source, bool local)
    {
        var normalized = NormalizeSource(source) ?? throw new ArgumentException("Package source is required.", nameof(source));
        var parsed = ParseSource(normalized);
        var scope = local ? "project" : "user";

        switch (parsed)
        {
            case NpmPackageSource npm:
                UninstallNpm(npm, scope);
                break;
            case GitPackageSource git:
                RemoveGit(git, scope);
                break;
            case LocalPackageSource:
                break;
            default:
                throw new InvalidOperationException($"Unsupported remove source: {normalized}");
        }
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
        var scope = local ? "project" : "user";
        var after = before
            .Where(package => !SourcesMatch(package.Source, normalized, scope))
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
            ListConfiguredPackages().Any(package => SourcesMatch(package.Source, normalized, package.Scope));
    }

    public void Update(string? source = null)
    {
        if (IsOfflineModeEnabled())
        {
            return;
        }

        var normalizedSource = NormalizeSource(source);
        var configured = EnumerateConfiguredSources().ToArray();
        var matched = false;

        foreach (var configuredSource in configured)
        {
            if (normalizedSource is not null &&
                !SourcesMatch(configuredSource.Source.Source, normalizedSource, configuredSource.Scope))
            {
                continue;
            }

            matched = true;
            UpdateSourceForScope(configuredSource.Source.Source, configuredSource.Scope);
        }

        if (normalizedSource is not null && !matched)
        {
            throw new InvalidOperationException(BuildNoMatchingPackageMessage(normalizedSource, configured));
        }
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
            if (!TryResolvePackageRoot(configured.Source.Source, configured.Scope, allowExternalLookup: true, out var root, out var issue))
            {
                diagnostics.Add(new CodingAgentPackageDiagnostic(
                    "warning",
                    issue ?? "package source could not be resolved",
                    configured.Source.Source,
                    configured.Scope));
                continue;
            }

            if (!Directory.Exists(root))
            {
                diagnostics.Add(new CodingAgentPackageDiagnostic(
                    "warning",
                    "package install path does not exist",
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
                TryResolvePackageRoot(source.Source, scope, allowExternalLookup: false, out var root, out _) &&
                    Directory.Exists(root)
                    ? root
                    : null));
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
            AddFilteredResourceEntries(root, source.Extensions, "extensions", extensions);
            AddFilteredResourceEntries(root, source.Skills, "skills", skills);
            AddFilteredResourceEntries(root, source.Prompts, "prompts", prompts);
            AddFilteredResourceEntries(root, source.Themes, "themes", themes);
            return;
        }

        var manifest = ReadManifest(root, source, scope, diagnostics);
        if (manifest is not null)
        {
            AddManifestResourceEntries(root, manifest.Extensions, "extensions", extensions);
            AddManifestResourceEntries(root, manifest.Skills, "skills", skills);
            AddManifestResourceEntries(root, manifest.Prompts, "prompts", prompts);
            AddManifestResourceEntries(root, manifest.Themes, "themes", themes);
            return;
        }

        AddConventionResourceEntries(root, "extensions", extensions);
        AddConventionResourceEntries(root, "skills", skills);
        AddConventionResourceEntries(root, "prompts", prompts);
        AddConventionResourceEntries(root, "themes", themes);
    }

    private static void AddConventionResourceEntries(string root, string resourceType, ICollection<string> target)
    {
        var directory = Path.Combine(root, resourceType);
        if (Directory.Exists(directory))
        {
            if (resourceType.Equals("extensions", StringComparison.Ordinal))
            {
                foreach (var entry in CollectExtensionEntries(directory))
                {
                    target.Add(entry);
                }
            }
            else
            {
                target.Add(directory);
            }
        }
    }

    private void AddManifestResourceEntries(
        string root,
        IReadOnlyList<string>? entries,
        string resourceType,
        ICollection<string> target)
    {
        if (entries is null)
        {
            return;
        }

        if (!entries.Any(IsPatternEntry))
        {
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    var resolved = ResolvePath(entry, root);
                    if (resourceType.Equals("extensions", StringComparison.Ordinal))
                    {
                        foreach (var path in CollectResourceFilesFromPath(resolved, resourceType))
                        {
                            target.Add(path);
                        }
                    }
                    else
                    {
                        target.Add(resolved);
                    }
                }
            }

            return;
        }

        var candidates = CollectFilesFromResourceEntries(root, entries, resourceType);
        var overridePatterns = GetOverridePatterns(entries);
        foreach (var path in ApplyResourcePatterns(candidates, overridePatterns, root))
        {
            target.Add(path);
        }
    }

    private void AddFilteredResourceEntries(
        string root,
        IReadOnlyList<string>? entries,
        string resourceType,
        ICollection<string> target)
    {
        if (entries is null)
        {
            AddDefaultResourceEntries(root, resourceType, target);
            return;
        }

        if (entries.Count == 0)
        {
            return;
        }

        var candidates = CollectDefaultResourceFiles(root, resourceType);
        foreach (var path in ApplyResourcePatterns(candidates, entries, root))
        {
            target.Add(path);
        }
    }

    private void AddDefaultResourceEntries(
        string root,
        string resourceType,
        ICollection<string> target)
    {
        var manifest = ReadManifest(root, new CodingAgentPackageSource(root), "package", new List<CodingAgentPackageDiagnostic>());
        var entries = GetResourceEntries(manifest, resourceType);
        if (entries is not null)
        {
            AddManifestResourceEntries(root, entries, resourceType, target);
            return;
        }

        AddConventionResourceEntries(root, resourceType, target);
    }

    private static bool IsOverridePattern(string value) =>
        value.StartsWith("!", StringComparison.Ordinal) ||
        value.StartsWith("+", StringComparison.Ordinal) ||
        value.StartsWith("-", StringComparison.Ordinal);

    private static IReadOnlyList<string> GetOverridePatterns(IReadOnlyList<string> entries) =>
        entries.Where(IsOverridePattern).ToArray();

    private static bool IsPatternEntry(string value) =>
        IsOverridePattern(value) ||
        value.Contains('*', StringComparison.Ordinal) ||
        value.Contains('?', StringComparison.Ordinal);

    private static IReadOnlyList<string>? GetResourceEntries(CodingAgentPackageSource? source, string resourceType) =>
        resourceType switch
        {
            "extensions" => source?.Extensions,
            "skills" => source?.Skills,
            "prompts" => source?.Prompts,
            "themes" => source?.Themes,
            _ => null
        };

    private static IReadOnlyList<string> CollectDefaultResourceFiles(string root, string resourceType)
    {
        var packageJsonPath = Path.Combine(root, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
                if (document.RootElement.TryGetProperty("pi", out var pi) &&
                    pi.ValueKind == JsonValueKind.Object)
                {
                    var entries = ReadStringArray(pi, resourceType);
                    if (entries is not null)
                    {
                        return ApplyResourcePatterns(
                            CollectFilesFromResourceEntries(root, entries, resourceType),
                            GetOverridePatterns(entries),
                            root);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        var conventionDirectory = Path.Combine(root, resourceType);
        return CollectResourceFilesFromPath(conventionDirectory, resourceType);
    }

    private static IReadOnlyList<string> CollectFilesFromResourceEntries(
        string root,
        IReadOnlyList<string> entries,
        string resourceType)
    {
        var files = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry) || IsOverridePattern(entry))
            {
                continue;
            }

            if (HasGlob(entry))
            {
                foreach (var match in EnumerateGlobMatches(root, entry))
                {
                    files.AddRange(CollectResourceFilesFromPath(match, resourceType));
                }
            }
            else
            {
                files.AddRange(CollectResourceFilesFromPath(ResolvePath(entry, root), resourceType));
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> CollectResourceFilesFromPath(string path, string resourceType)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path))
        {
            return [];
        }

        if (resourceType.Equals("extensions", StringComparison.Ordinal))
        {
            return CollectExtensionEntries(path);
        }

        if (File.Exists(path))
        {
            return [Path.GetFullPath(path)];
        }

        var pattern = resourceType switch
        {
            "extensions" => "*.json",
            "skills" => "SKILL.md",
            "prompts" => "*.md",
            "themes" => "*.json",
            _ => "*"
        };
        try
        {
            return Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)
                .Where(file => !IsUnderNodeModules(file) && !Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal))
                .Select(Path.GetFullPath)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> CollectExtensionEntries(string path)
    {
        if (File.Exists(path))
        {
            return IsExtensionEntryFile(path) ? [Path.GetFullPath(path)] : [];
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        var rootEntries = ResolveExtensionEntries(path);
        if (rootEntries is not null)
        {
            return rootEntries;
        }

        var entries = new List<string>();
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(path)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (IsIgnoredExtensionEntryName(name))
            {
                continue;
            }

            if (File.Exists(child))
            {
                if (IsExtensionEntryFile(child))
                {
                    entries.Add(Path.GetFullPath(child));
                }

                continue;
            }

            if (!Directory.Exists(child))
            {
                continue;
            }

            var resolved = ResolveExtensionEntries(child);
            if (resolved is not null)
            {
                entries.AddRange(resolved);
            }
        }

        return entries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string>? ResolveExtensionEntries(string directory)
    {
        var manifestEntries = ResolveExtensionManifestEntries(directory);
        if (manifestEntries.Count > 0)
        {
            return manifestEntries;
        }

        var indexTs = Path.Combine(directory, "index.ts");
        if (File.Exists(indexTs))
        {
            return [Path.GetFullPath(indexTs)];
        }

        var indexJs = Path.Combine(directory, "index.js");
        if (File.Exists(indexJs))
        {
            return [Path.GetFullPath(indexJs)];
        }

        var indexJson = Path.Combine(directory, "index.json");
        if (File.Exists(indexJson))
        {
            return [Path.GetFullPath(indexJson)];
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveExtensionManifestEntries(string directory)
    {
        var packageJsonPath = Path.Combine(directory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return [];
        }

        IReadOnlyList<string>? configured;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            configured = document.RootElement.TryGetProperty("pi", out var pi) && pi.ValueKind == JsonValueKind.Object
                ? ReadStringArray(pi, "extensions")
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (configured is null || configured.Count == 0)
        {
            return [];
        }

        var entries = new List<string>();
        foreach (var entry in configured)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var resolved = ResolvePath(entry, directory);
            if (File.Exists(resolved))
            {
                if (IsExtensionEntryFile(resolved))
                {
                    entries.Add(Path.GetFullPath(resolved));
                }

                continue;
            }

            if (Directory.Exists(resolved) && !PathsEqual(resolved, directory))
            {
                entries.AddRange(CollectExtensionEntries(resolved));
            }
        }

        return entries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsExtensionEntryFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredExtensionEntryName(string name) =>
        string.IsNullOrWhiteSpace(name) ||
        name.StartsWith(".", StringComparison.Ordinal) ||
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ApplyResourcePatterns(
        IReadOnlyList<string> allFiles,
        IReadOnlyList<string> patterns,
        string root)
    {
        if (allFiles.Count == 0)
        {
            return [];
        }

        var includes = new List<string>();
        var excludes = new List<string>();
        var forceIncludes = new List<string>();
        var forceExcludes = new List<string>();

        foreach (var rawPattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(rawPattern))
            {
                continue;
            }

            var pattern = rawPattern.Trim();
            if (pattern.StartsWith("+", StringComparison.Ordinal))
            {
                forceIncludes.Add(pattern[1..]);
            }
            else if (pattern.StartsWith("-", StringComparison.Ordinal))
            {
                forceExcludes.Add(pattern[1..]);
            }
            else if (pattern.StartsWith("!", StringComparison.Ordinal))
            {
                excludes.Add(pattern[1..]);
            }
            else
            {
                includes.Add(pattern);
            }
        }

        var result = includes.Count == 0
            ? allFiles.ToList()
            : allFiles.Where(file => MatchesAnyPattern(file, includes, root)).ToList();

        if (excludes.Count > 0)
        {
            result = result.Where(file => !MatchesAnyPattern(file, excludes, root)).ToList();
        }

        foreach (var file in allFiles)
        {
            if (!result.Contains(file, StringComparer.OrdinalIgnoreCase) &&
                MatchesAnyExactPattern(file, forceIncludes, root))
            {
                result.Add(file);
            }
        }

        if (forceExcludes.Count > 0)
        {
            result = result.Where(file => !MatchesAnyExactPattern(file, forceExcludes, root)).ToList();
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateGlobMatches(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (!IsUnderNodeModules(entry) && MatchesPattern(entry, pattern, root))
            {
                yield return entry;
            }
        }
    }

    private static bool MatchesAnyPattern(string path, IEnumerable<string> patterns, string root) =>
        patterns.Any(pattern => MatchesPattern(path, pattern, root));

    private static bool MatchesAnyExactPattern(string path, IEnumerable<string> patterns, string root) =>
        patterns.Any(pattern => MatchesExactPattern(path, pattern, root));

    private static bool MatchesPattern(string path, string pattern, string root)
    {
        var normalizedPattern = NormalizePackagePattern(pattern);
        var rel = ToPosixPath(Path.GetRelativePath(root, path));
        var name = Path.GetFileName(path);
        var full = ToPosixPath(Path.GetFullPath(path));

        if (GlobMatches(rel, normalizedPattern) ||
            GlobMatches(name, normalizedPattern) ||
            GlobMatches(full, normalizedPattern))
        {
            return true;
        }

        if (!Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        var parentRel = ToPosixPath(Path.GetRelativePath(root, parent));
        var parentName = Path.GetFileName(parent);
        var parentFull = ToPosixPath(Path.GetFullPath(parent));
        return GlobMatches(parentRel, normalizedPattern) ||
            GlobMatches(parentName, normalizedPattern) ||
            GlobMatches(parentFull, normalizedPattern);
    }

    private static bool MatchesExactPattern(string path, string pattern, string root)
    {
        var normalizedPattern = NormalizeExactPattern(pattern);
        var rel = ToPosixPath(Path.GetRelativePath(root, path));
        var full = ToPosixPath(Path.GetFullPath(path));
        if (rel.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase) ||
            full.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        var parentRel = ToPosixPath(Path.GetRelativePath(root, parent));
        var parentFull = ToPosixPath(Path.GetFullPath(parent));
        return parentRel.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase) ||
            parentFull.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool GlobMatches(string value, string pattern) =>
        Regex.IsMatch(ToPosixPath(value), GlobToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string GlobToRegex(string pattern)
    {
        var normalized = NormalizePackagePattern(pattern);
        var builder = new System.Text.StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (ch == '*')
            {
                if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        i++;
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(ch.ToString()));
            }
        }

        builder.Append('$');
        return builder.ToString();
    }

    private static string NormalizePackagePattern(string pattern) =>
        ToPosixPath(pattern.Trim());

    private static string NormalizeExactPattern(string pattern)
    {
        var normalized = NormalizePackagePattern(pattern);
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static bool HasGlob(string value) =>
        value.Contains('*', StringComparison.Ordinal) ||
        value.Contains('?', StringComparison.Ordinal);

    private static string ToPosixPath(string path) =>
        path.Replace('\\', '/');

    private static bool IsUnderNodeModules(string path) =>
        ToPosixPath(Path.GetFullPath(path)).Contains("/node_modules/", StringComparison.OrdinalIgnoreCase);

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

    private bool TryResolvePackageRoot(
        string source,
        string scope,
        bool allowExternalLookup,
        out string root,
        out string? issue)
    {
        root = string.Empty;
        issue = null;
        var parsed = ParseSource(source);

        switch (parsed)
        {
            case LocalPackageSource local:
                root = ResolvePath(local.Path, _cwd);
                return true;
            case GitPackageSource git:
                root = GetGitInstallPath(git, scope);
                return true;
            case NpmPackageSource npm:
                return TryGetNpmInstallPath(npm, scope, allowExternalLookup, out root, out issue);
            default:
                issue = "unsupported package source";
                return false;
        }
    }

    private bool TryGetNpmInstallPath(
        NpmPackageSource source,
        string scope,
        bool allowExternalLookup,
        out string root,
        out string? issue)
    {
        issue = null;
        root = string.Empty;
        if (scope == "project")
        {
            root = Path.Combine(GetNpmInstallRoot(scope), "node_modules", source.Name);
            return true;
        }

        if (!allowExternalLookup)
        {
            return false;
        }

        try
        {
            root = Path.Combine(GetGlobalNpmRoot(), source.Name);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            issue = $"npm package install root could not be resolved: {ex.Message}";
            return false;
        }
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

    private void UpdateSourceForScope(string source, string scope)
    {
        var parsed = ParseSource(source);
        switch (parsed)
        {
            case NpmPackageSource npm when !npm.Pinned:
                InstallNpm(npm with { Spec = $"{npm.Name}@latest" }, scope, temporary: false);
                break;
            case GitPackageSource git when !git.Pinned:
                UpdateGit(git, scope);
                break;
        }
    }

    private void InstallNpm(NpmPackageSource source, string scope, bool temporary)
    {
        if (scope == "user" && !temporary)
        {
            RunNpmCommand(["install", "-g", source.Spec]);
            return;
        }

        var installRoot = GetNpmInstallRoot(scope);
        EnsureNpmProject(installRoot);
        RunNpmCommand(["install", source.Spec, "--prefix", installRoot]);
    }

    private void UninstallNpm(NpmPackageSource source, string scope)
    {
        if (scope == "user")
        {
            RunNpmCommand(["uninstall", "-g", source.Name]);
            return;
        }

        var installRoot = GetNpmInstallRoot(scope);
        if (!Directory.Exists(installRoot))
        {
            return;
        }

        RunNpmCommand(["uninstall", source.Name, "--prefix", installRoot]);
    }

    private void InstallGit(GitPackageSource source, string scope)
    {
        var targetDir = GetGitInstallPath(source, scope);
        if (Directory.Exists(targetDir))
        {
            return;
        }

        var gitRoot = GetGitInstallRoot(scope);
        EnsureGitIgnore(gitRoot);
        var parent = Path.GetDirectoryName(targetDir);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        RunCommand("git", ["clone", source.Repo, targetDir]);
        if (!string.IsNullOrWhiteSpace(source.Ref))
        {
            RunCommand("git", ["checkout", source.Ref], targetDir);
        }

        if (File.Exists(Path.Combine(targetDir, "package.json")))
        {
            RunNpmCommand(["install", "--omit=dev"], targetDir);
        }
    }

    private void UpdateGit(GitPackageSource source, string scope)
    {
        var targetDir = GetGitInstallPath(source, scope);
        if (!Directory.Exists(targetDir))
        {
            InstallGit(source, scope);
            return;
        }

        RunCommand("git", ["pull", "--ff-only"], targetDir);
        if (File.Exists(Path.Combine(targetDir, "package.json")))
        {
            RunNpmCommand(["install", "--omit=dev"], targetDir);
        }
    }

    private void RemoveGit(GitPackageSource source, string scope)
    {
        var targetDir = GetGitInstallPath(source, scope);
        var gitRoot = GetGitInstallRoot(scope);
        if (!Directory.Exists(targetDir))
        {
            return;
        }

        if (!IsPathUnder(targetDir, gitRoot) || PathsEqual(targetDir, gitRoot))
        {
            throw new InvalidOperationException($"Refusing to remove package path outside install root: {targetDir}");
        }

        Directory.Delete(targetDir, recursive: true);
        PruneEmptyGitParents(targetDir, gitRoot);
    }

    private void PruneEmptyGitParents(string targetDir, string installRoot)
    {
        var current = Path.GetDirectoryName(targetDir);
        while (!string.IsNullOrWhiteSpace(current) &&
               IsPathUnder(current, installRoot) &&
               !PathsEqual(current, installRoot))
        {
            if (Directory.Exists(current) && Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            if (Directory.Exists(current))
            {
                Directory.Delete(current);
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private void EnsureNpmProject(string installRoot)
    {
        EnsureGitIgnore(installRoot);
        var packageJsonPath = Path.Combine(installRoot, "package.json");
        if (File.Exists(packageJsonPath))
        {
            return;
        }

        File.WriteAllText(
            packageJsonPath,
            """
            {
              "name": "tau-extensions",
              "private": true
            }
            """);
    }

    private static void EnsureGitIgnore(string directory)
    {
        Directory.CreateDirectory(directory);
        var ignorePath = Path.Combine(directory, ".gitignore");
        if (!File.Exists(ignorePath))
        {
            File.WriteAllText(ignorePath, "*\n!.gitignore\n");
        }
    }

    private string GetNpmInstallRoot(string scope)
    {
        if (scope == "project")
        {
            return Path.Combine(_cwd, ProjectConfigDirectoryName, "npm");
        }

        return Path.GetDirectoryName(GetGlobalNpmRoot()) ?? GetGlobalNpmRoot();
    }

    private string GetGlobalNpmRoot()
    {
        var npmCommand = GetNpmCommand();
        var commandKey = string.Join('\0', new[] { npmCommand.Command }.Concat(npmCommand.Arguments));
        if (_globalNpmRoot is not null && _globalNpmRootCommandKey == commandKey)
        {
            return _globalNpmRoot;
        }

        var result = RunCommandCapture(npmCommand.Command, [.. npmCommand.Arguments, "root", "-g"], _cwd);
        var root = result.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("npm root -g returned an empty install root.");
        }

        _globalNpmRoot = root;
        _globalNpmRootCommandKey = commandKey;
        return _globalNpmRoot;
    }

    private string GetGitInstallPath(GitPackageSource source, string scope) =>
        Path.Combine(GetGitInstallRoot(scope), source.Host, Path.Combine(source.Path.Split('/')));

    private string GetGitInstallRoot(string scope) =>
        scope == "project"
            ? Path.Combine(_cwd, ProjectConfigDirectoryName, "git")
            : Path.Combine(UserInstallDirectory, "git");

    private NpmCommand GetNpmCommand()
    {
        var command = new CodingAgentSettingsStore(ProjectSettingsPath).Load().NpmCommand ??
            new CodingAgentSettingsStore(UserSettingsPath).Load().NpmCommand;

        if (command is null || command.Count == 0)
        {
            return new NpmCommand("npm", []);
        }

        var executable = command[0];
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Invalid npmCommand: first array entry must be a non-empty command");
        }

        return new NpmCommand(executable, command.Skip(1).ToArray());
    }

    private void RunNpmCommand(IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var command = GetNpmCommand();
        RunCommand(command.Command, [.. command.Arguments, .. arguments], workingDirectory ?? _cwd);
    }

    private void RunCommand(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var result = _commandRunner.Run(new CodingAgentPackageCommand(fileName, arguments, workingDirectory));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildCommandFailureMessage(fileName, result));
        }
    }

    private string RunCommandCapture(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var result = _commandRunner.Run(new CodingAgentPackageCommand(fileName, arguments, workingDirectory));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildCommandFailureMessage(fileName, result));
        }

        return result.StandardOutput;
    }

    private static string BuildCommandFailureMessage(string fileName, CodingAgentPackageCommandResult result)
    {
        var detail = result.StandardError;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = result.StandardOutput;
        }

        detail = CodingAgentSecretRedactor.Default.Redact(detail?.Trim());
        if (string.IsNullOrWhiteSpace(detail))
        {
            return $"Package command failed: {fileName} exited with code {result.ExitCode}";
        }

        return $"Package command failed: {fileName} exited with code {result.ExitCode}: {Truncate(detail, 500)}";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static bool IsOfflineModeEnabled()
    {
        var value = Environment.GetEnvironmentVariable("PI_OFFLINE");
        return value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static ParsedPackageSource ParseSource(string source)
    {
        var normalized = NormalizeSource(source) ?? string.Empty;
        if (normalized.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
        {
            var spec = normalized["npm:".Length..].Trim();
            var (name, version) = ParseNpmSpec(spec);
            return new NpmPackageSource(normalized, spec, name, !string.IsNullOrWhiteSpace(version));
        }

        var localSource = normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? normalized["file:".Length..].Trim()
            : normalized;
        if (IsLocalSource(localSource))
        {
            return new LocalPackageSource(normalized, localSource);
        }

        if (CodingAgentGitUrlParser.TryParse(normalized, out var git))
        {
            return new GitPackageSource(
                normalized,
                git.Repo,
                git.Host,
                git.Path,
                git.Ref,
                git.Pinned);
        }

        return new LocalPackageSource(normalized, localSource);
    }

    private static (string Name, string? Version) ParseNpmSpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return (spec, null);
        }

        var versionSeparator = -1;
        if (spec.StartsWith("@", StringComparison.Ordinal))
        {
            var slash = spec.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                versionSeparator = spec.IndexOf('@', slash + 1);
            }
        }
        else
        {
            versionSeparator = spec.IndexOf('@', StringComparison.Ordinal);
        }

        return versionSeparator > 0
            ? (spec[..versionSeparator], spec[(versionSeparator + 1)..])
            : (spec, null);
    }

    private bool SourcesMatch(string configuredSource, string inputSource, string scope) =>
        GetSourceMatchKey(configuredSource, scope)
            .Equals(GetSourceMatchKey(inputSource, scope), StringComparison.OrdinalIgnoreCase);

    private string GetSourceMatchKey(string source, string scope)
    {
        var parsed = ParseSource(source);
        return parsed switch
        {
            NpmPackageSource npm => $"npm:{npm.Name}",
            GitPackageSource git => $"git:{git.Host}/{git.Path}",
            LocalPackageSource local => $"local:{ResolvePath(local.Path, scope == "project" ? _cwd : UserInstallDirectory)}",
            _ => source
        };
    }

    private string BuildNoMatchingPackageMessage(
        string source,
        IReadOnlyList<(CodingAgentPackageSource Source, string Scope)> configured)
    {
        var suggestion = configured
            .Select(package => package.Source.Source)
            .FirstOrDefault(configuredSource => SourcesMatch(configuredSource, source, "project"));
        return suggestion is null
            ? $"No matching package found for {source}"
            : $"No matching package found for {source}. Did you mean {suggestion}?";
    }

    private static bool IsPathUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase);
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

    private sealed record NpmCommand(string Command, IReadOnlyList<string> Arguments);

    private abstract record ParsedPackageSource(string Source);

    private sealed record NpmPackageSource(
        string Source,
        string Spec,
        string Name,
        bool Pinned) : ParsedPackageSource(Source);

    private sealed record GitPackageSource(
        string Source,
        string Repo,
        string Host,
        string Path,
        string? Ref,
        bool Pinned) : ParsedPackageSource(Source);

    private sealed record LocalPackageSource(string Source, string Path) : ParsedPackageSource(Source);
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

                try
                {
                    packageManager.InstallAndPersist(parsed.Source, parsed.Local);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"Error: {ex.Message}");
                    exitCode = 1;
                    return true;
                }

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

                bool removed;
                try
                {
                    removed = packageManager.RemoveAndPersist(parsed.Source, parsed.Local);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"Error: {ex.Message}");
                    exitCode = 1;
                    return true;
                }

                if (!removed)
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
                try
                {
                    packageManager.Update(parsed.Source);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"Error: {ex.Message}");
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

                Refresh configured package sources and update npm/git package installs when applicable.
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
