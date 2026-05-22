namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentCommandInfo(string Name, string Usage, string Description);

public static class CodingAgentCommandCatalog
{
    private static readonly CodingAgentCommandInfo[] Commands =
    [
        new("/help", "/help", "Show supported local commands"),
        new("/reload", "/reload", "Reload settings, keybindings, extensions, skills, and prompts"),
        new("/hotkeys", "/hotkeys", "Show current editor keyboard shortcuts"),
        new("/settings", "/settings [current|path]", "Show current settings file and effective settings summary"),
        new("/theme", "/theme [current|list|set|clear] [name]", "Show, list, or persist the current color theme"),
        new("/name", "/name [display name | clear]", "Show, set, or clear the current session display name"),
        new("/copy", "/copy", "Copy the last assistant text message to the clipboard"),
        new("/files", "/files", "List file operations in the current session"),
        new("/export", "/export [path]", "Export the current session as HTML by default, JSON by path, or the current branch as JSONL"),
        new("/share", "/share", "Share the current HTML transcript as a secret GitHub gist"),
        new("/import", "/import <path>", "Import a flat JSON snapshot or resume a JSONL session"),
        new("/new", "/new", "Start a new session"),
        new("/session", "/session", "Show current session status"),
        new("/tree", "/tree [max entries] [default|no-tools|user-only|labeled-only|all] [--label-time] [--search query] [--interactive]", "Show the current JSONL session tree"),
        new("/label", "/label <entry-id> [label | clear]", "Show, set, or clear a JSONL session entry label"),
        new("/fork", "/fork <entry-id> [--summarize [instructions]]", "Fork the current JSONL session from an earlier entry, optionally summarizing the abandoned branch"),
        new("/clone", "/clone", "Duplicate the current JSONL session branch into a new session"),
        new("/resume", "/resume [latest | path.jsonl]", "Resume a JSONL session"),
        new("/quit", "/quit", "Exit the CLI"),
        new("/model", "/model [provider/model | model] or /model <provider> <model>", "Show or select the current model"),
        new("/provider", "/provider [provider]", "Show or select the current provider"),
        new("/models", "/models [provider]", "List models for a provider"),
        new("/providers", "/providers", "List registered providers"),
        new("/scoped-models", "/scoped-models [set|add|remove|clear|all] [provider/model ...]", "Show or configure the persisted model cycle scope"),
        new("/prompts", "/prompts", "List local prompt templates"),
        new("/skills", "/skills", "List local skills and their /skill:name commands"),
        new("/extensions", "/extensions", "List local extension commands, resources, and diagnostics"),
        new("/auth", "/auth [provider]", "Show provider auth status"),
        new("/login", "/login [provider]", "Show login guidance for a provider"),
        new("/logout", "/logout [provider]", "Remove provider credentials from auth.json"),
        new("/changelog", "/changelog [count|all]", "Show Tau release notes"),
        new("/retry", "/retry [current|default|off|<max attempts> [base delay ms]]", "Show or configure transient retry behavior"),
        new("/thinking", "/thinking [current|cycle|off|minimal|low|medium|high|xhigh]", "Show or set the thinking (reasoning) level"),
        new("/history", "/history [count|all]", "List recent input history entries"),
        new("/find", "/find <pattern>", "Search session messages for a substring"),
        new("/clear", "/clear", "Clear the terminal screen (keep session state)"),
        new("/compact", "/compact [instructions]", "Manually compact the current session")
    ];

    private static readonly IReadOnlyDictionary<string, CodingAgentCommandInfo> CommandsByName =
        Commands.ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<CodingAgentCommandInfo> SupportedCommands => Commands;

    public static string HelpLine =>
        $"commands: {string.Join(", ", Commands.Select(command => command.Name))}";

    public static bool IsSupported(string name) => CommandsByName.ContainsKey(name);

    public static string Usage(string name)
    {
        if (CommandsByName.TryGetValue(name, out var command))
        {
            return $"usage: {command.Usage}";
        }

        return $"usage: {name}";
    }
}
