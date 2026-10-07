// 作者：xxx
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【工具选择】区分完整可用目录与初始活动集合，统一 SDK 和命令行选择规则。</summary>
internal sealed record CodingAgentToolSelection(IReadOnlyList<IAgentTool> Registered, IReadOnlyList<string> Active,
    IReadOnlyList<string>? Allowed, IReadOnlyList<string> Excluded, bool UsesDefaults, IReadOnlyList<string> ConfiguredDefaults)
{
    internal static readonly string[] DefaultNames = ["read", "bash", "edit", "write"];

    /// <summary>【CodingAgent】【工具名称】兼容原生 CLI 名称并保留扩展自定义名称。</summary>
    /// <param name="names">调用方提供的名称，空值表示没有限制。</param>
    /// <returns>去重且保持顺序的工具名，保留未设置与空集合的区别。</returns>
    internal static IReadOnlyList<string>? NormalizeNames(IEnumerable<string>? names) => names?.Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => CodingAgentCliArguments.CliToolNameToTauToolName.GetValueOrDefault(name.Trim(), name.Trim()))
        .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>【CodingAgent】【目录名称】在真实目录中解析旧别名，保留与旧别名同名的自定义工具。</summary>
    /// <param name="names">输入名称。</param><param name="registered">可选工具目录。</param><returns>独立名称集合。</returns>
    internal static IReadOnlyList<string>? ResolveNames(IEnumerable<string>? names, IReadOnlyList<IAgentTool>? registered = null) =>
        NormalizeNames(names)?.Select(name => registered is null ? CodingAgentToolNames.NormalizeLegacy(name) : CodingAgentToolNames.ResolveRegistered(name, registered)).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>【CodingAgent】【默认工具】先由普通名称决定基础集合，再按列表顺序应用增减条目。</summary>
    /// <param name="entries">已合并的默认工具设置，空值继承内置默认集合。</param><param name="registered">可选工具目录。</param><returns>规范化的有效名称。</returns>
    internal static IReadOnlyList<string> ResolveDefaults(IReadOnlyList<string>? entries, IReadOnlyList<IAgentTool>? registered = null)
    {
        if (entries is null) return DefaultNames;
        var plain = entries.Where(entry => !entry.StartsWithAnyToolModifier()).ToArray();
        var names = (plain.Length > 0 || entries.Count == 0 ? ResolveNames(plain, registered)! : DefaultNames).ToList();
        foreach (var entry in entries.Where(entry => entry.StartsWithAnyToolModifier()))
        {
            var name = ResolveNames([entry[1..]], registered)!.FirstOrDefault();
            if (name is null) continue;
            if (entry[0] == '+' && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            else if (entry[0] == '-') names.Remove(name);
        }
        return names;
    }

    /// <summary>【CodingAgent】【工具选择】先应用允许和排除集合，再合并默认内置和默认启用的扩展工具。</summary>
    /// <param name="tools">包含内置及自定义工具的完整目录。</param>
    /// <param name="allowed">显式允许名单。</param><param name="excluded">排除名单。</param>
    /// <param name="defaults">设置中的默认工具，空集合有效。</param><param name="suppression">默认工具关闭模式。</param>
    /// <returns>注册目录、初始活动名称和可持续使用的限制。</returns>
    internal static CodingAgentToolSelection Create(IReadOnlyList<IAgentTool> tools, IReadOnlyList<string>? allowed,
        IReadOnlyList<string>? excluded, IReadOnlyList<string>? defaults, CodingAgentSdkNoToolsMode suppression)
    {
        var allow = ResolveNames(allowed, tools) ?? (suppression == CodingAgentSdkNoToolsMode.All ? [] : null);
        var deny = ResolveNames(excluded, tools) ?? [];
        var registered = tools.Where(tool => (allow is null || allow.Contains(tool.Name)) && !deny.Contains(tool.Name)).ToArray();
        var initial = ResolveNames(allowed, tools) ?? (suppression != CodingAgentSdkNoToolsMode.None ? [] : ResolveDefaults(defaults, tools));
        var active = initial.Where(name => !deny.Contains(name)).ToList();
        if (allow is null)
            active.AddRange(registered.Where(tool => CodingAgentSourceInfo.ForTool(tool).Source != "builtin" && RuntimeCodingAgentRunner.IsToolActiveOnRegistration(tool)).Select(tool => tool.Name));
        return new(registered, active.Distinct(StringComparer.Ordinal).ToArray(), allow, deny,
            allowed is null && suppression == CodingAgentSdkNoToolsMode.None, ResolveDefaults(defaults, tools));
    }
}

public sealed partial class RuntimeCodingAgentRunner
{
    private IReadOnlySet<string>? _allowedToolNames;
    private bool _usesDefaultToolSettings;
    private IReadOnlyList<string> _configuredDefaultToolNames = CodingAgentToolSelection.DefaultNames;

    /// <summary>【CodingAgent】【持续工具限制】保存创建时的允许和排除名单，约束后续动态注册及恢复。</summary>
    /// <param name="selection">本次会话的工具选择。</param>
    internal void ConfigureToolSelection(CodingAgentToolSelection selection)
    {
        _allowedToolNames = selection.Allowed?.ToHashSet(StringComparer.Ordinal);
        _usesDefaultToolSettings = selection.UsesDefaults;
        _configuredDefaultToolNames = selection.ConfiguredDefaults;
        _excludedExtensionTools = selection.Excluded.ToHashSet(StringComparer.Ordinal);
        _pendingToolNames.RemoveWhere(name => !IsToolAllowed(name));
    }

    /// <summary>【CodingAgent】【工具限制】判断名称是否允许进入会话目录。</summary>
    /// <param name="name">工具名称。</param><returns>是否允许。</returns>
    private bool IsToolAllowed(string name) => (_allowedToolNames is null || _allowedToolNames.Contains(name)) && !_excludedExtensionTools.Contains(name);
}
