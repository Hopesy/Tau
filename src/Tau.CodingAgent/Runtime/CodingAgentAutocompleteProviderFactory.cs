using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public static class CodingAgentAutocompleteProviderFactory
{
    public static ITuiAutocompleteProvider Create(
        CodingAgentPromptTemplateStore? promptTemplateStore = null,
        CodingAgentSkillStore? skillStore = null,
        CodingAgentExtensionCommandStore? extensionCommandStore = null,
        string? basePath = null)
    {
        var commands = new List<TuiSlashCommand>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in CodingAgentCommandCatalog.SupportedCommands)
        {
            AddCommand(
                commands,
                names,
                command.Name.TrimStart('/'),
                command.Description,
                ExtractArgumentHint(command.Usage));
        }

        foreach (var prompt in LoadOrEmpty(promptTemplateStore))
        {
            AddCommand(commands, names, prompt.Name, prompt.Description, prompt.ArgumentHint);
        }

        foreach (var skill in LoadOrEmpty(skillStore))
        {
            AddCommand(commands, names, $"skill:{skill.Name}", skill.Description, null);
        }

        foreach (var extensionCommand in LoadOrEmpty(extensionCommandStore))
        {
            AddCommand(
                commands,
                names,
                extensionCommand.InvocationName,
                extensionCommand.Description,
                extensionCommand.ArgumentHint);
        }

        return new TuiCombinedAutocompleteProvider(commands, basePath);
    }

    private static void AddCommand(
        List<TuiSlashCommand> commands,
        HashSet<string> names,
        string name,
        string? description,
        string? argumentHint)
    {
        if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
        {
            return;
        }

        commands.Add(new TuiSlashCommand(name.Trim(), description, argumentHint));
    }

    private static string? ExtractArgumentHint(string usage)
    {
        if (string.IsNullOrWhiteSpace(usage))
        {
            return null;
        }

        var index = usage.IndexOf(' ', StringComparison.Ordinal);
        if (index < 0 || index + 1 >= usage.Length)
        {
            return null;
        }

        return usage[(index + 1)..].Trim();
    }

    private static IReadOnlyList<CodingAgentPromptTemplate> LoadOrEmpty(CodingAgentPromptTemplateStore? store)
    {
        if (store is null)
        {
            return [];
        }

        try
        {
            return store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<CodingAgentSkill> LoadOrEmpty(CodingAgentSkillStore? store)
    {
        if (store is null)
        {
            return [];
        }

        try
        {
            return store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<CodingAgentExtensionCommand> LoadOrEmpty(CodingAgentExtensionCommandStore? store)
    {
        if (store is null)
        {
            return [];
        }

        try
        {
            return store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
