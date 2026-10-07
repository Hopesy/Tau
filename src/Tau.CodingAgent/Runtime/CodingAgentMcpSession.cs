// 作者：xxx
using Tau.AgentCore;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private CodingAgentMcpService? _mcpService;
    /// <summary>【CodingAgent】【MCP 管理访问】当前宿主管理的 MCP 服务，未启用时为空值。</summary>
    public CodingAgentMcpService? McpService => _mcpService;
    private long _mcpVersion = -1;
    private bool _syncingMcp;
    private readonly object _mcpSyncGate = new();
    private CodingAgentToolSearchTool? _toolSearch;
    private CodingAgentCodeModeTool? _codeMode;
    private readonly HashSet<string> _mcpToolNames = new(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【MCP 会话绑定】附加由宿主管理生命周期的服务，工具在会话读取和请求边界应用。</summary>
    /// <param name="service">当前会话的 MCP 服务。</param>
    public void AttachMcpService(CodingAgentMcpService service)
    {
        if (_mcpService is not null && !ReferenceEquals(_mcpService, service)) throw new InvalidOperationException("The session already has an MCP service");
        _mcpService = service;
        SynchronizeMcpTools();
    }

    /// <summary>【CodingAgent】【工具搜索注册】启用内置搜索能力，默认不声明，显式工具选择和 deferred MCP 配置可激活。</summary>
    public void EnableToolSearch()
    {
        _toolSearch ??= new(this, token => _mcpService?.WaitForServersAsync(token) ?? Task.CompletedTask);
        _mcpVersion = -1;
        SynchronizeMcpTools();
    }

    /// <summary>【CodingAgent】【脚本工具注册】注册内置脚本执行器，按显式选择或 MCP 配置启用。</summary>
    /// <param name="commands">提供当前会话分支状态的扩展宿主。</param><param name="options">可选模型访问和呈现覆盖。</param>
    public void EnableCodeMode(CodingAgentExtensionCommandStore? commands = null, CodingAgentCodeModeOptions? options = null)
    {
        _codeMode ??= new(this, commands, settings: () => _sessionSettings?.Load().AdditionalSettings?.GetValueOrDefault("codemode"),
            waitForScript: (source, token) => _mcpService?.WaitForScriptServersAsync(source, token) ?? Task.CompletedTask, options: options);
        _mcpVersion = -1;
        SynchronizeMcpTools();
    }

    /// <summary>【CodingAgent】【MCP 工具同步】在宿主访问边界合入完整目录，遵守允许、排除、SDK 优先级及用户关闭选择。</summary>
    private void SynchronizeMcpTools()
    { lock (_mcpSyncGate) SynchronizeMcpToolsCore(); }

    /// <summary>【CodingAgent】【MCP 同步事务】在同步锁内一次性替换目录，防止两个宿主读取并发更新。</summary>
    private void SynchronizeMcpToolsCore()
    {
        if (_syncingMcp) return;
        _syncingMcp = true;
        try
        {
            foreach (var builtin in new IAgentTool?[] { _toolSearch, _codeMode }.OfType<IAgentTool>())
            {
                if (!IsToolAllowed(builtin.Name) || _registeredTools.Any(tool => tool.Name == builtin.Name)) continue;
                _registeredTools = [.. _registeredTools, builtin];
                if (_allowedToolNames?.Contains(builtin.Name) == true || _configuredDefaultToolNames.Contains(builtin.Name) || _pendingToolNames.Contains(builtin.Name))
                    _config = _config with { Tools = ApplyToolLoadout([.. _config.Tools.Select(tool => tool.Name), builtin.Name], new()) };
                RefreshToolPromptMetadata();
            }
            if (_mcpService is not { } service || service.Version == _mcpVersion) return;
            var version = service.Version;
            var incoming = service.GetTools().Cast<IAgentTool>().Concat(service.GetResourceTools()).Where(tool => IsToolAllowed(tool.Name)).ToArray();
            var previous = _registeredTools.Where(tool => tool is CodingAgentMcpTool or CodingAgentMcpResourceTool && _mcpToolNames.Contains(tool.Name)).ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            var tools = _registeredTools.Where(tool => tool is not (CodingAgentMcpTool or CodingAgentMcpResourceTool) || !_mcpToolNames.Contains(tool.Name)).ToList();
            var active = _config.Tools.Select(tool => tool.Name).ToList();
            _mcpToolNames.Clear();
            foreach (var tool in incoming)
            {
                // 1. 【CodingAgent】【MCP 名称冲突】保留宿主和扩展已有工具，服务器不能覆盖同名本地实现
                if (tools.Any(existing => existing.Name == tool.Name)) continue;
                tools.Add(tool); _mcpToolNames.Add(tool.Name);
                // 2. 【CodingAgent】【MCP 策略切换】离开直接声明模式后移除旧激活状态，由脚本或搜索重新发现
                if (previous.TryGetValue(tool.Name, out var previousTool) && GetToolExposure(tool) != "direct" &&
                    (GetToolExposure(previousTool) != GetToolExposure(tool) ||
                     tool is CodingAgentMcpTool nextMcp && previousTool is CodingAgentMcpTool oldMcp &&
                     (nextMcp.McpExposure != oldMcp.McpExposure || nextMcp.DeclarationResetVersion != oldMcp.DeclarationResetVersion)))
                    active.RemoveAll(name => name == tool.Name);
                if (_allowedToolNames?.Contains(tool.Name) == true && GetToolExposure(tool) == "direct" ||
                    IsToolActiveOnRegistration(tool) && (!previous.TryGetValue(tool.Name, out var old) || !IsToolActiveOnRegistration(old))) active.Add(tool.Name);
            }
            _registeredTools = tools;
            if (service.HasConfiguredExposure("deferred") && _registeredTools.Any(tool => ReferenceEquals(tool, _toolSearch))) active.Add("tool_search");
            if (service.AutoEnableCodemode && service.HasConfiguredExposure("codemode") && _registeredTools.Any(tool => ReferenceEquals(tool, _codeMode))) active.Add("codemode");
            active.AddRange(_pendingToolNames);
            // 2. 【CodingAgent】【MCP 工具投影】同步元数据时不重入 Node，完整 prepare_loadout 在请求准备阶段执行
            _config = _config with { Tools = ApplyToolLoadout(active, new()) };
            _pendingToolNames.ExceptWith(_config.Tools.Select(tool => tool.Name));
            _mcpVersion = version;
            RefreshToolPromptMetadata();
        }
        finally { _syncingMcp = false; }
    }
}

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【MCP 会话注册表】向连接服务提供当前扩展运行时的服务器定义。</summary>
    public CodingAgentMcpServerRegistry McpServers => _javaScriptRuntime.McpServers;
}
