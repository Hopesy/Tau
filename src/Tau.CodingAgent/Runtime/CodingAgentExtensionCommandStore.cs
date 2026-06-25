using System.Text.Json;
using Tau.AgentCore;
using Tau.Tui.Abstractions;

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
    string Scope,
    string Runtime = "json");

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

public sealed record CodingAgentExtensionTool(
    string Name,
    string Label,
    string Description,
    JsonElement ParameterSchema,
    string FilePath,
    string Scope,
    string Runtime,
    bool HasPrepareArguments,
    string? ExecutionMode);

public sealed record CodingAgentExtensionFlag(
    string Name,
    string Description,
    string Type,
    JsonElement? DefaultValue,
    string FilePath,
    string Scope,
    string Runtime);

public sealed record CodingAgentExtensionShortcut(
    string Shortcut,
    string Description,
    bool HasHandler,
    string FilePath,
    string Scope,
    string Runtime);

public sealed record CodingAgentResolvedExtensionShortcut(
    KeyBinding KeyBinding,
    CodingAgentExtensionShortcut Shortcut);

public sealed record CodingAgentExtensionShortcutInvocation(
    bool Handled,
    bool IsError,
    bool SendToRunner,
    string Message,
    CodingAgentExtensionShortcut Shortcut)
{
    public static CodingAgentExtensionShortcutInvocation Status(CodingAgentExtensionShortcut shortcut, string message) =>
        new(true, false, false, message, shortcut);

    public static CodingAgentExtensionShortcutInvocation Runner(CodingAgentExtensionShortcut shortcut, string message) =>
        new(true, false, true, message, shortcut);

    public static CodingAgentExtensionShortcutInvocation Error(CodingAgentExtensionShortcut shortcut, string message) =>
        new(true, true, false, message, shortcut);
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

public sealed record CodingAgentExtensionModule(
    string FilePath,
    string Scope,
    string Runtime,
    string Status);

public sealed record CodingAgentExtensionEventHandler(
    string FilePath,
    string Scope,
    string Runtime,
    string EventType);

public sealed record CodingAgentExtensionDiagnostic(
    string Severity,
    string Message,
    string Path,
    string Scope);

public sealed record CodingAgentExtensionStatus(
    IReadOnlyList<CodingAgentExtensionCommand> Commands,
    IReadOnlyList<CodingAgentExtensionTool> Tools,
    IReadOnlyList<CodingAgentExtensionFlag> Flags,
    IReadOnlyList<CodingAgentExtensionShortcut> Shortcuts,
    CodingAgentExtensionResources Resources,
    IReadOnlyList<CodingAgentExtensionFileStatus> Files,
    IReadOnlyList<CodingAgentExtensionModule> Modules,
    IReadOnlyList<CodingAgentExtensionEventHandler> EventHandlers,
    IReadOnlyList<CodingAgentExtensionDiagnostic> Diagnostics)
{
    public IReadOnlyList<CodingAgentResourceDiagnostic> ResourceDiagnostics =>
        CodingAgentResourceDiagnostics.FromExtensions(Diagnostics);
}

public sealed class CodingAgentExtensionCommandStore
{
    public const string ExtensionPathsEnvironmentVariable = "TAU_CODING_AGENT_EXTENSION_PATHS";

    private readonly string _cwd;
    private readonly string _userExtensionsDirectory;
    private readonly IReadOnlyList<string> _explicitPaths;
    private readonly Func<IReadOnlyList<string>>? _additionalPathsProvider;
    private readonly bool _includeDefaults;
    private readonly CodingAgentJavaScriptExtensionRuntime _javaScriptRuntime;

    public CodingAgentExtensionCommandStore(
        string? cwd = null,
        string? userExtensionsDirectory = null,
        IReadOnlyList<string>? explicitPaths = null,
        Func<IReadOnlyList<string>>? additionalPathsProvider = null,
        bool includeDefaults = true,
        CodingAgentJavaScriptExtensionRuntime? javaScriptRuntime = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _userExtensionsDirectory = string.IsNullOrWhiteSpace(userExtensionsDirectory)
            ? GetDefaultUserExtensionsDirectory()
            : Path.GetFullPath(userExtensionsDirectory);
        _explicitPaths = explicitPaths ?? GetConfiguredExtensionPaths();
        _additionalPathsProvider = additionalPathsProvider;
        _includeDefaults = includeDefaults;
        _javaScriptRuntime = javaScriptRuntime ?? new CodingAgentJavaScriptExtensionRuntime(_cwd);
    }

    public IReadOnlyList<CodingAgentExtensionCommand> Load()
    {
        return LoadStatus().Commands;
    }

    public void SetExtensionUiBridge(CodingAgentRpcExtensionUiBridge? extensionUiBridge)
    {
        _javaScriptRuntime.SetExtensionUiBridge(extensionUiBridge);
    }

    public IReadOnlyList<CodingAgentExtensionTool> LoadToolDefinitions()
    {
        return LoadStatus().Tools;
    }

    public IReadOnlyList<IAgentTool> LoadTools()
    {
        return LoadToolDefinitions()
            .Select(tool => new CodingAgentExtensionToolAdapter(tool, _javaScriptRuntime))
            .ToArray();
    }

    public IReadOnlyList<IToolInterceptor> LoadToolInterceptors()
    {
        var modules = LoadStatus()
            .EventHandlers
            .Where(static handler =>
                handler.EventType.Equals("tool_call", StringComparison.Ordinal) ||
                handler.EventType.Equals("tool_result", StringComparison.Ordinal))
            .GroupBy(static handler => Path.GetFullPath(handler.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var handlers = group.ToArray();
                var first = handlers[0];
                return new CodingAgentExtensionToolEventModule(
                    first.FilePath,
                    first.Scope,
                    first.Runtime,
                    handlers.Any(static handler => handler.EventType.Equals("tool_call", StringComparison.Ordinal)),
                    handlers.Any(static handler => handler.EventType.Equals("tool_result", StringComparison.Ordinal)));
            })
            .ToArray();

        return modules.Length == 0
            ? []
            : [new CodingAgentExtensionToolEventInterceptor(modules, _javaScriptRuntime)];
    }

    public CodingAgentExtensionLifecycleEventSink? LoadLifecycleEventSink()
    {
        var modules = LoadStatus()
            .EventHandlers
            .Where(static handler => CodingAgentExtensionLifecycleEventSink.IsSupportedEventType(handler.EventType))
            .GroupBy(static handler => Path.GetFullPath(handler.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var handlers = group.ToArray();
                var first = handlers[0];
                return new CodingAgentExtensionLifecycleEventModule(
                    first.FilePath,
                    first.Scope,
                    first.Runtime,
                    handlers
                        .Select(static handler => handler.EventType)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            })
            .ToArray();

        return modules.Length == 0
            ? null
            : new CodingAgentExtensionLifecycleEventSink(modules, _javaScriptRuntime);
    }

    public CodingAgentExtensionResources LoadResources()
    {
        return LoadStatus().Resources;
    }

    public CodingAgentExtensionStatus LoadStatus(IKeyBindingMap? keyBindings = null)
    {
        var definitions = new List<CommandDefinition>();
        var tools = new List<CodingAgentExtensionTool>();
        var flags = new List<CodingAgentExtensionFlag>();
        var shortcuts = new List<CodingAgentExtensionShortcut>();
        var skillPaths = new List<string>();
        var promptPaths = new List<string>();
        var themePaths = new List<string>();
        var files = new List<CodingAgentExtensionFileStatus>();
        var modules = new List<CodingAgentExtensionModule>();
        var eventHandlers = new List<CodingAgentExtensionEventHandler>();
        var diagnostics = new List<CodingAgentExtensionDiagnostic>();
        if (_includeDefaults)
        {
            LoadSourceDirectory(_userExtensionsDirectory, "user", definitions, tools, flags, shortcuts, skillPaths, promptPaths, themePaths, files, modules, eventHandlers, diagnostics, _javaScriptRuntime);
            LoadSourceDirectory(Path.Combine(_cwd, ".tau", "extensions"), "project", definitions, tools, flags, shortcuts, skillPaths, promptPaths, themePaths, files, modules, eventHandlers, diagnostics, _javaScriptRuntime);
        }

        foreach (var path in GetExplicitPaths())
        {
            var resolved = ResolvePath(path, _cwd);
            if (Directory.Exists(resolved))
            {
                LoadSourceDirectory(resolved, "path", definitions, tools, flags, shortcuts, skillPaths, promptPaths, themePaths, files, modules, eventHandlers, diagnostics, _javaScriptRuntime, reportMissing: true);
            }
            else if (File.Exists(resolved) && Path.GetExtension(resolved).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                LoadSourceFile(resolved, "path", definitions, skillPaths, promptPaths, themePaths, files, diagnostics);
            }
            else if (File.Exists(resolved) && IsModuleFile(resolved))
            {
                AddModule(resolved, "path", modules, eventHandlers, definitions, tools, flags, shortcuts, diagnostics, _javaScriptRuntime);
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

        var resolvedShortcuts = keyBindings is null
            ? shortcuts.ToArray()
            : ResolveShortcuts(shortcuts, keyBindings, diagnostics)
                .Select(static resolved => resolved.Shortcut)
                .ToArray();

        return new CodingAgentExtensionStatus(
            ResolveInvocationNames(definitions),
            tools.ToArray(),
            flags.ToArray(),
            resolvedShortcuts,
            resources,
            files,
            modules
                .DistinctBy(static module => Path.GetFullPath(module.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            eventHandlers,
            diagnostics);
    }

    public IReadOnlyList<CodingAgentResolvedExtensionShortcut> LoadResolvedShortcuts(IKeyBindingMap? keyBindings = null)
    {
        var diagnostics = new List<CodingAgentExtensionDiagnostic>();
        return ResolveShortcuts(LoadStatus().Shortcuts, keyBindings, diagnostics);
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
        if (IsNodeModuleRuntime(command.Runtime))
        {
            var result = _javaScriptRuntime.Invoke(command.FilePath, command.Name, argsText);
            if (!result.Success)
            {
                invocation = CodingAgentExtensionCommandInvocation.Error(
                    command,
                    $"extension command '/{command.InvocationName}' failed: {result.Error ?? $"unknown {command.Runtime} extension error"}");
                return true;
            }

            if (result.RunnerMessages.Count > 0)
            {
                invocation = CodingAgentExtensionCommandInvocation.Runner(
                    command,
                    string.Join($"{Environment.NewLine}{Environment.NewLine}", result.RunnerMessages));
                return true;
            }

            invocation = CodingAgentExtensionCommandInvocation.Status(
                command,
                string.IsNullOrWhiteSpace(result.StatusMessage)
                    ? $"extension command '/{command.InvocationName}' completed"
                    : result.StatusMessage);
            return true;
        }

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

    public bool TryInvokeShortcut(
        CodingAgentExtensionShortcut shortcut,
        out CodingAgentExtensionShortcutInvocation? invocation)
    {
        invocation = null;
        if (!shortcut.HasHandler)
        {
            invocation = CodingAgentExtensionShortcutInvocation.Error(
                shortcut,
                $"extension shortcut '{shortcut.Shortcut}' has no handler");
            return true;
        }

        if (!IsNodeModuleRuntime(shortcut.Runtime))
        {
            invocation = CodingAgentExtensionShortcutInvocation.Error(
                shortcut,
                $"extension shortcut '{shortcut.Shortcut}' uses unsupported runtime '{shortcut.Runtime}'");
            return true;
        }

        var result = _javaScriptRuntime.InvokeShortcut(shortcut.FilePath, shortcut.Shortcut);
        if (!result.Success)
        {
            invocation = CodingAgentExtensionShortcutInvocation.Error(
                shortcut,
                $"extension shortcut '{shortcut.Shortcut}' failed: {result.Error ?? $"unknown {shortcut.Runtime} extension error"}");
            return true;
        }

        if (result.RunnerMessages.Count > 0)
        {
            invocation = CodingAgentExtensionShortcutInvocation.Runner(
                shortcut,
                string.Join($"{Environment.NewLine}{Environment.NewLine}", result.RunnerMessages));
            return true;
        }

        invocation = CodingAgentExtensionShortcutInvocation.Status(
            shortcut,
            string.IsNullOrWhiteSpace(result.StatusMessage)
                ? $"extension shortcut '{shortcut.Shortcut}' completed"
                : result.StatusMessage);
        return true;
    }

    /// <summary>
    /// Validates CLI-supplied extension flag tokens against currently registered extension flags and
    /// seeds the resolved values into the JavaScript/TypeScript runtime so <c>pi.getFlag(...)</c> returns
    /// them. Mirrors upstream <c>applyExtensionFlagValues</c>: boolean flags resolve to <c>true</c> when
    /// present, string flags require a value, unknown flags and value-less string flags produce error
    /// diagnostics. A <c>null</c> dictionary value means the flag was supplied without an <c>=value</c>.
    /// </summary>
    public IReadOnlyList<CodingAgentExtensionDiagnostic> ApplyExtensionFlagValues(
        IReadOnlyDictionary<string, string?> cliFlags)
    {
        ArgumentNullException.ThrowIfNull(cliFlags);
        if (cliFlags.Count == 0)
        {
            return [];
        }

        var registered = LoadStatus().Flags;
        var registeredByName = new Dictionary<string, CodingAgentExtensionFlag>(StringComparer.Ordinal);
        foreach (var flag in registered)
        {
            registeredByName[flag.Name] = flag;
        }

        var diagnostics = new List<CodingAgentExtensionDiagnostic>();
        var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        var unknownFlags = new List<string>();
        foreach (var (rawName, value) in cliFlags)
        {
            var name = NormalizeName(rawName);
            if (!registeredByName.TryGetValue(name, out var flag))
            {
                unknownFlags.Add(name);
                continue;
            }

            if (flag.Type.Equals("boolean", StringComparison.Ordinal))
            {
                resolved[name] = true;
                continue;
            }

            if (value is not null)
            {
                resolved[name] = value;
                continue;
            }

            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "error",
                $"Extension flag \"--{name}\" requires a value",
                flag.FilePath,
                flag.Scope));
        }

        if (unknownFlags.Count > 0)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "error",
                $"Unknown option{(unknownFlags.Count == 1 ? string.Empty : "s")}: {string.Join(", ", unknownFlags.Select(static name => $"--{name}"))}",
                string.Empty,
                "cli"));
        }

        _javaScriptRuntime.SetFlagValues(resolved);
        return diagnostics;
    }

    private static bool IsNodeModuleRuntime(string runtime) =>
        runtime.Equals("javascript", StringComparison.Ordinal) ||
        runtime.Equals("typescript", StringComparison.Ordinal);

    private static IReadOnlyList<CodingAgentResolvedExtensionShortcut> ResolveShortcuts(
        IReadOnlyList<CodingAgentExtensionShortcut> shortcuts,
        IKeyBindingMap? keyBindings,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics)
    {
        var resolved = new Dictionary<KeyBinding, CodingAgentExtensionShortcut>();
        foreach (var shortcut in shortcuts)
        {
            if (!TryParseShortcutKey(shortcut.Shortcut, out var keyBinding))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"Extension shortcut '{shortcut.Shortcut}' from {shortcut.FilePath} uses an unsupported key format. Skipping.",
                    shortcut.FilePath,
                    shortcut.Scope));
                continue;
            }

            if (keyBindings is not null &&
                keyBindings.Bindings.TryGetValue(keyBinding, out var builtInAction) &&
                builtInAction != EditorAction.None)
            {
                if (IsReservedBuiltInShortcutAction(builtInAction))
                {
                    diagnostics.Add(new CodingAgentExtensionDiagnostic(
                        "warning",
                        $"Extension shortcut '{shortcut.Shortcut}' from {shortcut.FilePath} conflicts with built-in shortcut. Skipping.",
                        shortcut.FilePath,
                        shortcut.Scope));
                    continue;
                }

                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"Extension shortcut conflict: '{shortcut.Shortcut}' is built-in shortcut for {builtInAction} and {shortcut.FilePath}. Using {shortcut.FilePath}.",
                    shortcut.FilePath,
                    shortcut.Scope));
            }

            if (resolved.TryGetValue(keyBinding, out var existing))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"Extension shortcut conflict: '{shortcut.Shortcut}' registered by both {existing.FilePath} and {shortcut.FilePath}. Using {shortcut.FilePath}.",
                    shortcut.FilePath,
                    shortcut.Scope));
            }

            resolved[keyBinding] = shortcut;
        }

        return resolved
            .Select(static pair => new CodingAgentResolvedExtensionShortcut(pair.Key, pair.Value))
            .ToArray();
    }

    private static bool IsReservedBuiltInShortcutAction(EditorAction action) =>
        action is EditorAction.Cancel
            or EditorAction.Submit
            or EditorAction.CycleModelForward
            or EditorAction.CycleModelBackward
            or EditorAction.SelectModel
            or EditorAction.KillToLineEnd;

    public static bool TryParseShortcutKey(string shortcut, out KeyBinding keyBinding)
    {
        keyBinding = default;
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        var parts = shortcut
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.ToLowerInvariant())
            .ToArray();
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = ConsoleModifiers.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= parts[index] switch
            {
                "ctrl" or "control" => ConsoleModifiers.Control,
                "shift" => ConsoleModifiers.Shift,
                "alt" or "option" => ConsoleModifiers.Alt,
                _ => (ConsoleModifiers)(-1)
            };

            if ((int)modifiers < 0)
            {
                return false;
            }
        }

        if (!TryParseConsoleKey(parts[^1], out var key))
        {
            return false;
        }

        keyBinding = new KeyBinding(key, modifiers);
        return true;
    }

    private static bool TryParseConsoleKey(string keyName, out ConsoleKey key)
    {
        key = default;
        if (keyName.Length == 1)
        {
            var ch = keyName[0];
            if (ch is >= 'a' and <= 'z')
            {
                key = Enum.Parse<ConsoleKey>(ch.ToString().ToUpperInvariant());
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                key = Enum.Parse<ConsoleKey>($"D{ch}");
                return true;
            }
        }

        keyName = keyName switch
        {
            "enter" or "return" => nameof(ConsoleKey.Enter),
            "esc" or "escape" => nameof(ConsoleKey.Escape),
            "tab" => nameof(ConsoleKey.Tab),
            "space" or "spacebar" => nameof(ConsoleKey.Spacebar),
            "backspace" => nameof(ConsoleKey.Backspace),
            "delete" or "del" => nameof(ConsoleKey.Delete),
            "left" => nameof(ConsoleKey.LeftArrow),
            "right" => nameof(ConsoleKey.RightArrow),
            "up" => nameof(ConsoleKey.UpArrow),
            "down" => nameof(ConsoleKey.DownArrow),
            _ => keyName
        };

        return Enum.TryParse(keyName, ignoreCase: true, out key);
    }

    private static void LoadSourceDirectory(
        string directory,
        string scope,
        ICollection<CommandDefinition> definitions,
        ICollection<CodingAgentExtensionTool> tools,
        ICollection<CodingAgentExtensionFlag> flags,
        ICollection<CodingAgentExtensionShortcut> shortcuts,
        ICollection<string> skillPaths,
        ICollection<string> promptPaths,
        ICollection<string> themePaths,
        ICollection<CodingAgentExtensionFileStatus> fileStatuses,
        ICollection<CodingAgentExtensionModule> modules,
        ICollection<CodingAgentExtensionEventHandler> eventHandlers,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics,
        CodingAgentJavaScriptExtensionRuntime javaScriptRuntime,
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

        foreach (var module in DiscoverModuleFiles(directory))
        {
            AddModule(module, scope, modules, eventHandlers, definitions, tools, flags, shortcuts, diagnostics, javaScriptRuntime);
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
            scope,
            "json");
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
                definition.Scope,
                definition.Runtime));
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

    private static IReadOnlyList<string> DiscoverModuleFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var rootEntries = ResolveModuleEntries(directory);
        if (rootEntries is not null)
        {
            return rootEntries;
        }

        var modules = new List<string>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith(".", StringComparison.Ordinal) ||
                name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(entry))
            {
                if (IsModuleFile(entry))
                {
                    modules.Add(Path.GetFullPath(entry));
                }

                continue;
            }

            if (!Directory.Exists(entry))
            {
                continue;
            }

            var resolved = ResolveModuleEntries(entry);
            if (resolved is not null)
            {
                modules.AddRange(resolved);
            }
        }

        return modules
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string>? ResolveModuleEntries(string directory)
    {
        var manifestEntries = ResolveModuleManifestEntries(directory);
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

        return null;
    }

    private static IReadOnlyList<string> ResolveModuleManifestEntries(string directory)
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
                ? ReadPathArray(pi, "extensions")
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

        var modules = new List<string>();
        foreach (var entry in configured)
        {
            var resolved = ResolvePath(entry, directory);
            if (File.Exists(resolved))
            {
                if (IsModuleFile(resolved))
                {
                    modules.Add(Path.GetFullPath(resolved));
                }

                continue;
            }

            if (Directory.Exists(resolved) &&
                !Path.GetFullPath(resolved).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            {
                modules.AddRange(DiscoverModuleFiles(resolved));
            }
        }

        return modules
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string>? ReadPathArray(JsonElement element, string propertyName)
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

    private static bool IsModuleFile(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".js", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddModule(
        string filePath,
        string scope,
        ICollection<CodingAgentExtensionModule> modules,
        ICollection<CodingAgentExtensionEventHandler> eventHandlers,
        ICollection<CommandDefinition> definitions,
        ICollection<CodingAgentExtensionTool> tools,
        ICollection<CodingAgentExtensionFlag> flags,
        ICollection<CodingAgentExtensionShortcut> shortcuts,
        ICollection<CodingAgentExtensionDiagnostic> diagnostics,
        CodingAgentJavaScriptExtensionRuntime javaScriptRuntime)
    {
        var fullPath = Path.GetFullPath(filePath);
        var runtime = Path.GetExtension(filePath).Equals(".ts", StringComparison.OrdinalIgnoreCase)
            ? "typescript"
            : "javascript";

        var result = javaScriptRuntime.Load(fullPath);
        if (!result.Success)
        {
            diagnostics.Add(new CodingAgentExtensionDiagnostic(
                "error",
                $"failed to load {runtime} extension: {result.Error ?? $"unknown {runtime} extension error"}",
                fullPath,
                scope));
            modules.Add(new CodingAgentExtensionModule(
                fullPath,
                scope,
                runtime,
                "load failed"));
            return;
        }

        foreach (var command in result.Commands)
        {
            var name = NormalizeName(command.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a command with an invalid name",
                    fullPath,
                    scope));
                continue;
            }

            if (!command.HasHandler)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension command '{name}' has no handler",
                    fullPath,
                    scope));
                continue;
            }

            definitions.Add(new CommandDefinition(
                name,
                command.Description.Trim(),
                command.ArgumentHint,
                null,
                null,
                false,
                fullPath,
                scope,
                runtime));
        }

        foreach (var tool in result.Tools)
        {
            var name = NormalizeName(tool.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a tool with an invalid name",
                    fullPath,
                    scope));
                continue;
            }

            if (!tool.HasHandler)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension tool '{name}' has no execute handler",
                    fullPath,
                    scope));
                continue;
            }

            if (tools.Any(existing => existing.Name.Equals(name, StringComparison.Ordinal)))
            {
                continue;
            }

            tools.Add(new CodingAgentExtensionTool(
                name,
                string.IsNullOrWhiteSpace(tool.Label) ? name : tool.Label.Trim(),
                tool.Description.Trim(),
                tool.ParameterSchema.Clone(),
                fullPath,
                scope,
                runtime,
                tool.HasPrepareArguments,
                tool.ExecutionMode));
        }

        foreach (var eventType in result.EventHandlerTypes)
        {
            eventHandlers.Add(new CodingAgentExtensionEventHandler(
                fullPath,
                scope,
                runtime,
                eventType));
        }

        foreach (var flag in result.Flags)
        {
            var name = NormalizeName(flag.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a flag with an invalid name",
                    fullPath,
                    scope));
                continue;
            }

            if (!flag.Type.Equals("boolean", StringComparison.Ordinal) &&
                !flag.Type.Equals("string", StringComparison.Ordinal))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension flag '{name}' has an unsupported type",
                    fullPath,
                    scope));
                continue;
            }

            if (flags.Any(existing => existing.Name.Equals(name, StringComparison.Ordinal)))
            {
                continue;
            }

            flags.Add(new CodingAgentExtensionFlag(
                name,
                flag.Description.Trim(),
                flag.Type,
                flag.DefaultValue?.Clone(),
                fullPath,
                scope,
                runtime));
        }

        foreach (var shortcut in result.Shortcuts)
        {
            var key = shortcut.Shortcut.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension registered a shortcut with an invalid key",
                    fullPath,
                    scope));
                continue;
            }

            if (!shortcut.HasHandler)
            {
                diagnostics.Add(new CodingAgentExtensionDiagnostic(
                    "warning",
                    $"{runtime} extension shortcut '{key}' has no handler",
                    fullPath,
                    scope));
                continue;
            }

            if (shortcuts.Any(existing => existing.Shortcut.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            shortcuts.Add(new CodingAgentExtensionShortcut(
                key,
                shortcut.Description.Trim(),
                true,
                fullPath,
                scope,
                runtime));
        }

        modules.Add(new CodingAgentExtensionModule(
            fullPath,
            scope,
            runtime,
            FormatNodeModuleStatus(result)));
    }

    private static string FormatNodeModuleStatus(CodingAgentJavaScriptExtensionLoadResult result)
    {
        var status = $"loaded; commands {result.Commands.Count(static command => command.HasHandler)}; tools {result.Tools.Count(static tool => tool.HasHandler)}";
        if (result.Flags.Count > 0)
        {
            status = $"{status}; flags {result.Flags.Count}";
        }

        var shortcutCount = result.Shortcuts.Count(static shortcut => shortcut.HasHandler);
        if (shortcutCount > 0)
        {
            status = $"{status}; shortcuts {shortcutCount}";
        }

        if (result.EventHandlerTypes.Count > 0)
        {
            status = $"{status}; events {result.EventHandlerTypes.Count}";
        }

        status = $"{status}; limited runtime";
        var unsupported = new List<string>();
        if (result.Unsupported.Tools > 0)
        {
            unsupported.Add($"tools {result.Unsupported.Tools}");
        }

        if (result.Unsupported.Flags > 0)
        {
            unsupported.Add($"flags {result.Unsupported.Flags}");
        }

        if (result.Unsupported.Shortcuts > 0)
        {
            unsupported.Add($"shortcuts {result.Unsupported.Shortcuts}");
        }

        if (result.Unsupported.Handlers > 0)
        {
            unsupported.Add($"events {result.Unsupported.Handlers}");
        }

        if (result.Unsupported.MessageRenderers > 0)
        {
            unsupported.Add($"message renderers {result.Unsupported.MessageRenderers}");
        }

        if (result.Unsupported.Providers > 0)
        {
            unsupported.Add($"providers {result.Unsupported.Providers}");
        }

        return unsupported.Count == 0
            ? status
            : $"{status}; unsupported {string.Join(", ", unsupported)}";
    }

    private sealed record CommandDefinition(
        string Name,
        string Description,
        string? ArgumentHint,
        string? Response,
        string? Prompt,
        bool SendToRunner,
        string FilePath,
        string Scope,
        string Runtime);
}
