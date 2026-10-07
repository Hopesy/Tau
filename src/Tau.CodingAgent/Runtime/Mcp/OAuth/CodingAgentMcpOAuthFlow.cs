// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【OAuth 流程结果】区分已获得令牌与已发起用户授权。</summary>
public enum CodingAgentMcpOAuthFlowResult { Authorized, Redirect }
/// <summary>【CodingAgent】【OAuth 流程参数】绑定服务器、发现覆盖、授权响应和权限升级要求。</summary>
public sealed record CodingAgentMcpOAuthFlowOptions
{
    public required Uri ServerUrl { get; init; }
    public string? AuthorizationCode { get; init; }
    public string? Issuer { get; init; }
    public string? Scope { get; init; }
    public Uri? ResourceMetadataUrl { get; init; }
    public Uri? AuthorizationServerMetadataUrl { get; init; }
    public bool SkipIssuerValidation { get; init; }
    public bool SkipRefresh { get; init; }
}

public sealed partial class CodingAgentMcpOAuthFlow
{
    /// <summary>【CodingAgent】【OAuth 授权入口】首次遇到失效客户端或授权时精确清理并重试一次，防止无限登录循环。</summary>
    /// <param name="provider">状态提供方。</param><param name="options">流程参数。</param><param name="token">取消。</param><returns>令牌就绪或待用户授权。</returns>
    public async Task<CodingAgentMcpOAuthFlowResult> AuthorizeAsync(CodingAgentMcpOAuthProvider provider, CodingAgentMcpOAuthFlowOptions options, CancellationToken token = default)
    {
        try { return await RunAsync(provider, options, token).ConfigureAwait(false); }
        catch (CodingAgentMcpOAuthException error) when (error.Code is "invalid_client" or "unauthorized_client" or "invalid_grant")
        {
            var kind = error.Code == "invalid_grant" ? CodingAgentMcpOAuthInvalidation.Tokens : CodingAgentMcpOAuthInvalidation.All;
            await provider.InvalidateCredentialsAsync(kind, token).ConfigureAwait(false);
            return await RunAsync(provider, options, token).ConfigureAwait(false);
        }
    }

    /// <summary>【CodingAgent】【OAuth 权限升级】合并原授权与新增权限并按首次出现顺序去重；无质询时保留默认权限选择。</summary><param name="granted">已授予权限。</param><param name="challenged">新增权限。</param><returns>合并权限或空值。</returns>
    public static string? StepUpScope(string? granted, string? challenged) => string.IsNullOrEmpty(challenged) ? null :
        string.Join(" ", SplitScope(granted).Concat(SplitScope(challenged)).Distinct(StringComparer.Ordinal));

    /// <summary>【CodingAgent】【OAuth 完整流程】依次发现服务器、选择客户端、交换或刷新令牌，最后才请求显式授权跳转。</summary>
    /// <param name="provider">状态与回调。</param><param name="options">流程参数。</param><param name="token">取消。</param><returns>流程结果。</returns>
    private async Task<CodingAgentMcpOAuthFlowResult> RunAsync(CodingAgentMcpOAuthProvider provider, CodingAgentMcpOAuthFlowOptions options, CancellationToken token)
    {
        // 1. 【MCP】【OAuth 发现】显式元数据覆盖不读写旧发现缓存
        var metadataUrl = options.AuthorizationServerMetadataUrl is { } configuredUrl ? SecureEndpoint(configuredUrl) : null;
        var discovery = new CodingAgentMcpOAuthDiscovery(http);
        var cached = metadataUrl is null ? await provider.DiscoveryStateAsync(token).ConfigureAwait(false) : null;
        JsonObject discovered;
        if (CodingAgentMcpOAuthJson.Text(cached, "authorizationServerUrl") is { Length: > 0 } cachedIssuer)
        {
            discovered = cached!;
            discovered["authorizationServerMetadata"] ??= await discovery.AuthorizationServerAsync(cachedIssuer, options.SkipIssuerValidation, token).ConfigureAwait(false);
        }
        else discovered = await discovery.DiscoverAsync(options.ServerUrl, options.ResourceMetadataUrl, metadataUrl, options.SkipIssuerValidation, token).ConfigureAwait(false);
        if (metadataUrl is null)
        {
            if (options.ResourceMetadataUrl is { } resourceUrl) discovered["resourceMetadataUrl"] = resourceUrl.AbsoluteUri;
            await provider.SaveDiscoveryStateAsync(discovered, token).ConfigureAwait(false);
        }
        var issuer = new Uri(CodingAgentMcpOAuthJson.Text(discovered, "authorizationServerUrl")!);
        var metadata = discovered["authorizationServerMetadata"] as JsonObject;
        var resourceMetadata = discovered["resourceMetadata"] as JsonObject;
        var resource = CodingAgentMcpOAuthDiscovery.SelectResource(options.ServerUrl, resourceMetadata);
        var scope = NonEmpty(options.Scope) ?? NonEmpty(string.Join(" ", CodingAgentMcpOAuthJson.List(resourceMetadata, "scopes_supported"))) ?? CodingAgentMcpOAuthJson.Text(provider.ClientMetadata, "scope");

        // 2. 【MCP】【OAuth 客户端】显式或存储客户端优先，其次客户端文档，最后动态注册
        var client = await provider.ClientInformationAsync(token).ConfigureAwait(false);
        var document = client is null ? provider.ClientMetadataDocument?.Invoke(metadata?.DeepClone().AsObject()) : null;
        if (document is not null)
        {
            var documentUrl = new Uri(document.Url);
            if (documentUrl.Scheme != "https" || documentUrl.AbsolutePath == "/") throw new InvalidDataException("Invalid OAuth client metadata URL");
            client = new() { ["client_id"] = document.Url };
        }
        if (client is null)
        {
            if (!string.IsNullOrEmpty(options.AuthorizationCode)) throw new InvalidOperationException("OAuth client information is missing during code exchange");
            client = await RegisterClientAsync(issuer, metadata, provider.ClientMetadata, scope, token).ConfigureAwait(false);
            await provider.SaveClientInformationAsync(client, token).ConfigureAwait(false);
        }
        var redirect = document?.RedirectUrl ?? provider.RedirectUrl;
        var tokenOptions = new CodingAgentMcpOAuthTokenOptions { Metadata = metadata, ClientInformation = client, Resource = resource, AddClientAuthentication = provider.AddClientAuthentication };

        // 3. 【MCP】【OAuth 令牌】授权响应必须在读取验证器和发送授权码之前校验发行方
        if (!string.IsNullOrEmpty(options.AuthorizationCode))
        {
            if (metadata is not null && (options.Issuer is not null || CodingAgentMcpOAuthJson.Flag(metadata, "authorization_response_iss_parameter_supported")))
            {
                var expected = CodingAgentMcpOAuthJson.Text(metadata, "issuer")!;
                if (options.Issuer != expected) throw new CodingAgentMcpOAuthIssuerException(expected, options.Issuer);
            }
            var verifier = await provider.CodeVerifierAsync(token).ConfigureAwait(false);
            var tokens = await ExchangeCodeAsync(issuer, tokenOptions, options.AuthorizationCode, verifier, redirect, token).ConfigureAwait(false);
            await provider.SaveTokensAsync(WithScope(tokens, scope), token).ConfigureAwait(false);
            return CodingAgentMcpOAuthFlowResult.Authorized;
        }
        var existing = options.SkipRefresh ? null : await provider.TokensAsync(token).ConfigureAwait(false);
        if (CodingAgentMcpOAuthJson.Text(existing, "refresh_token") is { Length: > 0 } refreshToken)
        {
            try
            {
                var tokens = await RefreshAsync(issuer, tokenOptions, refreshToken, token).ConfigureAwait(false);
                await provider.SaveTokensAsync(WithScope(tokens, CodingAgentMcpOAuthJson.Text(existing, "scope")), token).ConfigureAwait(false);
                return CodingAgentMcpOAuthFlowResult.Authorized;
            }
            catch (CodingAgentMcpOAuthInsecureEndpointException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (CodingAgentMcpOAuthException error) when (error.Code != "server_error") { throw; }
            catch (Exception error) when (error is HttpRequestException or CodingAgentMcpOAuthException or InvalidDataException or System.Text.Json.JsonException) { }
        }

        // 4. 【MCP】【OAuth 显式跳转】先保存 PKCE，再通知宿主展示授权地址
        var authorization = StartAuthorization(issuer, tokenOptions, redirect, scope, await provider.StateAsync(token).ConfigureAwait(false));
        await provider.SaveCodeVerifierAsync(authorization.CodeVerifier, token).ConfigureAwait(false);
        await provider.RedirectToAuthorizationAsync(authorization.AuthorizationUrl, token).ConfigureAwait(false);
        return CodingAgentMcpOAuthFlowResult.Redirect;
    }
    /// <summary>【CodingAgent】【OAuth 权限保留】响应未报告权限时使用本次请求或原授权权限。</summary><param name="tokens">令牌对象。</param><param name="scope">待补齐权限。</param><returns>原令牌对象。</returns>
    private static JsonObject WithScope(JsonObject tokens, string? scope)
    {
        if (tokens["scope"] is null && !string.IsNullOrEmpty(scope)) tokens["scope"] = scope;
        return tokens;
    }
    /// <summary>【CodingAgent】【OAuth 默认选择】空文本不覆盖下一项默认值。</summary><param name="value">可选文本。</param><returns>非空文本或空值。</returns>
    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
