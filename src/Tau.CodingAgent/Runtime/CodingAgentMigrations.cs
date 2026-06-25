using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentMigrationResult(
    IReadOnlyList<string> MigratedAuthProviders,
    IReadOnlyList<string> DeprecationWarnings);

public sealed record CodingAgentMigrationOptions(
    string? AgentDirectory = null,
    string? Cwd = null,
    string? AuthPath = null,
    string? KeybindingsPath = null,
    string? BinDirectory = null);

internal static class CodingAgentMigrations
{
    private const string ConfigDirectoryName = ".tau";
    private const string ExtensionsMigrationGuideUrl =
        "https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/CHANGELOG.md#extensions-migration";
    private const string ExtensionsDocumentationUrl =
        "https://github.com/earendil-works/pi-mono/blob/main/packages/coding-agent/docs/extensions.md";

    private static readonly IReadOnlyDictionary<string, string> KeybindingNameMigrations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cursorUp"] = "tui.editor.cursorUp",
            ["cursorDown"] = "tui.editor.cursorDown",
            ["cursorLeft"] = "tui.editor.cursorLeft",
            ["cursorRight"] = "tui.editor.cursorRight",
            ["cursorWordLeft"] = "tui.editor.cursorWordLeft",
            ["cursorWordRight"] = "tui.editor.cursorWordRight",
            ["cursorLineStart"] = "tui.editor.cursorLineStart",
            ["cursorLineEnd"] = "tui.editor.cursorLineEnd",
            ["jumpForward"] = "tui.editor.jumpForward",
            ["jumpBackward"] = "tui.editor.jumpBackward",
            ["pageUp"] = "tui.editor.pageUp",
            ["pageDown"] = "tui.editor.pageDown",
            ["deleteCharBackward"] = "tui.editor.deleteCharBackward",
            ["deleteCharForward"] = "tui.editor.deleteCharForward",
            ["deleteWordBackward"] = "tui.editor.deleteWordBackward",
            ["deleteWordForward"] = "tui.editor.deleteWordForward",
            ["deleteToLineStart"] = "tui.editor.deleteToLineStart",
            ["deleteToLineEnd"] = "tui.editor.deleteToLineEnd",
            ["yank"] = "tui.editor.yank",
            ["yankPop"] = "tui.editor.yankPop",
            ["undo"] = "tui.editor.undo",
            ["newLine"] = "tui.input.newLine",
            ["submit"] = "tui.input.submit",
            ["tab"] = "tui.input.tab",
            ["copy"] = "tui.input.copy",
            ["selectUp"] = "tui.select.up",
            ["selectDown"] = "tui.select.down",
            ["selectPageUp"] = "tui.select.pageUp",
            ["selectPageDown"] = "tui.select.pageDown",
            ["selectConfirm"] = "tui.select.confirm",
            ["selectCancel"] = "tui.select.cancel",
            ["interrupt"] = "app.interrupt",
            ["clear"] = "app.clear",
            ["exit"] = "app.exit",
            ["suspend"] = "app.suspend",
            ["cycleThinkingLevel"] = "app.thinking.cycle",
            ["cycleModelForward"] = "app.model.cycleForward",
            ["cycleModelBackward"] = "app.model.cycleBackward",
            ["selectModel"] = "app.model.select",
            ["expandTools"] = "app.tools.expand",
            ["toggleThinking"] = "app.thinking.toggle",
            ["toggleSessionNamedFilter"] = "app.session.toggleNamedFilter",
            ["externalEditor"] = "app.editor.external",
            ["followUp"] = "app.message.followUp",
            ["dequeue"] = "app.message.dequeue",
            ["pasteImage"] = "app.clipboard.pasteImage",
            ["newSession"] = "app.session.new",
            ["tree"] = "app.session.tree",
            ["fork"] = "app.session.fork",
            ["resume"] = "app.session.resume",
            ["treeFoldOrUp"] = "app.tree.foldOrUp",
            ["treeUnfoldOrDown"] = "app.tree.unfoldOrDown",
            ["treeEditLabel"] = "app.tree.editLabel",
            ["treeToggleLabelTimestamp"] = "app.tree.toggleLabelTimestamp",
            ["toggleSessionPath"] = "app.session.togglePath",
            ["toggleSessionSort"] = "app.session.toggleSort",
            ["renameSession"] = "app.session.rename",
            ["deleteSession"] = "app.session.delete",
            ["deleteSessionNoninvasive"] = "app.session.deleteNoninvasive"
        };

    public static CodingAgentMigrationResult Run(CodingAgentMigrationOptions? options = null)
    {
        options ??= new CodingAgentMigrationOptions();
        var cwd = ResolveDirectory(options.Cwd, Environment.CurrentDirectory);
        var agentDir = ResolveDirectory(options.AgentDirectory, GetDefaultAgentDirectory());
        var authPath = ResolvePath(options.AuthPath, Path.Combine(agentDir, "auth.json"));
        var binDir = ResolveDirectory(options.BinDirectory, Path.Combine(agentDir, "bin"));
        var keybindingsPath = ResolvePath(options.KeybindingsPath, ResolveDefaultKeybindingsPath(agentDir));

        var migratedAuthProviders = MigrateAuthToAuthJson(agentDir, authPath);
        MigrateSessionsFromAgentRoot(agentDir);
        MigrateToolsToBin(agentDir, binDir);
        MigrateKeybindingsConfigFile(keybindingsPath);
        MigrateKeybindingsConfigFile(Path.Combine(agentDir, "keybindings.json"));
        var deprecationWarnings = MigrateExtensionSystem(cwd, agentDir);

        return new CodingAgentMigrationResult(migratedAuthProviders, deprecationWarnings);
    }

    public static void PrintDeprecationWarnings(IReadOnlyList<string> warnings, TextWriter error)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        foreach (var warning in warnings)
        {
            error.WriteLine($"Warning: {warning}");
        }

        error.WriteLine("Move your extensions to the extensions/ directory.");
        error.WriteLine($"Migration guide: {ExtensionsMigrationGuideUrl}");
        error.WriteLine($"Documentation: {ExtensionsDocumentationUrl}");
    }

    private static IReadOnlyList<string> MigrateAuthToAuthJson(string agentDir, string authPath)
    {
        if (File.Exists(authPath))
        {
            return [];
        }

        var migrated = new JsonObject();
        var providers = new List<string>();
        var oauthPath = Path.Combine(agentDir, "oauth.json");
        var settingsPath = Path.Combine(agentDir, "settings.json");

        MigrateOAuthFile(oauthPath, migrated, providers);
        MigrateSettingsApiKeys(settingsPath, migrated, providers);

        if (migrated.Count == 0)
        {
            return providers;
        }

        try
        {
            var directory = Path.GetDirectoryName(authPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WriteJsonFile(authPath, migrated);
        }
        catch (Exception ex) when (IsMigrationIoException(ex))
        {
            return [];
        }

        return providers;
    }

    private static void MigrateOAuthFile(string oauthPath, JsonObject migrated, ICollection<string> providers)
    {
        if (!File.Exists(oauthPath))
        {
            return;
        }

        try
        {
            var parsed = JsonNode.Parse(File.ReadAllText(oauthPath)) as JsonObject;
            if (parsed is null)
            {
                return;
            }

            foreach (var (provider, value) in parsed)
            {
                if (string.IsNullOrWhiteSpace(provider) || value is not JsonObject credentials)
                {
                    continue;
                }

                var entry = new JsonObject
                {
                    ["type"] = "oauth"
                };
                foreach (var (key, child) in credentials)
                {
                    if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    entry[key] = child?.DeepClone();
                }

                migrated[provider] = entry;
                providers.Add(provider);
            }

            File.Move(oauthPath, oauthPath + ".migrated", overwrite: true);
        }
        catch (Exception ex) when (IsMigrationJsonOrIoException(ex))
        {
        }
    }

    private static void MigrateSettingsApiKeys(string settingsPath, JsonObject migrated, ICollection<string> providers)
    {
        if (!File.Exists(settingsPath))
        {
            return;
        }

        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            if (settings is null ||
                settings["apiKeys"] is not JsonObject apiKeys)
            {
                return;
            }

            foreach (var (provider, value) in apiKeys)
            {
                if (string.IsNullOrWhiteSpace(provider) ||
                    migrated.ContainsKey(provider) ||
                    value is not JsonValue jsonValue ||
                    !jsonValue.TryGetValue<string>(out var key) ||
                    string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                migrated[provider] = new JsonObject
                {
                    ["type"] = "api_key",
                    ["key"] = key
                };
                providers.Add(provider);
            }

            settings.Remove("apiKeys");
            WriteJsonFile(settingsPath, settings);
        }
        catch (Exception ex) when (IsMigrationJsonOrIoException(ex))
        {
        }
    }

    private static void MigrateSessionsFromAgentRoot(string agentDir)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(agentDir))
            {
                return;
            }

            files = Directory.GetFiles(agentDir, "*.jsonl", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (IsMigrationIoException(ex))
        {
            return;
        }

        foreach (var file in files)
        {
            TryMigrateRootSession(file);
        }
    }

    private static void TryMigrateRootSession(string file)
    {
        try
        {
            var firstLine = File.ReadLines(file).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return;
            }

            using var document = JsonDocument.Parse(firstLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !type.ValueEquals("session") ||
                !root.TryGetProperty("cwd", out var cwdElement) ||
                cwdElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var cwd = cwdElement.GetString();
            if (string.IsNullOrWhiteSpace(cwd))
            {
                return;
            }

            var targetDirectory = Path.Combine(Path.GetFullPath(cwd), ConfigDirectoryName, "coding-agent-sessions");
            var targetPath = Path.Combine(targetDirectory, Path.GetFileName(file));
            if (File.Exists(targetPath))
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);
            File.Move(file, targetPath);
        }
        catch (Exception ex) when (IsMigrationJsonOrIoException(ex) || ex is InvalidOperationException)
        {
        }
    }

    private static void MigrateToolsToBin(string agentDir, string binDir)
    {
        var toolsDir = Path.Combine(agentDir, "tools");
        if (!Directory.Exists(toolsDir))
        {
            return;
        }

        foreach (var binary in new[] { "fd", "rg", "fd.exe", "rg.exe" })
        {
            var oldPath = Path.Combine(toolsDir, binary);
            if (!File.Exists(oldPath))
            {
                continue;
            }

            var newPath = Path.Combine(binDir, binary);
            try
            {
                Directory.CreateDirectory(binDir);
                if (!File.Exists(newPath))
                {
                    File.Move(oldPath, newPath);
                }
                else
                {
                    File.Delete(oldPath);
                }
            }
            catch (Exception ex) when (IsMigrationIoException(ex))
            {
            }
        }
    }

    private static void MigrateKeybindingsConfigFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (parsed is null)
            {
                return;
            }

            var migrated = false;
            var next = new JsonObject();
            foreach (var (key, value) in parsed)
            {
                var nextKey = KeybindingNameMigrations.TryGetValue(key, out var migratedKey) ? migratedKey : key;
                if (!key.Equals(nextKey, StringComparison.Ordinal))
                {
                    migrated = true;
                }

                if (!key.Equals(nextKey, StringComparison.Ordinal) && parsed.ContainsKey(nextKey))
                {
                    continue;
                }

                next[nextKey] = value?.DeepClone();
            }

            if (migrated)
            {
                WriteJsonFile(path, next);
            }
        }
        catch (Exception ex) when (IsMigrationJsonOrIoException(ex))
        {
        }
    }

    private static IReadOnlyList<string> MigrateExtensionSystem(string cwd, string agentDir)
    {
        var projectDir = Path.Combine(cwd, ConfigDirectoryName);
        MigrateCommandsToPrompts(agentDir);
        MigrateCommandsToPrompts(projectDir);

        var warnings = new List<string>();
        warnings.AddRange(CheckDeprecatedExtensionDirs(agentDir, "Global"));
        warnings.AddRange(CheckDeprecatedExtensionDirs(projectDir, "Project"));
        return warnings;
    }

    private static void MigrateCommandsToPrompts(string baseDir)
    {
        var commandsDir = Path.Combine(baseDir, "commands");
        var promptsDir = Path.Combine(baseDir, "prompts");
        if (!Directory.Exists(commandsDir) || Directory.Exists(promptsDir))
        {
            return;
        }

        try
        {
            Directory.Move(commandsDir, promptsDir);
        }
        catch (Exception ex) when (IsMigrationIoException(ex))
        {
        }
    }

    private static IReadOnlyList<string> CheckDeprecatedExtensionDirs(string baseDir, string label)
    {
        var warnings = new List<string>();
        if (Directory.Exists(Path.Combine(baseDir, "hooks")))
        {
            warnings.Add($"{label} hooks/ directory found. Hooks have been renamed to extensions.");
        }

        var toolsDir = Path.Combine(baseDir, "tools");
        if (!Directory.Exists(toolsDir))
        {
            return warnings;
        }

        try
        {
            var customTools = Directory
                .EnumerateFileSystemEntries(toolsDir)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .Where(static name =>
                {
                    var lower = name.ToLowerInvariant();
                    return lower is not "fd" and not "rg" and not "fd.exe" and not "rg.exe" &&
                        !name.StartsWith(".", StringComparison.Ordinal);
                })
                .ToArray();
            if (customTools.Length > 0)
            {
                warnings.Add($"{label} tools/ directory contains custom tools. Custom tools have been merged into extensions.");
            }
        }
        catch (Exception ex) when (IsMigrationIoException(ex))
        {
        }

        return warnings;
    }

    private static void WriteJsonFile(string path, JsonNode node)
    {
        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        File.WriteAllText(path, json);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string GetDefaultAgentDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.CurrentDirectory, ConfigDirectoryName)
            : Path.Combine(home, ConfigDirectoryName);
    }

    private static string ResolveDefaultKeybindingsPath(string agentDir)
    {
        var explicitPath = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_KEYBINDINGS_FILE");
        return string.IsNullOrWhiteSpace(explicitPath)
            ? Path.Combine(agentDir, "coding-agent-keybindings.json")
            : explicitPath;
    }

    private static string ResolvePath(string? path, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? fallback : path);

    private static string ResolveDirectory(string? path, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? fallback : path);

    private static bool IsMigrationJsonOrIoException(Exception ex) =>
        ex is JsonException || IsMigrationIoException(ex) || ex is ArgumentException or NotSupportedException;

    private static bool IsMigrationIoException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;
}
