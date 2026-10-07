// 作者：xxx
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp;

public sealed partial class CodingAgentMcpService
{
    private readonly Dictionary<string, SessionOverride> _sessionOverrides = new(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【MCP 项目覆盖入口】只有可信项目中的全局服务器可以建立项目专属开关。</summary><param name="name">服务器名称。</param><returns>是否提供覆盖入口。</returns>
    public bool CanCreateProjectOverride(string name)
    { lock (_gate) return _connections.GetValueOrDefault(name)?.Entry.Scope == "global" && _projectTrusted(); }

    /// <summary>【CodingAgent】【MCP 手动重连】排空旧连接后重建当前服务器，禁用项不能通过重连隐式启用。</summary><param name="name">服务器名称。</param><param name="token">取消。</param><returns>新连接初始化任务。</returns>
    public Task ReconnectAsync(string name, CancellationToken token = default) =>
        AuthenticationActionAsync(name, (_, _, _) => Task.CompletedTask, token, requiresOAuth: false);

    /// <summary>【CodingAgent】【MCP 管理修改】文件服务器保存到实际来源或项目覆盖，扩展服务器只修改当前会话。</summary>
    /// <param name="name">服务器名称。</param><param name="enabled">可选开关。</param><param name="exposure">可选访问策略。</param><param name="inProject">是否建立项目专属覆盖。</param><param name="token">取消。</param><returns>配置和连接更新完成任务。</returns>
    public async Task UpdateServerAsync(string name, bool? enabled = null, string? exposure = null, bool inProject = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = _connections.GetValueOrDefault(name)?.Entry ?? throw new InvalidOperationException($"MCP server \"{name}\" was not found");
            var next = entry.Config.DeepClone().AsObject();
            if (enabled is { } active) next["enabled"] = active;
            if (exposure is not null) next["exposure"] = exposure;
            next = CodingAgentMcpServers.Validate(name, next);
            if (inProject && (entry.Scope != "global" || !_projectTrusted())) throw new InvalidOperationException("Project overrides require a trusted project and a global MCP server");
            if (entry.Scope == "extension")
            {
                var original = _registry.List().FirstOrDefault(value => value.Name == name && value.Source == entry.Source) ??
                    throw new InvalidOperationException("MCP extension registration changed before the update");
                var patch = _sessionOverrides.GetValueOrDefault(name)?.Patch.DeepClone().AsObject() ?? new JsonObject();
                if (enabled is { } value) patch["enabled"] = value;
                if (exposure is not null) patch["exposure"] = next["exposure"]!.DeepClone();
                _sessionOverrides[name] = new(entry.Source, original.Config.DeepClone().AsObject(), patch);
            }
            else
            {
                var path = inProject ? Path.Combine(_cwd, ".tau", "mcp.json") : entry.Override ?? entry.Source;
                CodingAgentMcpConfiguration.Update(path, name, enabled, exposure is null ? null : next["exposure"]!.GetValue<string>(), createOverride: inProject);
            }
            if (exposure is not null) _connections[name].DeclarationResetVersion++;
        }
        await ReloadAsync(token).ConfigureAwait(false);
        if (enabled == true) await WaitForServerAsync(name, token).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【MCP 会话覆盖合并】同一扩展原始注册未变化时保留用户开关和策略，重新注册新配置时清除旧覆盖。</summary>
    /// <param name="entries">当前合并定义。</param><param name="registered">扩展原始快照。</param>
    private void ApplySessionOverrides(Dictionary<string, CodingAgentMcpServerEntry> entries, IReadOnlyList<CodingAgentMcpServerEntry> registered)
    {
        foreach (var pair in _sessionOverrides.ToArray())
        {
            var original = registered.FirstOrDefault(entry => entry.Name == pair.Key);
            if (original is null || original.Source != pair.Value.Source || !JsonNode.DeepEquals(original.Config, pair.Value.Original))
            { _sessionOverrides.Remove(pair.Key); continue; }
            if (!entries.TryGetValue(pair.Key, out var selected) || selected.Scope != "extension") continue;
            var config = selected.Config.DeepClone().AsObject();
            foreach (var patch in pair.Value.Patch) config[patch.Key] = patch.Value?.DeepClone();
            entries[pair.Key] = selected with { Config = config };
        }
    }
    /// <summary>【CodingAgent】【MCP 连接身份】呈现策略和描述变化不重连，开关、超时、凭据及传输参数变化仍替换连接。</summary>
    /// <param name="previous">旧定义。</param><param name="next">新定义。</param><returns>是否可继续复用连接。</returns>
    private static bool CanKeepConnection(CodingAgentMcpServerEntry previous, CodingAgentMcpServerEntry next)
    {
        if (previous.Source != next.Source) return false;
        var oldConfig = previous.Config.DeepClone().AsObject(); var nextConfig = next.Config.DeepClone().AsObject();
        foreach (var key in new[] { "exposure", "toolExposure", "description" }) { oldConfig.Remove(key); nextConfig.Remove(key); }
        return JsonNode.DeepEquals(oldConfig, nextConfig);
    }
    /// <summary>【CodingAgent】【扩展会话覆盖】记录原始注册身份，避免扩展卸载重载时误用过时开关。</summary><param name="Source">扩展路径。</param><param name="Original">原配置。</param><param name="Patch">会话覆盖。</param>
    private sealed record SessionOverride(string Source, JsonObject Original, JsonObject Patch);
}
