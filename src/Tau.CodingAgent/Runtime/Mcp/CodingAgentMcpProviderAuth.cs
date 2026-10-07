// 作者：xxx
namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 提供方认证】逐请求读取会话提供方令牌，不复制凭据到 MCP 存储，也不扩大认证作用域。</summary>
public sealed class CodingAgentMcpProviderAuth : ICodingAgentMcpHttpAuth
{
    private readonly string _provider;
    private readonly Func<string, CancellationToken, Task<string?>> _token;
    public bool CanHandleUnauthorized => false;

    /// <summary>【CodingAgent】【MCP 认证绑定】绑定提供方和当前令牌解析器。</summary><param name="provider">提供方标识。</param><param name="token">逐请求调用的解析器。</param>
    public CodingAgentMcpProviderAuth(string provider, Func<string, CancellationToken, Task<string?>> token) { _provider = provider; _token = token; }
    /// <summary>【CodingAgent】【MCP 令牌读取】复用提供方当前凭据及其过期刷新机制。</summary><param name="token">取消信号。</param><returns>有效令牌或空值。</returns>
    public Task<string?> GetTokenAsync(CancellationToken token) => _token(_provider, token);
    /// <summary>【CodingAgent】【MCP 认证拒绝】提供方令牌不处理 MCP 的 OAuth 质询，认证错误由连接状态处理。</summary>
    /// <param name="response">认证响应。</param><param name="usedToken">已使用令牌。</param><param name="token">取消信号。</param><returns>已完成任务。</returns>
    public Task OnUnauthorizedAsync(HttpResponseMessage response, string? usedToken, CancellationToken token) => Task.CompletedTask;
}
