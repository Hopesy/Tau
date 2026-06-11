using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// Renders the standalone <c>--help</c> and <c>--version</c> output for the CodingAgent CLI.
/// Mirrors upstream <c>packages/coding-agent/src/cli/args.ts</c> <c>printHelp(...)</c> and the
/// <c>main.ts</c> <c>VERSION</c> output, including extension-registered CLI flags in the help body.
/// </summary>
internal static partial class CodingAgentCliHelp
{
    private const string CommandNameEnvironmentVariable = "TAU_CODING_AGENT_COMMAND_NAME";
    private const string DefaultCommandName = "pi";

    public static string ResolveCommandName()
    {
        var configured = Environment.GetEnvironmentVariable(CommandNameEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured) ? DefaultCommandName : configured.Trim();
    }

    public static string ResolveVersion()
    {
        var version = typeof(CodingAgentCliHelp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var normalized = NormalizeVersion(version);
        if (normalized is not null)
        {
            return normalized;
        }

        var assemblyVersion = typeof(CodingAgentCliHelp).Assembly.GetName().Version;
        return assemblyVersion is null
            ? "0.1.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
    }

    public static string BuildHelpText(
        string commandName,
        IReadOnlyList<CodingAgentExtensionFlag> extensionFlags)
    {
        var builder = new StringBuilder();
        builder.Append(commandName);
        builder.AppendLine(" - AI coding assistant with read, bash, edit, write tools");
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine($"  {commandName} [options] [@files...] [messages...]");
        builder.AppendLine();
        builder.AppendLine("Commands:");
        builder.AppendLine($"  {commandName} install <source> [-l]     Install extension source and add to settings");
        builder.AppendLine($"  {commandName} remove <source> [-l]      Remove extension source from settings");
        builder.AppendLine($"  {commandName} uninstall <source> [-l]   Alias for remove");
        builder.AppendLine($"  {commandName} update [source]           Update installed extensions");
        builder.AppendLine($"  {commandName} list                      List installed extensions from settings");
        builder.AppendLine($"  {commandName} config                    Open TUI to enable/disable package resources");
        builder.AppendLine();
        builder.AppendLine("Options:");
        builder.AppendLine("  --provider <name>              Provider name");
        builder.AppendLine("  --model <pattern>              Model pattern or ID (supports \"provider/id\" and optional \":<thinking>\")");
        builder.AppendLine("  --api-key <key>                API key (defaults to env vars)");
        builder.AppendLine("  --system-prompt <text>         System prompt (default: coding assistant prompt)");
        builder.AppendLine("  --append-system-prompt <text>  Append text or file contents to the system prompt (repeatable)");
        builder.AppendLine("  --mode <mode>                  Output mode: text (default), json, or rpc");
        builder.AppendLine("  --print, -p                    Non-interactive mode: process prompt and exit");
        builder.AppendLine("  --continue, -c                 Continue previous session");
        builder.AppendLine("  --resume, -r                   Select a session to resume");
        builder.AppendLine("  --session <path>               Use specific session file");
        builder.AppendLine("  --fork <path>                  Fork specific session file or partial UUID into a new session");
        builder.AppendLine("  --session-dir <dir>            Directory for session storage and lookup");
        builder.AppendLine("  --no-session                   Don't save session (ephemeral)");
        builder.AppendLine("  --models <patterns>            Comma-separated model patterns for Ctrl+P cycling");
        builder.AppendLine("  --no-tools                     Disable all built-in tools");
        builder.AppendLine("  --tools <tools>                Comma-separated list of tools to enable (default: read,bash,edit,write)");
        builder.AppendLine("  --thinking <level>             Set thinking level: off, minimal, low, medium, high, xhigh");
        builder.AppendLine("  --extension, -e <path>         Load an extension file (repeatable)");
        builder.AppendLine("  --no-extensions, -ne           Disable extension discovery (explicit -e paths still work)");
        builder.AppendLine("  --skill <path>                 Load a skill file or directory (repeatable)");
        builder.AppendLine("  --no-skills, -ns               Disable skills discovery and loading");
        builder.AppendLine("  --prompt-template <path>       Load a prompt template file or directory (repeatable)");
        builder.AppendLine("  --no-prompt-templates, -np     Disable prompt template discovery and loading");
        builder.AppendLine("  --theme <path>                 Load a theme file or directory (repeatable)");
        builder.AppendLine("  --no-themes                    Disable theme discovery and loading");
        builder.AppendLine("  --no-context-files, -nc        Disable AGENTS.md and CLAUDE.md discovery and loading");
        builder.AppendLine("  --export <file>                Export session file to HTML and exit");
        builder.AppendLine("  --list-models [search]         List available models (with optional fuzzy search)");
        builder.AppendLine("  --verbose                      Force verbose startup (overrides quietStartup setting)");
        builder.AppendLine("  --offline                      Disable startup network operations (same as PI_OFFLINE=1)");
        builder.AppendLine("  --help, -h                     Show this help");
        builder.AppendLine("  --version, -v                  Show version number");

        if (extensionFlags.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Extension CLI Flags:");
            foreach (var flag in extensionFlags)
            {
                var value = flag.Type.Equals("string", StringComparison.Ordinal) ? " <value>" : string.Empty;
                var description = string.IsNullOrWhiteSpace(flag.Description)
                    ? $"Registered by {flag.FilePath}"
                    : flag.Description;
                builder.AppendLine($"  --{flag.Name}{value}".PadRight(33) + description);
            }
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionPattern().Match(value.Trim());
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
