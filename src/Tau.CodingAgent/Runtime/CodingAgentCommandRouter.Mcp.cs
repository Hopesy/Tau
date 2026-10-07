// 作者：xxx
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.CodingAgent.Runtime.Mcp.OAuth;
using Tau.Tui.Components;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentCommandRouter
{
    private readonly Func<Func<CodingAgentMcpMenu>, CancellationToken, Task<string?>>? _mcpMenu;

    /// <summary>【CodingAgent】【MCP 会话命令】无参数时显示管理菜单或状态，子命令支持登录、注销和重连。</summary>
    /// <param name="parts">分词参数。</param><param name="token">取消。</param><returns>命令结果。</returns>
    private async Task<CodingAgentCommandResult> HandleMcpCommandAsync(IReadOnlyList<string> parts, CancellationToken token)
    {
        var service = (_runner as RuntimeCodingAgentRunner)?.McpService;
        if (service is null) return CodingAgentCommandResult.Error("MCP is not enabled for this session.");
        try
        {
            if (parts.Count == 1)
            {
                if (_mcpMenu is not null) await ManageMcpAsync(service, token).ConfigureAwait(false);
                return CodingAgentCommandResult.Status(McpStatus(service));
            }
            if (parts.Count > 3 || parts[1] is not ("login" or "logout" or "reconnect")) return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/mcp"));
            var action = parts[1]; var all = service.GetServerEntries();
            var entries = all.Where(entry => entry.Config["enabled"]?.GetValue<bool>() != false &&
                (action == "reconnect" || CodingAgentMcpService.UsesOAuth(entry))).ToArray();
            var states = service.GetStatus().ToDictionary(status => status.Name, StringComparer.Ordinal);
            var preferred = entries.Where(entry => states.TryGetValue(entry.Name, out var status) &&
                (action == "reconnect" ? status.State is "failed" or "disconnected" : status.State == "needs-auth")).ToArray();
            var name = parts.Count == 3 ? parts[2] : entries.Length == 1 ? entries[0].Name : preferred.Length == 1 ? preferred[0].Name : null;
            if (name is not null && !all.Any(entry => entry.Name == name)) return CodingAgentCommandResult.Error($"No MCP server named \"{name}\".");
            if (name is not null && !entries.Any(entry => entry.Name == name))
                return CodingAgentCommandResult.Error(action == "reconnect" ? "No enabled MCP server to reconnect." : "No enabled MCP server uses OAuth. Only HTTP servers without an Authorization header do.");
            if (name is null && _mcpMenu is not null)
                name = await _mcpMenu(() => new("Choose MCP server", entries.Select(entry => new TuiSelectItem(entry.Name, entry.Name)).ToArray()), token).ConfigureAwait(false);
            if (name is null) return CodingAgentCommandResult.Status(entries.Length == 0 ? "No eligible MCP servers." : "Choose a server: " + string.Join(", ", entries.Select(entry => entry.Name)));
            await RunMcpActionAsync(service, name, action, token).ConfigureAwait(false);
            var status = service.GetStatus().FirstOrDefault(value => value.Name == name);
            return CodingAgentCommandResult.Status($"{name}: {status?.State ?? "removed"}");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return CodingAgentCommandResult.Status("MCP sign-in cancelled."); }
        catch (Exception failure) when (failure is System.Net.Sockets.SocketException or HttpRequestException or TimeoutException)
        { return CodingAgentCommandResult.Error(failure.Message); }
    }

    /// <summary>【CodingAgent】【MCP 管理循环】展示动态服务器列表，在子菜单执行登录、开关和策略动作，错误保留在当前菜单。</summary>
    /// <param name="service">会话 MCP 服务。</param><param name="token">取消。</param><returns>菜单关闭任务。</returns>
    private async Task ManageMcpAsync(CodingAgentMcpService service, CancellationToken token)
    {
        while (true)
        {
            var name = await _mcpMenu!(() => new("MCP servers", service.GetStatus().OrderBy(status => status.Name, StringComparer.Ordinal)
                .Select(status => new TuiSelectItem(status.Name, status.Name, McpStateWithCounts(status))).ToArray(),
                service.ConfigurationErrors.Count == 0 ? "Select a server to manage." : string.Join("\n", service.ConfigurationErrors)), token).ConfigureAwait(false);
            if (name is null) return;
            string? error = null;
            while (true)
            {
                var action = await _mcpMenu(() => ServerMenu(service, name, error), token).ConfigureAwait(false);
                if (action is null) break;
                try
                {
                    error = null;
                    if (action == "tools")
                    {
                        await _mcpMenu(() =>
                        {
                            var config = service.GetServerEntries().FirstOrDefault(entry => entry.Name == name)?.Config;
                            return new("Tools of " + name, service.GetTools().Where(tool => tool.Label.StartsWith(name + "/", StringComparison.Ordinal))
                                .Select(tool =>
                                {
                                    var original = tool.Label[(name.Length + 1)..];
                                    var exposure = config is null ? tool.Exposure : CodingAgentMcpServers.GetToolExposure(config, original);
                                    return new TuiSelectItem(tool.Name, original, $"[{exposure}] {tool.Description.Split('\n', 2)[0]}");
                                }).ToArray());
                        }, token).ConfigureAwait(false);
                    }
                    else if (action == "exposure")
                    {
                        var selected = service.GetServerEntries().FirstOrDefault(entry => entry.Name == name)?.Config["exposure"]?.GetValue<string>() ?? "codemode";
                        var choice = await _mcpMenu(() => new("Exposure of " + name,
                            [new("codemode", "codemode", "Call from scripts; discover with searchTools"),
                             new("deferred", "deferred", "Load with tool_search, then call directly"),
                             new("direct", "direct", "Declare directly to the model")], Selected: selected), token).ConfigureAwait(false);
                        if (choice is not null && choice != selected) await service.UpdateServerAsync(name, exposure: choice, token: token).ConfigureAwait(false);
                    }
                    else if (action is "enable" or "disable" or "enable-project" or "disable-project")
                        await service.UpdateServerAsync(name, enabled: action.StartsWith("enable", StringComparison.Ordinal), inProject: action.EndsWith("-project", StringComparison.Ordinal), token: token).ConfigureAwait(false);
                    else await RunMcpActionAsync(service, name, action, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { error = "Sign-in cancelled."; }
                catch (Exception failure) when (failure is not OperationCanceledException) { error = failure.Message; }
            }
        }
    }

    /// <summary>【CodingAgent】【MCP 单服务器菜单】根据启用状态和认证方式展示可用动作，不展示凭据字段。</summary>
    /// <param name="service">服务。</param><param name="name">服务器名。</param><param name="error">最近动作错误。</param><returns>动态菜单。</returns>
    private static CodingAgentMcpMenu ServerMenu(CodingAgentMcpService service, string name, string? error)
    {
        var entry = service.GetServerEntries().FirstOrDefault(value => value.Name == name);
        if (entry is null) return new("MCP server removed", [], "Return to the server list.");
        var status = service.GetStatus().FirstOrDefault(value => value.Name == name);
        var active = entry.Config["enabled"]?.GetValue<bool>() != false;
        var items = new List<TuiSelectItem>();
        if (active)
        {
            if (status?.State == "needs-auth" && CodingAgentMcpService.UsesOAuth(entry)) items.Add(new("login", "Sign in", "Open the browser"));
            if (status?.State == "connected") items.Add(new("tools", "Tools", $"{status.ToolCount} offered"));
            if (status?.State is "failed" or "disconnected" or "connected" or "needs-auth") items.Add(new("reconnect", "Reconnect"));
            if (status?.State == "connected" && CodingAgentMcpService.UsesOAuth(entry)) items.Add(new("logout", "Sign out", "Delete this server's credentials"));
            items.Add(new("exposure", "Exposure", entry.Config["exposure"]?.GetValue<string>() ?? "codemode"));
        }
        items.Add(new(active ? "disable" : "enable", active ? "Disable" : "Enable", entry.Scope == "extension" ? "Current session" : "Save configuration"));
        if (service.CanCreateProjectOverride(name)) items.Add(new(active ? "disable-project" : "enable-project", active ? "Disable in this project" : "Enable in this project"));
        var source = entry.Scope == "extension" ? "Registered by " + entry.Source : "Saved to " + (entry.Override ?? entry.Source);
        return new("MCP server " + name, items, $"{(status is null ? "removed" : McpStateWithCounts(status))}\n{source}" + ((error ?? status?.Error) is { } failure ? "\n" + failure : ""));
    }

    /// <summary>【CodingAgent】【MCP 管理动作】登录使用当前交互宿主，注销和重连沿用服务的串行生命周期。</summary>
    /// <param name="service">服务。</param><param name="name">服务器名。</param><param name="action">动作。</param><param name="token">取消。</param><returns>动作任务。</returns>
    private async Task RunMcpActionAsync(CodingAgentMcpService service, string name, string action, CancellationToken token)
    {
        await (action switch
        {
            "login" => service.SignInAsync(name, new McpPrompt(_oauthLoginCallbacksFactory()), token),
            "logout" => service.SignOutAsync(name, token),
            "reconnect" => service.ReconnectAsync(name, token),
            _ => throw new InvalidOperationException("Unknown MCP action")
        }).ConfigureAwait(false);
        if (action == "logout") return;
        var status = service.GetStatus().FirstOrDefault(value => value.Name == name);
        if (status?.State == "connected") return;
        throw new InvalidOperationException((action == "login" ? "Signed in, but " : "Could not reconnect: ") +
            (status?.State == "needs-auth" ? $"MCP server \"{name}\" still requires sign-in" : status?.Error ?? status?.State ?? "MCP server was removed"));
    }
    /// <summary>【CodingAgent】【MCP 状态文本】非交互模式直接显示连接状态和启动错误，不触发额外资源请求。</summary><param name="service">服务。</param><returns>状态摘要。</returns>
    private static string McpStatus(CodingAgentMcpService service)
    {
        var lines = service.GetStatus().Select(status => $"{status.Name}: {McpStateWithCounts(status)}" + (status.Error is null ? "" : "\n  " + status.Error)).Concat(service.ConfigurationErrors).ToArray();
        return lines.Length == 0 ? "No MCP servers configured." : string.Join("\n", lines);
    }
    /// <summary>【CodingAgent】【MCP 状态数量】统一菜单和非交互状态中的工具、资源及模板数量。</summary>
    /// <param name="status">状态快照。</param><returns>单行状态。</returns>
    private static string McpStateWithCounts(CodingAgentMcpServerStatus status) => $"{status.State}, {status.ToolCount} tools" +
        (status.ResourceCount == 0 ? "" : $", {status.ResourceCount} resources") +
        (status.ResourceTemplateCount == 0 ? "" : $", {status.ResourceTemplateCount} URI templates");
    /// <summary>【CodingAgent】【MCP 登录桥接】复用现有 UI 授权展示与可取消输入。</summary><param name="callbacks">当前宿主登录回调。</param>
    private sealed class McpPrompt(IOAuthLoginCallbacks callbacks) : ICodingAgentMcpSignInPrompt
    {
        /// <summary>【CodingAgent】【MCP 授权展示】交给当前 UI 打开和展示 URL。</summary><param name="url">地址。</param><param name="token">取消。</param><returns>完成任务。</returns>
        public Task ShowAuthorizationUrlAsync(Uri url, CancellationToken token) { token.ThrowIfCancellationRequested(); callbacks.OnAuth(url.AbsoluteUri); return Task.CompletedTask; }
        /// <summary>【CodingAgent】【MCP 回调输入】提示必须粘贴完整回调 URL，控制台输入可取消且不进入历史。</summary><param name="token">取消。</param><returns>回调 URL。</returns>
        public async Task<string?> PromptForRedirectUrlAsync(CancellationToken token)
        {
            const string message = "If the browser cannot reach this machine, paste the full redirect URL:";
            if (callbacks is ConsoleOAuthLoginCallbacks) { callbacks.OnProgress(message); return await CodingAgentSecretInput.ReadAsync(token).ConfigureAwait(false); }
            return await callbacks.OnPromptAsync(message, null, true, token).ConfigureAwait(false);
        }
    }
}
