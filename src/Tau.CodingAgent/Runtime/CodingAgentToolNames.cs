// 作者：xxx
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【工具名称】对外使用 pi 原生名称，旧 Tau 配置和历史声明在选择时解析为对应内置名称。</summary>
internal static class CodingAgentToolNames
{
    /// <summary>【CodingAgent】【旧名兼容】把旧内置别名转换为原生名称，保留其他自定义工具名。</summary>
    /// <param name="name">工具名称。</param><returns>原生名称或原始自定义名称。</returns>
    internal static string NormalizeLegacy(string name) => name switch
    {
        "read_file" => "read", "write_file" => "write", "edit_file" => "edit", "shell" => "bash", "glob" => "find", _ => name
    };

    /// <summary>【CodingAgent】【目录解析】先匹配真实注册名称，避免覆盖恰好采用旧别名的自定义工具。</summary>
    /// <param name="name">请求名称。</param><param name="registered">当前完整工具目录。</param><returns>实际选择名称。</returns>
    internal static string ResolveRegistered(string name, IReadOnlyList<IAgentTool> registered) =>
        registered.Any(tool => tool.Name == name) ? name : NormalizeLegacy(name);
}
