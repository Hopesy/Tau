using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentExtensionCommand(
    string Name,
    string InvocationName,
    string Description,
    string? ArgumentHint,
    string? Response,
    string? Prompt,
    bool SendToRunner,
    string FilePath,
    string Scope);

public sealed record CodingAgentExtensionCommandInvocation(
    bool Handled,
    bool IsError,
    bool SendToRunner,
    string Message,
    CodingAgentExtensionCommand Command)
{
    public static CodingAgentExtensionCommandInvocation Status(CodingAgentExtensionCommand command, string message) =>
        new(true, false, false, message, command);

    public static CodingAgentExtensionCommandInvocation Runner(CodingAgentExtensionCommand command, string message) =>
        new(true, false, true, message, command);

    public static CodingAgentExtensionCommandInvocation Error(CodingAgentExtensionCommand command, string message) =>
        new(true, true, false, message, command);
}

public sealed record CodingAgentExtensionResources(
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptPaths,
    IReadOnlyList<string> ThemePaths)
{
    public CodingAgentExtensionResources(
        IReadOnlyList<string> skillPaths,
        IReadOnlyList<string> promptPaths)
        : this(skillPaths, promptPaths, [])
    {
    }
}

public sealed record CodingAgentExtensionFileStatus(
    string FilePath,
    string Scope,
    int CommandCount,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptPaths,
    IReadOnlyList<string> ThemePaths);

public sealed record CodingAgentExtensionDiagnostic(
    string Severity,
    string Message,
    string Path,
    string Scope);

public sealed record CodingAgentExtensionStatus(
    IReadOnlyList<CodingAgentExtensionCommand> Commands,
    CodingAgentExtensionResources Resources,
    IReadOnlyList<CodingAgentExtensionFileStatus> Files,
    IReadOnlyList<CodingAgentExtensionDiagnostic> Diagnostics);

public sealed class CodingAgentExtensionCommandStore
{
    public const string ExtensionPathsEnvironmentVariable = "TAU_CODING_AGENT_EXTENSION_PATHS";

    private readonly string _cwd;
    private readonly string _userExtensionsDirectory;
    private readonly IReadOnlyList<string> _explicitPaths;
    private readonly Func<IReadOnlyList<string>>? _additionalPathsProvider;
    private readonly bool _includeDefaults;

    public CodingAgentExtensionCommandStore(
        string? cwd = null,
        string? userExtensionsDirectory = null,
        IReadOnlyList<string>? explicitPaths = null,
        Func<IReadOnlyList<string>>? additionalPathsProvider = null,
        bool includeDefaults = true)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _userExtensionsDirectory = string.IsNullOrWhiteSpace(userExtensionsDirectory)
            ? GetDefaultUserExtensionsDirectory()
            : Path.GetFullPath(userExtensionsDirectory);
        _explicitPaths = explicitPaths ?? GetConfiguredExtensionPaths();
        _additionalPathsProvider = additionalPathsProvider;
        _includeDefaults = includeDefaults;
    }

    public IReadOnlyList<CodingAgentExtensionCommand> Load()
    {
        return LoadStatus().Commands;
    }

    public CodingAgentExtensionResources LoadResources()
    {
        return LoadStatus().Resources;
    }

    public CodingAgentExtensionStatus LoadStatus()
    {
        var definitions = new List<CommandDefinition>();
        var skillPaths = new List<string>();
        var promptPaths = new List<string>();
        var themePaths = new List<string>();
        var files = new List<CodingAgentExtensionFileStatus>();
        var diagnostics = new List<CodingAgentExtensionDiagnostic>();
        if (_includeDefaults)
        {
            LoadSourceDirectory(_userExtensionsDirectory, "user", definitions, skillPaths, promptPaths, themePaths, files, diagnostics);
            LoadSourceDirectory(Path.Combine(_cwd, ".tau", "extensions"), "project", definitions, skillPaths, promptPaths, themePaths, files, diagnostics);
        }

        foreach (var path in GetExplicitPaths())
        {
            var resolved = ResolvePath(path, _cwd);
            if (Directory.Exists(resolved))
            {
                LoadSourceDirectory(resolved, "path", definitions, skillPaths, promptPaths, themePaths, files, diagnostics, reportMissing: true);
            }
            else if (File.Exists(resolved) && Path.GetExtension(resolved).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                LoadSourceFile(resolved, "path", definitions, skillPaths, promptPaths, themePaths, files, diagnostics);
            }
            else if (File.Exists(resolved))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    "extension path is not a json file",
                    resolved,
                    "path"));
            }
            else
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    "extension path does not exist",
                    resolved,
                    "path"));
            }
        }

        var resources = new CodingAgentExtensionResources(
            skillPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            promptPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            themePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        return new CodingAgentExtensionStatus(
            ResolveInvocationNames(definitions),
            resources,
            files,
            diagnostics);
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

    public bool TryInvoke(string input, out CodingAgentExtensionCommandInvocation? invocation)
    {
        invocation = null;
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var spaceIndex = input.IndexOf(' ');
        var commandName = spaceIndex < 0 ? input[1..] : input[1..spaceIndex];
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return false;
        }

        var command = Load().FirstOrDefault(candidate =>
            candidate.InvocationName.Equals(commandName, StringComparison.Ordinal));
        if (command is null)
        {
            return false;
        }

        var argsText = spaceIndex < 0 ? string.Empty : input[(spaceIndex + 1)..];
        var args = CodingAgentPromptTemplateStore.ParseCommandArgs(argsText);
        var template = command.SendToRunner
            ? command.Prompt ?? command.Response
            : command.Response ?? command.Prompt;
        var message = string.IsNullOrWhiteSpace(template)
            ? string.Empty
            : CodingAgentPromptTemplateStore.SubstituteArgs(template, args);

        if (command.SendToRunner)
        {
            invocation = string.IsNullOrWhiteSpace(message)
                ? CodingAgentExtensionCommandInvocation.Error(
                    command,
                    $"extension command '/{command.InvocationName}' has no prompt or response")
                : CodingAgentExtensionCommandInvocation.Runner(command, message);
            return true;
        }

        invocation = CodingAgentExtensionCommandInvocation.Status(
            command,
            string.IsNullOrWhiteSpace(message)
                ? $"extension command '/{command.InvocationName}' completed"
                : message);
        return true;
    }

    private static void LoadSourceDirectory(
        string directory,
        string scope,
        ICollection<CommandDefinition> definitions,
        ICollection<string> skillPaths,
        ICollection<string> promptPaths,
        ICollection<string> themePaths,
        ICollection<CodingAgentExtensionFileStatus> fileStatuses,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics,
        bool reportMissing = false)
    {
        if (!Directory.Exists(directory))
        {
            if (reportMissing)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    "extension directory does not exist",
                    directory,
                    scope));
            }

            return;
        }

        IEnumerable<string> jsonFiles;
        try
        {
            jsonFiles = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .Where(static file => !IsIgnoredExtensionPath(file))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "warning",
                $"extension directory could not be read: {ex.Message}",
                directory,
                scope));
            return;
        }

        foreach (var file in jsonFiles)
        {
            LoadSourceFile(file, scope, definitions, skillPaths, promptPaths, themePaths, fileStatuses, diagnostics);
        }
    }

    private static void LoadSourceFile(
        string filePath,
        string scope,
        ICollection<CommandDefinition> definitions,
        ICollection<string> skillPaths,
        ICollection<string> promptPaths,
        ICollection<string> themePaths,
        ICollection<CodingAgentExtensionFileStatus> files,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                File.ReadAllText(filePath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "error",
                $"failed to load extension json: {ex.Message}",
                Path.GetFullPath(filePath),
                scope));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "warning",
                $"extension file could not be read: {ex.Message}",
                Path.GetFullPath(filePath),
                scope));
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    "extension json root must be an object",
                    Path.GetFullPath(filePath),
                    scope));
                return;
            }

            var commandCountBefore = definitions.Count;
            var fileSkillPaths = new List<string>();
            var filePromptPaths = new List<string>();
            var fileThemePaths = new List<string>();
            var commands = new List<CommandDefinition>();
            if (document.RootElement.TryGetProperty("commands", out var commandArray)
                && commandArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in commandArray.EnumerateArray())
                {
                    var command = ReadCommand(element, filePath, scope);
                    if (command is not null)
                    {
                        commands.Add(command);
                    }
                }
            }
            else
            {
                var singleCommand = ReadCommand(document.RootElement, filePath, scope);
                if (singleCommand is not null)
                {
                    commands.Add(singleCommand);
                }
            }

            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Environment.CurrentDirectory;
            AddPathArray(document.RootElement, "skillPaths", baseDirectory, fileSkillPaths);
            AddPathArray(document.RootElement, "skill-paths", baseDirectory, fileSkillPaths);
            AddPathArray(document.RootElement, "promptPaths", baseDirectory, filePromptPaths);
            AddPathArray(document.RootElement, "prompt-paths", baseDirectory, filePromptPaths);
            AddPathArray(document.RootElement, "themePaths", baseDirectory, fileThemePaths);
            AddPathArray(document.RootElement, "theme-paths", baseDirectory, fileThemePaths);
            if (document.RootElement.TryGetProperty("resources", out var resources)
                && resources.ValueKind == JsonValueKind.Object)
            {
                AddPathArray(resources, "skillPaths", baseDirectory, fileSkillPaths);
                AddPathArray(resources, "skill-paths", baseDirectory, fileSkillPaths);
                AddPathArray(resources, "promptPaths", baseDirectory, filePromptPaths);
                AddPathArray(resources, "prompt-paths", baseDirectory, filePromptPaths);
                AddPathArray(resources, "themePaths", baseDirectory, fileThemePaths);
                AddPathArray(resources, "theme-paths", baseDirectory, fileThemePaths);
            }

            foreach (var command in commands)
            {
                definitions.Add(command);
            }

            foreach (var path in fileSkillPaths)
            {
                skillPaths.Add(path);
            }

            foreach (var path in filePromptPaths)
            {
                promptPaths.Add(path);
            }

            foreach (var path in fileThemePaths)
            {
                themePaths.Add(path);
            }

            files.Add(new CodingAgentExtensionFileStatus(
                Path.GetFullPath(filePath),
                scope,
                definitions.Count - commandCountBefore,
                fileSkillPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                filePromptPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                fileThemePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
    }

    private static void AddPathArray(
        JsonElement element,
        string propertyName,
        string baseDirectory,
        ICollection<string> paths)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            AddPath(property.GetString(), baseDirectory, paths);
            return;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddPath(item.GetString(), baseDirectory, paths);
            }
        }
    }

    private static void AddPath(string? path, string baseDirectory, ICollection<string> paths)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        paths.Add(ResolvePath(path, baseDirectory));
    }

    private static CommandDefinition? ReadCommand(JsonElement element, string filePath, string scope)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = NormalizeName(ReadString(element, "name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new CommandDefinition(
            name,
            ReadString(element, "description")?.Trim() ?? string.Empty,
            ReadString(element, "argumentHint") ?? ReadString(element, "argument-hint"),
            ReadString(element, "response"),
            ReadString(element, "prompt"),
            ReadBool(element, "sendToRunner") ?? ReadBool(element, "send-to-runner") ?? false,
            Path.GetFullPath(filePath),
            scope);
    }

    private static IReadOnlyList<CodingAgentExtensionCommand> ResolveInvocationNames(
        IReadOnlyList<CommandDefinition> definitions)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            counts[definition.Name] = counts.GetValueOrDefault(definition.Name) + 1;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var takenInvocationNames = new HashSet<string>(StringComparer.Ordinal);
        var commands = new List<CodingAgentExtensionCommand>(definitions.Count);

        foreach (var definition in definitions)
        {
            var occurrence = seen.GetValueOrDefault(definition.Name) + 1;
            seen[definition.Name] = occurrence;

            var invocationName = counts[definition.Name] > 1
                ? $"{definition.Name}:{occurrence}"
                : definition.Name;
            if (takenInvocationNames.Contains(invocationName))
            {
                var suffix = occurrence;
                do
                {
                    suffix++;
                    invocationName = $"{definition.Name}:{suffix}";
                }
                while (takenInvocationNames.Contains(invocationName));
            }

            takenInvocationNames.Add(invocationName);
            commands.Add(new CodingAgentExtensionCommand(
                definition.Name,
                invocationName,
                definition.Description,
                string.IsNullOrWhiteSpace(definition.ArgumentHint) ? null : definition.ArgumentHint.Trim(),
                definition.Response,
                definition.Prompt,
                definition.SendToRunner,
                definition.FilePath,
                definition.Scope));
        }

        return commands.ToArray();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string NormalizeName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var name = rawName.Trim();
        if (name.StartsWith("/", StringComparison.Ordinal))
        {
            name = name[1..];
        }

        return name.Any(char.IsWhiteSpace) ? string.Empty : name;
    }

    private static IReadOnlyList<string> GetConfiguredExtensionPaths()
    {
        var configured = Environment.GetEnvironmentVariable(ExtensionPathsEnvironmentVariable);
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

    private static string GetDefaultUserExtensionsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tau", "extensions");

    private static bool IsIgnoredExtensionPath(string file)
    {
        var parts = Path.GetFullPath(file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(static part =>
            part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || part.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CommandDefinition(
        string Name,
        string Description,
        string? ArgumentHint,
        string? Response,
        string? Prompt,
        bool SendToRunner,
        string FilePath,
        string Scope);
}
