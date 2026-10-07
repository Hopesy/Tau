// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【MCP OAuth 设置】客户端密钥已由宿主解析，回调参数沿用 MCP 配置的验证规则。</summary>
public sealed record CodingAgentMcpOAuthSettings
{
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public int? CallbackPort { get; init; }
    public string? CallbackUrl { get; init; }
    public string? Scope { get; init; }
    public string? ClientName { get; init; }
    public string? ClientRegistration { get; init; }
    public Uri? AuthorizationServerMetadataUrl { get; init; }

    /// <summary>【CodingAgent】【MCP OAuth 提供方】将已解析设置应用到当前服务器的独立状态。</summary>
    /// <param name="serverUrl">MCP 地址。</param><param name="store">状态存储。</param><param name="redirectUrl">实际回调。</param><param name="redirect">显式授权展示回调。</param><param name="time">过期时间来源。</param><returns>状态提供方。</returns>
    internal CodingAgentMcpOAuthProvider CreateProvider(string serverUrl, ICodingAgentMcpOAuthStateStore store, string redirectUrl, Func<Uri, CancellationToken, Task> redirect, TimeProvider? time = null) => new(new()
    {
        ServerUrl = serverUrl, Store = store, RedirectUrl = redirectUrl, ClientId = ClientId, ClientSecret = ClientSecret,
        ClientMetadata = new() { ["client_name"] = ClientName ?? "tau" }, OnRedirect = redirect,
        TimeProvider = time ?? TimeProvider.System,
        ClientMetadataDocument = ClientRegistration == "cimd" ? metadata => ClientDocument(serverUrl, redirectUrl, metadata) : null
    });

    /// <summary>【CodingAgent】【OAuth 回调设置】保留配置中精确的固定回调文本，localhost 使用 IPv4 回环监听。</summary><returns>监听配置与固定回调。</returns>
    internal (string Host, string RedirectHost, int? Port, string Path, string? FixedUrl) Callback()
    {
        var uri = new Uri(CallbackUrl ?? "http://127.0.0.1/callback");
        var host = uri.Host.Trim('[', ']');
        var port = !uri.IsDefaultPort ? uri.Port : CallbackPort;
        var fixedUrl = !uri.IsDefaultPort ? CallbackUrl : port is { } selected ? new UriBuilder(uri) { Port = selected }.Uri.AbsoluteUri : null;
        return (host == "localhost" ? "127.0.0.1" : host, host, port, uri.AbsolutePath, fixedUrl);
    }

    /// <summary>【CodingAgent】【OAuth 回调身份】规范 URL 去掉片段后取 SHA-256 前九字节，用于隔离不同服务器的授权响应。</summary><param name="serverUrl">MCP 地址。</param><returns>十二字符 URL 安全标识。</returns>
    internal static string CallbackId(string serverUrl)
    {
        var value = new UriBuilder(CodingAgentMcpOAuthCredentialStore.NormalizeUrl(serverUrl)) { Fragment = "" }.Uri.AbsoluteUri;
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..9]).Replace('+', '-').Replace('/', '_');
    }

    /// <summary>【CodingAgent】【OAuth CIMD 选择】要求公共客户端能力，缺少 iss 回传时使用服务器独立文档和回调路径。</summary>
    /// <param name="serverUrl">MCP 地址。</param><param name="redirectUrl">回调。</param><param name="metadata">服务器元数据。</param><returns>源项目公布的客户端文档。</returns>
    private static CodingAgentMcpOAuthClientDocument ClientDocument(string serverUrl, string redirectUrl, JsonObject? metadata)
    {
        if (!CodingAgentMcpOAuthJson.Flag(metadata, "client_id_metadata_document_supported") ||
            !CodingAgentMcpOAuthJson.List(metadata, "token_endpoint_auth_methods_supported").Contains("none"))
            throw new InvalidOperationException("The authorization server does not support Client ID Metadata Documents for public clients; remove oauth.clientRegistration \"cimd\"");
        if (CodingAgentMcpOAuthJson.Flag(metadata, "authorization_response_iss_parameter_supported")) return new("https://pi.dev/oauth/client.json", redirectUrl);
        var id = CallbackId(serverUrl);
        return new($"https://pi.dev/oauth/{id}/client.json", new UriBuilder(redirectUrl) { Path = "/callback/" + id }.Uri.AbsoluteUri);
    }
}
