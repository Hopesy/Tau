// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【命令目录】扩展可查询的命令、来源类型和完整来源信息。</summary>
public sealed record CodingAgentSlashCommandInfo(string Name, string Description, string Source, CodingAgentSourceInfo SourceInfo);

public sealed partial class CodingAgentExtensionCommandStore
{
    internal IReadOnlyList<CodingAgentExtensionCommand> CurrentCommandCatalog { get; private set; } = [];
}

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【命令目录】按扩展、提示和技能顺序返回会话命令，避免在 Node 回调期间重新加载模块。</summary>
    /// <returns>完整命令快照，不包括内置斜杠命令。</returns>
    public IReadOnlyList<CodingAgentSlashCommandInfo> GetCommands() =>
    [
        .. (_inputCommands?.CurrentCommandCatalog ?? []).Select(command => new CodingAgentSlashCommandInfo(command.InvocationName, command.Description, "extension", command.SourceInfo)),
        .. (_inputTemplates?.Load() ?? []).Select(template => new CodingAgentSlashCommandInfo(template.Name, template.Description, "prompt", template.SourceInfo)),
        .. (_inputSkills?.Load() ?? []).Select(skill => new CodingAgentSlashCommandInfo("skill:" + skill.Name, skill.Description, "skill", skill.SourceInfo))
    ];
}

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【命令快照】把完整命令目录写入当前扩展上下文，保留所有来源字段。</summary>
    /// <param name="writer">正在写入运行状态的 JSON 写入器。</param>
    private void WriteCommandsSnapshot(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("commands"); writer.WriteStartArray();
        var commands = (_runner as RuntimeCodingAgentRunner)?.GetCommands()
            ?? (_extensions?.SessionCommands?.CurrentCommandCatalog ?? []).Select(command => new CodingAgentSlashCommandInfo(command.InvocationName, command.Description, "extension", command.SourceInfo)).ToArray();
        foreach (var command in commands)
        {
            writer.WriteStartObject();
            writer.WriteString("name", command.Name); writer.WriteString("description", command.Description); writer.WriteString("source", command.Source);
            writer.WritePropertyName("sourceInfo"); command.SourceInfo.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
