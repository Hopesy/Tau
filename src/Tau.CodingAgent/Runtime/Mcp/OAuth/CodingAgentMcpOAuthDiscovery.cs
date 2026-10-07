// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【OAuth 质询】服务端请求的资源文档、权限及错误描述。</summary>
public sealed record CodingAgentMcpOAuthChallenge(Uri? ResourceMetadataUrl = null, string? Scope = null, string? Error = null, string? ErrorDescription = null);

/// <summary>【CodingAgent】【OAuth 发行方校验】发现文档或授权响应的发行方与预期不一致。</summary>
public sealed class CodingAgentMcpOAuthIssuerException : InvalidOperationException
{
    /// <summary>【CodingAgent】【OAuth 发行方错误】记录预期与实际发行方，不包含令牌。</summary><param name="expected">预期发行方。</param><param name="actual">响应发行方。</param>
    public CodingAgentMcpOAuthIssuerException(string expected, string? actual) : base($"OAuth issuer mismatch: expected {expected}, received {actual ?? "(missing)"}") { }
}

/// <summary>【CodingAgent】【MCP OAuth 发现】按资源路径和 OAuth/OIDC 候选顺序发现端点，并校验发行方与资源范围。</summary>
public sealed class CodingAgentMcpOAuthDiscovery(HttpClient http)
{
    /// <summary>【CodingAgent】【OAuth 质询解析】仅接受 Bearer 和 DPoP 方案，忽略空字段及不可解析的资源地址。</summary><param name="header">WWW-Authenticate。</param><returns>独立质询。</returns>
    public static CodingAgentMcpOAuthChallenge ParseChallenge(string? header)
    {
        if (string.IsNullOrEmpty(header)) return new();
        var scheme = Regex.Match(header.TrimStart(), @"^\S+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Value;
        if (!scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("dpop", StringComparison.OrdinalIgnoreCase)) return new();
        var metadata = Field(header, "resource_metadata");
        return new(Uri.TryCreate(metadata, UriKind.Absolute, out var url) ? url : null, Field(header, "scope"), Field(header, "error"), Field(header, "error_description"));
    }

    /// <summary>【CodingAgent】【资源文档发现】先访问路径专属文档，只有缺失状态才回退根文档；显式 URL 不回退。</summary>
    /// <param name="serverUrl">MCP 地址。</param><param name="metadataUrl">质询指定文档。</param><param name="token">取消。</param><returns>已验证的资源文档。</returns>
    public async Task<JsonObject> ProtectedResourceAsync(Uri serverUrl, Uri? metadataUrl = null, CancellationToken token = default)
    {
        var first = metadataUrl ?? AtOrigin(serverUrl, "/.well-known/oauth-protected-resource" + Suffix(serverUrl.AbsolutePath));
        using var response = await FetchAsync(first, token).ConfigureAwait(false);
        if (metadataUrl is null && serverUrl.AbsolutePath != "/" && Miss((int)response.StatusCode))
        {
            using var fallback = await FetchAsync(AtOrigin(serverUrl, "/.well-known/oauth-protected-resource"), token).ConfigureAwait(false);
            return CodingAgentMcpOAuthJson.ProtectedResource(await ReadAsync(fallback, "OAuth protected resource metadata", token).ConfigureAwait(false));
        }
        return CodingAgentMcpOAuthJson.ProtectedResource(await ReadAsync(response, "OAuth protected resource metadata", token).ConfigureAwait(false));
    }

    /// <summary>【CodingAgent】【授权发现候选】按 OAuth、前置路径 OIDC、后置路径 OIDC 的优先级生成地址。</summary><param name="issuer">发行方地址。</param><returns>候选地址。</returns>
    public static IReadOnlyList<Uri> AuthorizationServerUrls(Uri issuer)
    {
        var path = Suffix(issuer.AbsolutePath);
        var result = new List<Uri> { AtOrigin(issuer, "/.well-known/oauth-authorization-server" + path), AtOrigin(issuer, "/.well-known/openid-configuration" + path) };
        if (path.Length > 0) result.Add(AtOrigin(issuer, path + "/.well-known/openid-configuration"));
        return result;
    }

    /// <summary>【CodingAgent】【授权文档发现】缺失候选继续探测，其余 HTTP 或文档错误直接失败，默认验证发行方。</summary>
    /// <param name="issuer">预期发行方原文本。</param><param name="skipIssuerValidation">是否由宿主显式跳过发行方比较。</param><param name="token">取消。</param><returns>文档或全部缺失时的空值。</returns>
    public async Task<JsonObject?> AuthorizationServerAsync(string issuer, bool skipIssuerValidation = false, CancellationToken token = default)
    {
        foreach (var url in AuthorizationServerUrls(new Uri(issuer)))
        {
            using var response = await FetchAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && Miss((int)response.StatusCode)) continue;
            var metadata = CodingAgentMcpOAuthJson.AuthorizationServer(await ReadAsync(response, "authorization server metadata", token).ConfigureAwait(false));
            var actual = CodingAgentMcpOAuthJson.Text(metadata, "issuer")!;
            if (!skipIssuerValidation && Suffix(actual) != Suffix(issuer)) throw new CodingAgentMcpOAuthIssuerException(issuer, actual);
            return metadata;
        }
        return null;
    }

    /// <summary>【CodingAgent】【OAuth 服务发现】资源文档缺失或无效时回退 MCP 源站，网络错误保留；显式授权文档按配置可信处理。</summary>
    /// <param name="server">MCP 地址。</param><param name="resourceMetadataUrl">资源文档覆盖。</param><param name="authorizationMetadataUrl">授权文档覆盖。</param>
    /// <param name="skipIssuerValidation">是否跳过发行方比较。</param><param name="token">取消。</param><returns>可持久化的发现状态。</returns>
    public async Task<JsonObject> DiscoverAsync(Uri server, Uri? resourceMetadataUrl = null, Uri? authorizationMetadataUrl = null, bool skipIssuerValidation = false, CancellationToken token = default)
    {
        JsonObject? resource = null;
        try { resource = await ProtectedResourceAsync(server, resourceMetadataUrl, token).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidDataException or JsonException) { }
        string issuer;
        JsonObject? metadata;
        if (authorizationMetadataUrl is not null)
        {
            using var response = await FetchAsync(authorizationMetadataUrl, token).ConfigureAwait(false);
            metadata = CodingAgentMcpOAuthJson.AuthorizationServer(await ReadAsync(response, "authorization server metadata", token).ConfigureAwait(false));
            issuer = CodingAgentMcpOAuthJson.Text(metadata, "issuer")!;
        }
        else
        {
            issuer = CodingAgentMcpOAuthJson.List(resource, "authorization_servers").FirstOrDefault() ?? AtOrigin(server, "/").AbsoluteUri;
            metadata = await AuthorizationServerAsync(issuer, skipIssuerValidation, token).ConfigureAwait(false);
        }
        var result = new JsonObject { ["authorizationServerUrl"] = issuer };
        if (metadata is not null) result["authorizationServerMetadata"] = metadata;
        if (resource is not null) result["resourceMetadata"] = resource;
        return result;
    }

    /// <summary>【CodingAgent】【OAuth 资源绑定】资源文档必须与 MCP 同源，且路径为 MCP 路径的完整父级，避免前缀相似路径混淆。</summary>
    /// <param name="server">MCP 地址。</param><param name="metadata">资源元数据。</param><returns>原资源 URI 或空值。</returns>
    public static string? SelectResource(Uri server, JsonObject? metadata)
    {
        if (metadata is null) return null;
        var value = CodingAgentMcpOAuthJson.Text(metadata, "resource")!;
        var resource = new Uri(value);
        if (server.GetLeftPart(UriPartial.Authority) != resource.GetLeftPart(UriPartial.Authority) ||
            !(Suffix(server.AbsolutePath) + "/").StartsWith(Suffix(resource.AbsolutePath) + "/", StringComparison.Ordinal))
            throw new InvalidDataException($"Protected resource {value} does not match MCP server {new UriBuilder(server) { Fragment = "" }.Uri}");
        return value;
    }

    /// <summary>【CodingAgent】【发现请求】所有文档请求带上 JSON 接受类型及 MCP 协议版本。</summary><param name="url">地址。</param><param name="token">取消。</param><returns>由调用方释放的响应。</returns>
    private async Task<HttpResponseMessage> FetchAsync(Uri url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("MCP-Protocol-Version", CodingAgentMcpClient.LatestProtocolVersion);
        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
    }
    /// <summary>【CodingAgent】【发现响应】HTTP 失败只输出状态，成功响应按 JSON 解析。</summary><param name="response">响应。</param><param name="kind">文档名称。</param><param name="token">取消。</param><returns>JSON 节点。</returns>
    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, string kind, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"HTTP {(int)response.StatusCode} loading {kind}");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }
    /// <summary>【CodingAgent】【发现缺失状态】4xx 和 502 允许尝试下一个候选文档。</summary><param name="status">状态码。</param><returns>是否缺失。</returns>
    private static bool Miss(int status) => status is >= 400 and < 500 or 502;
    /// <summary>【CodingAgent】【发现路径后缀】只删除一个末尾斜杠，与源实现比较规则一致。</summary><param name="value">路径或地址。</param><returns>规范后缀。</returns>
    private static string Suffix(string value) => value.EndsWith('/') ? value[..^1] : value;
    /// <summary>【CodingAgent】【源站地址】组合源站与绝对路径，不继承查询和片段。</summary><param name="server">源地址。</param><param name="path">绝对路径。</param><returns>请求 URI。</returns>
    private static Uri AtOrigin(Uri server, string path) => new(server.GetLeftPart(UriPartial.Authority) + path);
    /// <summary>【CodingAgent】【质询字段】读取双引号或未加引号参数，空值视为缺省。</summary><param name="header">完整请求头。</param><param name="name">固定协议字段名。</param><returns>参数或空值。</returns>
    private static string? Field(string header, string name)
    {
        var match = Regex.Match(header, "(?:^|[,\\s])" + Regex.Escape(name) + "=(?:\"([^\"]*)\"|([^\\s,]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return value.Length > 0 ? value : null;
    }
}
