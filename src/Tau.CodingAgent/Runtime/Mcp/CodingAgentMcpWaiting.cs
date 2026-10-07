// 作者：xxx
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime.Mcp;

public sealed partial class CodingAgentMcpService
{
    /// <summary>【CodingAgent】【MCP 脚本等待】仅等待源码引用的命名空间，发现或枚举操作等待全部服务器。</summary>
    /// <param name="code">原始 JavaScript 源码。</param><param name="token">调用取消信号。</param><returns>所需服务器首次连接尝试完成任务。</returns>
    public Task WaitForScriptServersAsync(string code, CancellationToken token = default)
    {
        var discovery = ScriptDiscovery().IsMatch(code);
        return WaitForMatchingServersAsync(connection => discovery || code.Contains(CodingAgentMcpServers.Namespace(connection.Entry.Name), StringComparison.Ordinal), token);
    }

    /// <summary>【CodingAgent】【MCP 单服务器等待】等待指定服务器的首次连接尝试，其他连接不影响管理动作。</summary>
    /// <param name="name">服务器名称。</param><param name="token">调用取消信号。</param><returns>连接尝试完成任务；服务器已移除时直接返回。</returns>
    public Task WaitForServerAsync(string name, CancellationToken token = default) =>
        WaitForMatchingServersAsync(connection => connection.Entry.Name == name, token);

    /// <summary>【CodingAgent】【MCP 选择等待】先等待配置合并，再捕获启用连接，取消等待不取消共享连接。</summary>
    /// <param name="include">选择连接的谓词。</param><param name="token">调用取消信号。</param><returns>所选初始化任务。</returns>
    private async Task WaitForMatchingServersAsync(Func<Connection, bool> include, CancellationToken token)
    {
        Task reload; lock (_gate) reload = _reload;
        await reload.WaitAsync(token).ConfigureAwait(false);
        Task[] attempts; lock (_gate) attempts = _connections.Values.Where(connection => connection.Enabled && include(connection)).Select(connection => connection.Initialization).ToArray();
        await Task.WhenAll(attempts).WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【MCP 发现识别】按上游词边界识别全目录发现接口。</summary>
    /// <returns>ASCII 单词边界表达式，与 JavaScript 正则保持一致。</returns>
    [GeneratedRegex(@"\b(searchTools|describeNamespace|describeTool|ALL_TOOLS)\b", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptDiscovery();
}
