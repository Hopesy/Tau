// 作者：xxx
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【MCP 注册】保存单个扩展运行时的服务器定义及归属，不建立连接。</summary>
public sealed class CodingAgentMcpServerRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CodingAgentMcpServerEntry> _servers = new(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【MCP 变更】注册替换或实际注销后通知订阅者，参数是独立配置快照。</summary>
    public event Action<IReadOnlyList<CodingAgentMcpServerEntry>>? Changed;

    /// <summary>【CodingAgent】【MCP 注册】验证归属和命名空间后原子新增或替换，同一扩展可以更新自己的定义。</summary>
    /// <param name="name">服务器名称。</param><param name="config">配置定义。</param><param name="extensionPath">所属扩展的稳定路径。</param>
    public void Register(string name, JsonObject config, string extensionPath)
    {
        var validated = CodingAgentMcpServers.Validate(name, config);
        IReadOnlyList<CodingAgentMcpServerEntry> snapshot;
        lock (_gate)
        {
            if (_servers.TryGetValue(name, out var previous) && previous.Source != extensionPath)
                throw new InvalidOperationException($"MCP server \"{name}\" is already registered by extension \"{previous.Source}\"");
            var clash = _servers.Keys.FirstOrDefault(other => other != name && CodingAgentMcpServers.Namespace(other) == CodingAgentMcpServers.Namespace(name));
            if (clash is not null) throw new InvalidOperationException($"MCP server \"{name}\" conflicts with registered server \"{clash}\"");
            _servers[name] = new(name, validated, extensionPath, "extension");
            snapshot = List();
        }
        Changed?.Invoke(snapshot);
    }

    /// <summary>【CodingAgent】【MCP 注销】仅移除当前扩展拥有的定义，其他扩展和不存在的条目保持不变。</summary>
    /// <param name="name">服务器名称。</param><param name="extensionPath">调用扩展。</param><returns>是否移除条目。</returns>
    public bool Unregister(string name, string extensionPath)
    {
        IReadOnlyList<CodingAgentMcpServerEntry> snapshot;
        lock (_gate)
        {
            if (!_servers.TryGetValue(name, out var previous) || previous.Source != extensionPath) return false;
            _servers.Remove(name);
            snapshot = List();
        }
        Changed?.Invoke(snapshot);
        return true;
    }

    /// <summary>【CodingAgent】【MCP 查询】按注册顺序返回服务器副本，调用方修改不会污染注册表。</summary>
    /// <returns>当前服务器快照。</returns>
    public IReadOnlyList<CodingAgentMcpServerEntry> List()
    {
        lock (_gate) return _servers.Values.Select(entry => entry with { Config = (JsonObject)entry.Config.DeepClone() }).ToArray();
    }

    /// <summary>【CodingAgent】【MCP 桥接】原子应用扩展进程的已提交快照，先校验所有条目再替换。</summary>
    /// <param name="servers">扩展服务器快照。</param>
    internal void Replace(IReadOnlyList<CodingAgentMcpServerEntry> servers)
    {
        var validated = servers.Select(entry => entry with { Config = CodingAgentMcpServers.Validate(entry.Name, entry.Config) }).ToArray();
        if (validated.Select(entry => CodingAgentMcpServers.Namespace(entry.Name)).Distinct(StringComparer.Ordinal).Count() != validated.Length)
            throw new ArgumentException("MCP server namespace conflict.");
        IReadOnlyList<CodingAgentMcpServerEntry> snapshot;
        lock (_gate)
        {
            _servers.Clear();
            foreach (var entry in validated) _servers.Add(entry.Name, entry);
            snapshot = List();
        }
        Changed?.Invoke(snapshot);
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【MCP 注册表】当前扩展运行时已提交的服务器，连接由 MCP 服务负责。</summary>
    public CodingAgentMcpServerRegistry McpServers { get; } = new();

    /// <summary>【CodingAgent】【MCP 桥接】读取原生服务器快照并保留扩展归属。</summary>
    /// <param name="value">服务器数组；空值清除已加载的定义。</param>
    internal void ApplyMcpServers(System.Text.Json.JsonElement? value) => McpServers.Replace(value is { } servers ?
        servers.EnumerateArray().Select(server => new CodingAgentMcpServerEntry(server.GetProperty("name").GetString()!,
            JsonNode.Parse(server.GetProperty("config").GetRawText())!.AsObject(), server.GetProperty("extensionPath").GetString()!, "extension")).ToArray() : []);
}
