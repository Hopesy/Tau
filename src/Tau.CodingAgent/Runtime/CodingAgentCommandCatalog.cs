namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentCommandInfo(string Name, string Usage, string Description);

public static class CodingAgentCommandCatalog
{
    private static readonly CodingAgentCommandInfo[] Commands =
    [
        new("/help", "/help", "Show supported local commands"),
        new("/name", "/name [display name | clear]", "Show, set, or clear the current session display name"),
        new("/copy", "/copy", "Copy the last assistant text message to the clipboard"),
        new("/export", "/export [path]", "Export the current session as HTML by default, JSON by path, or the current branch as JSONL"),
        new("/import", "/import <path>", "Import a flat JSON snapshot or resume a JSONL session"),
        new("/new", "/new", "Start a new session"),
        new("/session", "/session", "Show current session status"),
        new("/tree", "/tree [max entries] [default|no-tools|user-only|labeled-only|all] [--label-time] [--search query]", "Show the current JSONL session tree"),
        new("/label", "/label <entry-id> [label | clear]", "Show, set, or clear a JSONL session entry label"),
        new("/fork", "/fork <entry-id>", "Fork the current JSONL session from an earlier entry"),
        new("/resume", "/resume [latest | path.jsonl]", "Resume a JSONL session"),
        new("/quit", "/quit", "Exit the CLI"),
        new("/model", "/model [provider/model | model] or /model <provider> <model>", "Show or select the current model"),
        new("/provider", "/provider [provider]", "Show or select the current provider"),
        new("/models", "/models [provider]", "List models for a provider"),
        new("/providers", "/providers", "List registered providers"),
        new("/prompts", "/prompts", "List local prompt templates"),
        new("/skills", "/skills", "List local skills and their /skill:name commands"),
        new("/extensions", "/extensions", "List local extension commands"),
        new("/auth", "/auth [provider]", "Show provider auth status"),
        new("/login", "/login [provider]", "Show login guidance for a provider"),
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
