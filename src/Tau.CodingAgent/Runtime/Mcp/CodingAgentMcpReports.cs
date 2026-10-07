// 作者：xxx
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp;

public sealed partial class CodingAgentMcpService
{
    /// <summary>【CodingAgent】【MCP 管理定义】返回独立配置副本，供管理入口确定编辑位置和能力。</summary><returns>服务器定义。</returns>
    public IReadOnlyList<CodingAgentMcpServerEntry> GetServerEntries()
    { lock (_gate) return _connections.Values.Select(connection => connection.Entry with { Config = connection.Entry.Config.DeepClone().AsObject() }).ToArray(); }

    /// <summary>【CodingAgent】【MCP 检查报告】等待初始化后按配置顺序生成无凭据字段的连接、工具和资源报告。</summary><param name="token">取消。</param><returns>管理报告 JSON 数组。</returns>
    public async Task<JsonArray> GetReportsAsync(CancellationToken token = default)
    {
        await WaitForServersAsync(token).ConfigureAwait(false);
        lock (_gate) return new JsonArray(_connections.Values.Select(Report).Cast<JsonNode?>().ToArray());
    }

    /// <summary>【CodingAgent】【MCP 单服务器报告】调用方持有服务锁，报告与菜单共用初始连接或通知刷新后的资源快照。</summary>
    /// <param name="connection">当前连接。</param><returns>单台服务器报告。</returns>
    private static JsonObject Report(Connection connection)
    {
        var entry = connection.Entry; var config = entry.Config; var exposure = config["exposure"]?.GetValue<string>() ?? "codemode";
        var report = new JsonObject
        {
            ["name"] = entry.Name, ["scope"] = entry.Scope, ["source"] = entry.Source, ["enabled"] = connection.Enabled,
            ["exposure"] = exposure, ["transport"] = config["url"]?.GetValue<string>() ??
                string.Join(" ", new[] { config["command"]!.GetValue<string>() }.Concat((config["args"] as JsonArray ?? []).Select(value => value!.GetValue<string>()))),
            ["state"] = connection.State, ["tools"] = new JsonArray(connection.Tools.Select(tool => (JsonNode?)JsonValue.Create(tool.GetProperty("name").GetString())).ToArray())
        };
        if (entry.Override is not null) report["override"] = entry.Override;
        if (connection.Error is not null && connection.State != "connected") report["error"] = connection.Error;
        var overrides = new JsonObject();
        foreach (var tool in connection.Tools)
        {
            var name = tool.GetProperty("name").GetString()!; var policy = CodingAgentMcpServers.GetToolExposure(config, name);
            if (policy != exposure) overrides[name] = policy;
        }
        if (overrides.Count > 0) report["toolExposure"] = overrides;
        if (connection.HasResources && connection.Enabled && connection.State == "connected")
        {
            report["resources"] = connection.Resources.Count;
            report["resourceTemplates"] = connection.ResourceTemplates.Count;
        }
        return report;
    }
}
