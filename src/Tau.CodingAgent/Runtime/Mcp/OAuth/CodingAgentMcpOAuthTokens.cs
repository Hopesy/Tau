// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【OAuth 客户端文档】用 HTTPS 元数据文档 URL 作为客户端标识，并声明对应回调。</summary>
public sealed record CodingAgentMcpOAuthClientDocument(string Url, string RedirectUrl);
/// <summary>【CodingAgent】【OAuth 自定义客户端认证】宿主按端点需求修改请求头及表单。</summary>
/// <param name="headers">可修改的请求头。</param><param name="parameters">可修改的表单。</param><param name="url">端点。</param><param name="metadata">授权服务器元数据。</param><param name="token">取消。</param><returns>修改任务。</returns>
public delegate Task CodingAgentMcpOAuthClientAuthentication(HttpRequestHeaders headers, IDictionary<string, string> parameters, Uri url, JsonObject? metadata, CancellationToken token);
/// <summary>【CodingAgent】【OAuth 错误】保留标准错误码和可选文档地址，供上层精确清理凭据。</summary>
public sealed class CodingAgentMcpOAuthException : InvalidOperationException
{
    public string Code { get; }
    public string? ErrorUri { get; }
    /// <summary>【CodingAgent】【OAuth 错误创建】记录服务端错误，不包含提交表单。</summary><param name="code">错误码。</param><param name="description">错误说明。</param><param name="uri">错误文档。</param>
    public CodingAgentMcpOAuthException(string code, string description, string? uri = null) : base(description) { Code = code; ErrorUri = uri; }
}
/// <summary>【CodingAgent】【OAuth 安全端点】禁止向非回环 HTTP 端点发送授权码或刷新令牌。</summary>
public sealed class CodingAgentMcpOAuthInsecureEndpointException : InvalidOperationException
{
    /// <summary>【CodingAgent】【OAuth 安全端点错误】标识被拒绝的端点。</summary><param name="url">端点地址。</param>
    public CodingAgentMcpOAuthInsecureEndpointException(Uri url) : base($"OAuth endpoint must use HTTPS except on loopback: {url}") { }
}
/// <summary>【CodingAgent】【OAuth 需要授权】普通 MCP 请求不能完成需要用户交互的登录。</summary>
public sealed class CodingAgentMcpOAuthAuthorizationRequiredException : InvalidOperationException
{
    /// <summary>【CodingAgent】【OAuth 授权提示】引导宿主进入显式登录流程。</summary>
    public CodingAgentMcpOAuthAuthorizationRequiredException() : base("MCP server requires authorization. Sign in through /mcp.") { }
}
/// <summary>【CodingAgent】【OAuth 令牌参数】描述注册客户端、资源绑定及自定义认证。</summary>
public sealed record CodingAgentMcpOAuthTokenOptions
{
    public required JsonObject ClientInformation { get; init; }
    public JsonObject? Metadata { get; init; }
    public string? Resource { get; init; }
    public CodingAgentMcpOAuthClientAuthentication? AddClientAuthentication { get; init; }
}

/// <summary>【CodingAgent】【MCP OAuth 协议】实现动态注册、PKCE、授权码交换及刷新，不隐式启动浏览器。</summary>
public sealed partial class CodingAgentMcpOAuthFlow(HttpClient http)
{
    /// <summary>【CodingAgent】【OAuth 授权 URL】验证授权码和 S256 能力，并生成独立 PKCE 验证器。</summary>
    /// <param name="issuer">授权服务器。</param><param name="options">客户端和资源配置。</param><param name="redirectUrl">回调。</param><param name="scope">请求权限。</param><param name="state">响应校验值。</param><returns>授权 URL 与验证器。</returns>
    public static (Uri AuthorizationUrl, string CodeVerifier) StartAuthorization(Uri issuer, CodingAgentMcpOAuthTokenOptions options, string redirectUrl, string? scope = null, string? state = null)
    {
        if (options.Metadata is { } metadata && !CodingAgentMcpOAuthJson.List(metadata, "response_types_supported").Contains("code"))
            throw new InvalidDataException("Authorization server does not support authorization codes");
        if (options.Metadata?["code_challenge_methods_supported"] is not null && !CodingAgentMcpOAuthJson.List(options.Metadata, "code_challenge_methods_supported").Contains("S256"))
            throw new InvalidDataException("Authorization server does not support PKCE S256");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code", ["client_id"] = CodingAgentMcpOAuthJson.Text(options.ClientInformation, "client_id")!,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["redirect_uri"] = redirectUrl
        };
        if (!string.IsNullOrEmpty(state)) parameters["state"] = state;
        if (!string.IsNullOrEmpty(scope)) parameters["scope"] = scope;
        if (SplitScope(scope).Contains("offline_access")) parameters["prompt"] = "consent";
        if (!string.IsNullOrEmpty(options.Resource)) parameters["resource"] = options.Resource;
        var url = new Uri(CodingAgentMcpOAuthJson.Text(options.Metadata, "authorization_endpoint") ?? new Uri(issuer, "/authorize").AbsoluteUri);
        return (SetQuery(url, parameters), verifier);
    }

    /// <summary>【CodingAgent】【OAuth 动态注册】向注册端点提交客户端元数据，成功后验证返回客户端信息。</summary>
    /// <param name="issuer">授权服务器。</param><param name="metadata">服务器元数据。</param><param name="clientMetadata">客户端元数据。</param><param name="scope">覆盖权限。</param><param name="token">取消。</param><returns>注册客户端。</returns>
    public async Task<JsonObject> RegisterClientAsync(Uri issuer, JsonObject? metadata, JsonObject clientMetadata, string? scope = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var endpoint = CodingAgentMcpOAuthJson.Text(metadata, "registration_endpoint");
        if (metadata is not null && endpoint is null) throw new InvalidOperationException("Authorization server does not support dynamic client registration");
        var body = clientMetadata.DeepClone().AsObject();
        if (!string.IsNullOrEmpty(scope)) body["scope"] = scope;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint is null ? new Uri(issuer, "/register") : new Uri(endpoint));
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OAuth registration failed: HTTP {(int)response.StatusCode}: {text}");
        return CodingAgentMcpOAuthJson.ClientInformation(JsonNode.Parse(text));
    }

    /// <summary>【CodingAgent】【OAuth 授权码交换】提交授权码、原始回调及 PKCE 验证器。</summary>
    /// <param name="issuer">授权服务器。</param><param name="options">客户端参数。</param><param name="code">授权码。</param><param name="verifier">PKCE 验证器。</param><param name="redirectUrl">原回调。</param><param name="token">取消。</param><returns>已验证令牌。</returns>
    public Task<JsonObject> ExchangeCodeAsync(Uri issuer, CodingAgentMcpOAuthTokenOptions options, string code, string verifier, string redirectUrl, CancellationToken token = default) =>
        TokenRequestAsync(issuer, options, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirectUrl }, token);

    /// <summary>【CodingAgent】【OAuth 令牌刷新】服务端未返回轮换令牌时保留原刷新令牌。</summary>
    /// <param name="issuer">授权服务器。</param><param name="options">客户端参数。</param><param name="refreshToken">原刷新令牌。</param><param name="token">取消。</param><returns>刷新结果。</returns>
    public async Task<JsonObject> RefreshAsync(Uri issuer, CodingAgentMcpOAuthTokenOptions options, string refreshToken, CancellationToken token = default)
    {
        var result = await TokenRequestAsync(issuer, options, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, token).ConfigureAwait(false);
        result["refresh_token"] ??= refreshToken;
        return result;
    }

    /// <summary>【CodingAgent】【OAuth 令牌 HTTP】选择客户端认证方式，先识别响应错误码再判断 HTTP 状态。</summary>
    /// <param name="issuer">授权服务器。</param><param name="options">客户端参数。</param><param name="parameters">授权表单。</param><param name="token">取消。</param><returns>标准令牌。</returns>
    private async Task<JsonObject> TokenRequestAsync(Uri issuer, CodingAgentMcpOAuthTokenOptions options, Dictionary<string, string> parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 1. 【MCP】【令牌请求】在放入任何凭据前检查目标地址
        var url = SecureEndpoint(new Uri(CodingAgentMcpOAuthJson.Text(options.Metadata, "token_endpoint") ?? new Uri(issuer, "/token").AbsoluteUri));
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrEmpty(options.Resource)) parameters["resource"] = options.Resource;
        if (options.AddClientAuthentication is { } authenticate) await authenticate(request.Headers, parameters, url, options.Metadata, token).ConfigureAwait(false);
        else
        {
            var method = ClientAuthMethod(options.ClientInformation, CodingAgentMcpOAuthJson.List(options.Metadata, "token_endpoint_auth_methods_supported"));
            var id = CodingAgentMcpOAuthJson.Text(options.ClientInformation, "client_id")!;
            var secret = CodingAgentMcpOAuthJson.Text(options.ClientInformation, "client_secret");
            if (method == "client_secret_basic")
            {
                if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("client_secret_basic requires a client secret");
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret)));
            }
            else
            {
                parameters["client_id"] = id;
                if (method == "client_secret_post" && !string.IsNullOrEmpty(secret)) parameters["client_secret"] = secret;
            }
        }
        // 2. 【MCP】【令牌响应】服务端可能用 200 状态报告 OAuth 错误
        request.Content = new FormUrlEncodedContent(parameters);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        JsonNode? value = null;
        try { value = JsonNode.Parse(text); } catch (JsonException) { }
        if (value is JsonObject obj && CodingAgentMcpOAuthJson.Text(obj, "error") is { } error)
            throw new CodingAgentMcpOAuthException(error, CodingAgentMcpOAuthJson.Text(obj, "error_description") ?? error, CodingAgentMcpOAuthJson.Text(obj, "error_uri"));
        if (!response.IsSuccessStatusCode) throw new CodingAgentMcpOAuthException("server_error", $"HTTP {(int)response.StatusCode}: {text}");
        return CodingAgentMcpOAuthJson.Tokens(value);
    }

    /// <summary>【CodingAgent】【OAuth 安全检查】只有 HTTPS 或回环 HTTP 地址可接收敏感令牌数据。</summary><param name="url">目标。</param><returns>已验证地址。</returns>
    internal static Uri SecureEndpoint(Uri url)
    {
        if (url.Scheme != "https" && !(url.Scheme == "http" && url.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1"))
            throw new CodingAgentMcpOAuthInsecureEndpointException(url);
        return url;
    }
    /// <summary>【CodingAgent】【客户端认证选择】优先尊重受支持的注册提示，否则按密钥和端点能力选择。</summary><param name="client">客户端。</param><param name="supported">端点能力。</param><returns>标准认证方式。</returns>
    private static string ClientAuthMethod(JsonObject client, string[] supported)
    {
        var hinted = CodingAgentMcpOAuthJson.Text(client, "token_endpoint_auth_method");
        if (hinted is "client_secret_basic" or "client_secret_post" or "none" && (supported.Length == 0 || supported.Contains(hinted))) return hinted;
        var secret = !string.IsNullOrEmpty(CodingAgentMcpOAuthJson.Text(client, "client_secret"));
        if (supported.Length == 0) return secret ? "client_secret_basic" : "none";
        if (secret && supported.Contains("client_secret_basic")) return "client_secret_basic";
        if (secret && supported.Contains("client_secret_post")) return "client_secret_post";
        return supported.Contains("none") ? "none" : secret ? "client_secret_post" : "none";
    }
    /// <summary>【CodingAgent】【PKCE 编码】生成无填充 URL 安全 Base64。</summary><param name="bytes">随机值或摘要。</param><returns>编码文本。</returns>
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    /// <summary>【CodingAgent】【OAuth 权限拆分】忽略空白分隔产生的空项。</summary><param name="scope">权限文本。</param><returns>权限序列。</returns>
    private static string[] SplitScope(string? scope) => (scope ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    /// <summary>【CodingAgent】【授权查询参数】替换协议参数并保留端点原有的其他查询参数。</summary><param name="url">端点。</param><param name="parameters">待覆盖参数。</param><returns>新地址。</returns>
    private static Uri SetQuery(Uri url, IReadOnlyDictionary<string, string> parameters)
    {
        var remaining = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(part =>
            !parameters.ContainsKey(Uri.UnescapeDataString(part.Split('=', 2)[0].Replace('+', ' '))));
        var added = parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
        return new UriBuilder(url) { Query = string.Join("&", remaining.Concat(added)) }.Uri;
    }
}
