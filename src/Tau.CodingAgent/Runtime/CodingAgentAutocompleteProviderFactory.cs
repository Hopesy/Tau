using Tau.Tui.Runtime;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Runtime;

public static partial class CodingAgentAutocompleteProviderFactory
{
    /// <summary>【CodingAgent】【命令补全】合并内置命令、项目资源与扩展命令，并动态读取 MCP 服务器。</summary>
    /// <param name="promptTemplateStore">提示模板来源。</param><param name="skillStore">技能来源。</param><param name="extensionCommandStore">扩展命令来源。</param>
    /// <param name="basePath">路径补全基准目录。</param><param name="mcpService">可选 MCP 会话服务。</param><returns>组合补全提供方。</returns>
    public static ITuiAutocompleteProvider Create(
        CodingAgentPromptTemplateStore? promptTemplateStore = null,
        CodingAgentSkillStore? skillStore = null,
        CodingAgentExtensionCommandStore? extensionCommandStore = null,
        string? basePath = null,
        CodingAgentMcpService? mcpService = null)
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
                ExtractArgumentHint(command.Usage),
                command.Name == "/mcp" ? (prefix, token) => McpCompletions(mcpService, prefix, token) : null);
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

    /// <summary>【CodingAgent】【命令目录】按名称去重，保留优先来源和可选参数补全。</summary>
    /// <param name="commands">输出列表。</param><param name="names">已使用名称。</param><param name="name">命令名称。</param><param name="description">说明。</param>
    /// <param name="argumentHint">参数提示。</param><param name="complete">动态参数补全。</param>
    private static void AddCommand(
        List<TuiSlashCommand> commands,
        HashSet<string> names,
        string name,
        string? description,
        string? argumentHint,
        Func<string, CancellationToken, ValueTask<IReadOnlyList<TuiAutocompleteItem>?>>? complete = null)
    {
        if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
        {
            return;
        }

        commands.Add(new TuiSlashCommand(name.Trim(), description, argumentHint, complete));
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
