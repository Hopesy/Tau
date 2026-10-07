// 作者：xxx
using System.Text.RegularExpressions;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public static partial class CodingAgentAutocompleteProviderFactory
{
    /// <summary>【CodingAgent】【MCP 参数补全】动态提供管理动作和符合认证条件的启用服务器，保留动作前缀用于整体替换。</summary>
    /// <param name="service">可选会话服务。</param><param name="prefix">命令后的完整参数。</param><param name="token">取消信号。</param><returns>匹配项；不支持的参数形状返回空值。</returns>
    private static ValueTask<IReadOnlyList<TuiAutocompleteItem>?> McpCompletions(CodingAgentMcpService? service, string prefix, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parts = McpArgumentWhitespace().Split(prefix.TrimStart());
        if (parts.Length > 2) return new((IReadOnlyList<TuiAutocompleteItem>?)null);
        if (parts.Length == 1) return new(new[] { "login", "logout", "reconnect" }
            .Where(action => action.StartsWith(parts[0], StringComparison.Ordinal))
            .Select(action => new TuiAutocompleteItem(action + " ", action)).ToArray());
        if (parts[0] is not ("login" or "logout" or "reconnect") || service is null) return new((IReadOnlyList<TuiAutocompleteItem>?)null);
        var status = service.GetStatus().ToDictionary(item => item.Name, StringComparer.Ordinal);
        IReadOnlyList<TuiAutocompleteItem> items = service.GetServerEntries()
            .Where(entry => entry.Config["enabled"]?.GetValue<bool>() != false &&
                (parts[0] == "reconnect" || CodingAgentMcpService.UsesOAuth(entry)) && entry.Name.StartsWith(parts[1], StringComparison.Ordinal))
            .Select(entry => new TuiAutocompleteItem(parts[0] + " " + entry.Name, entry.Name,
                status.TryGetValue(entry.Name, out var current) ? DescribeMcpCompletion(current) : "starting")).ToArray();
        return new(items.Count == 0 ? null : items);
    }

    /// <summary>【CodingAgent】【MCP 补全状态】为候选项提供不含凭据的短状态，失败只展示首行。</summary>
    /// <param name="status">连接快照。</param><returns>候选说明。</returns>
    private static string DescribeMcpCompletion(CodingAgentMcpServerStatus status) => status.State switch
    {
        "connected" => $"connected · {status.ToolCount} tool{(status.ToolCount == 1 ? "" : "s")}" +
            (status.ResourceCount == 0 ? "" : $" · {status.ResourceCount} resource{(status.ResourceCount == 1 ? "" : "s")}"),
        "needs-auth" => "needs sign-in",
        "failed" => "failed: " + (status.Error ?? "unknown error").Split('\n', 2)[0].TrimEnd('\r'),
        "connecting" => "connecting…",
        _ => status.State
    };

    /// <summary>【CodingAgent】【MCP 参数分隔】保留末尾空参数，从动作补全过渡到服务器补全。</summary><returns>空白分隔表达式。</returns>
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex McpArgumentWhitespace();
}
