// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【MCP OAuth 状态范围】按认证失败类型清除需要重新获取的状态。</summary>
public enum CodingAgentMcpOAuthInvalidation { All, Client, Tokens, Verifier, Discovery }

/// <summary>【CodingAgent】【MCP OAuth 提供方参数】组合服务器身份、客户端元数据、存储与显式授权跳转回调。</summary>
public sealed record CodingAgentMcpOAuthProviderOptions
{
    public required string ServerUrl { get; init; }
    public required string RedirectUrl { get; init; }
    public JsonObject ClientMetadata { get; init; } = new();
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public ICodingAgentMcpOAuthStateStore? Store { get; init; }
    public Func<Uri, CancellationToken, Task>? OnRedirect { get; init; }
    public Func<JsonObject?, CodingAgentMcpOAuthClientDocument?>? ClientMetadataDocument { get; init; }
    public CodingAgentMcpOAuthClientAuthentication? AddClientAuthentication { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>【CodingAgent】【MCP OAuth 状态提供方】隔离确切服务器 URL 的客户端、令牌及 PKCE 状态，所有更新串行持久化。</summary>
public sealed class CodingAgentMcpOAuthProvider
{
    private readonly string _serverUrl;
    private readonly JsonObject? _configuredClient;
    private readonly JsonObject _clientMetadata;
    private readonly ICodingAgentMcpOAuthStateStore _store;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Func<Uri, CancellationToken, Task>? _redirect;
    private readonly TimeProvider _time;
    public string RedirectUrl { get; }
    public JsonObject ClientMetadata => _clientMetadata.DeepClone().AsObject();
    public Func<JsonObject?, CodingAgentMcpOAuthClientDocument?>? ClientMetadataDocument { get; }
    public CodingAgentMcpOAuthClientAuthentication? AddClientAuthentication { get; }

    /// <summary>【CodingAgent】【OAuth 提供方创建】补齐标准授权码和刷新配置，显式客户端信息优先于存储。</summary>
    /// <param name="options">服务器、元数据和宿主回调。</param>
    public CodingAgentMcpOAuthProvider(CodingAgentMcpOAuthProviderOptions options)
    {
        _serverUrl = CodingAgentMcpOAuthCredentialStore.NormalizeUrl(options.ServerUrl);
        ClientMetadataDocument = options.ClientMetadataDocument; AddClientAuthentication = options.AddClientAuthentication;
        RedirectUrl = options.RedirectUrl; _store = options.Store ?? new CodingAgentMcpOAuthMemoryStore(); _redirect = options.OnRedirect; _time = options.TimeProvider;
        _clientMetadata = options.ClientMetadata.DeepClone().AsObject();
        _clientMetadata["redirect_uris"] ??= new JsonArray(RedirectUrl);
        _clientMetadata["grant_types"] ??= new JsonArray("authorization_code", "refresh_token");
        _clientMetadata["response_types"] ??= new JsonArray("code");
        _clientMetadata["token_endpoint_auth_method"] ??= string.IsNullOrEmpty(options.ClientSecret) ? "none" : "client_secret_post";
        if (!string.IsNullOrEmpty(options.ClientId))
        {
            _configuredClient = new() { ["client_id"] = options.ClientId };
            if (!string.IsNullOrEmpty(options.ClientSecret)) _configuredClient["client_secret"] = options.ClientSecret;
        }
    }

    /// <summary>【CodingAgent】【OAuth state】复用当前登录流程状态，首次生成 32 字节随机值并保存后返回。</summary>
    /// <param name="token">取消。</param><returns>用于授权响应校验的 state。</returns>
    public async Task<string> StateAsync(CancellationToken token = default)
    {
        string? state = null;
        await UpdateAsync(value =>
        {
            state = String(value, "oauthState");
            if (string.IsNullOrEmpty(state)) value["oauthState"] = state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }, token).ConfigureAwait(false);
        return state!;
    }

    /// <summary>【CodingAgent】【OAuth 客户端读取】显式配置不依赖旧存储，并返回副本。</summary><param name="token">取消。</param><returns>客户端信息或空值。</returns>
    public async Task<JsonObject?> ClientInformationAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return _configuredClient?.DeepClone().AsObject() ?? (await LoadAsync(token).ConfigureAwait(false))["clientInformation"]?.DeepClone() as JsonObject; }
    /// <summary>【CodingAgent】【OAuth 客户端保存】仅持久化动态注册信息，显式配置的客户端无需写入。</summary><param name="information">注册结果。</param><param name="token">取消。</param><returns>写入任务。</returns>
    public Task SaveClientInformationAsync(JsonObject information, CancellationToken token = default) =>
        _configuredClient is not null ? Task.CompletedTask : UpdateAsync(value => value["clientInformation"] = information.DeepClone(), token);
    /// <summary>【CodingAgent】【OAuth 令牌读取】取得独立令牌副本，不触发网络刷新。</summary><param name="token">取消。</param><returns>令牌或空值。</returns>
    public async Task<JsonObject?> TokensAsync(CancellationToken token = default) => (await LoadAsync(token).ConfigureAwait(false))["tokens"]?.DeepClone() as JsonObject;

    /// <summary>【CodingAgent】【OAuth 令牌保存】按保存时刻计算绝对过期时间，无 expires_in 时移除旧过期时间。</summary>
    /// <param name="tokens">已验证令牌对象。</param><param name="token">取消。</param><returns>写入任务。</returns>
    public Task SaveTokensAsync(JsonObject tokens, CancellationToken token = default)
    {
        double? expires = tokens["expires_in"]?.GetValueKind() == JsonValueKind.Number ? double.Parse(tokens["expires_in"]!.ToJsonString(), CultureInfo.InvariantCulture) : null;
        return UpdateAsync(state =>
        {
            state["tokens"] = tokens.DeepClone();
            if (expires is { } duration)
            {
                var absolute = _time.GetUtcNow().ToUnixTimeMilliseconds() + duration * 1000;
                state["tokensExpireAt"] = double.IsFinite(absolute) ? JsonValue.Create(absolute) : null;
            }
            else state.Remove("tokensExpireAt");
        }, token);
    }

    /// <summary>【CodingAgent】【OAuth 授权跳转】仅执行宿主显式提供的回调，提供方本身不启动浏览器。</summary><param name="url">授权 URL。</param><param name="token">取消。</param><returns>宿主回调任务。</returns>
    public Task RedirectToAuthorizationAsync(Uri url, CancellationToken token = default) =>
        (_redirect ?? throw new InvalidOperationException("No MCP OAuth authorization redirect handler is configured."))(url, token);
    /// <summary>【CodingAgent】【PKCE 保存】持久化授权码交换需要的验证器。</summary><param name="verifier">随机验证器。</param><param name="token">取消。</param><returns>写入任务。</returns>
    public Task SaveCodeVerifierAsync(string verifier, CancellationToken token = default) => UpdateAsync(value => value["codeVerifier"] = verifier, token);
    /// <summary>【CodingAgent】【PKCE 读取】缺少验证器时明确失败，禁止继续授权码交换。</summary><param name="token">取消。</param><returns>验证器。</returns>
    public async Task<string> CodeVerifierAsync(CancellationToken token = default) => String(await LoadAsync(token).ConfigureAwait(false), "codeVerifier") is { Length: > 0 } value
        ? value : throw new InvalidOperationException("No OAuth PKCE code verifier is stored");
    /// <summary>【CodingAgent】【OAuth 发现保存】持久化已验证的授权服务器与资源元数据。</summary><param name="discovery">发现状态。</param><param name="token">取消。</param><returns>写入任务。</returns>
    public Task SaveDiscoveryStateAsync(JsonObject discovery, CancellationToken token = default) => UpdateAsync(value => value["discovery"] = discovery.DeepClone(), token);
    /// <summary>【CodingAgent】【OAuth 发现读取】返回当前服务器的发现副本。</summary><param name="token">取消。</param><returns>发现状态或空值。</returns>
    public async Task<JsonObject?> DiscoveryStateAsync(CancellationToken token = default) => (await LoadAsync(token).ConfigureAwait(false))["discovery"]?.DeepClone() as JsonObject;

    /// <summary>【CodingAgent】【OAuth 失效】精确移除失效字段，只有完整失效才删除授权 state。</summary>
    /// <param name="kind">失效范围。</param><param name="token">取消。</param><returns>写入任务。</returns>
    public Task InvalidateCredentialsAsync(CodingAgentMcpOAuthInvalidation kind, CancellationToken token = default) => UpdateAsync(value =>
    {
        if (kind is CodingAgentMcpOAuthInvalidation.All or CodingAgentMcpOAuthInvalidation.Client) value.Remove("clientInformation");
        if (kind is CodingAgentMcpOAuthInvalidation.All or CodingAgentMcpOAuthInvalidation.Tokens) { value.Remove("tokens"); value.Remove("tokensExpireAt"); }
        if (kind is CodingAgentMcpOAuthInvalidation.All or CodingAgentMcpOAuthInvalidation.Verifier) value.Remove("codeVerifier");
        if (kind is CodingAgentMcpOAuthInvalidation.All or CodingAgentMcpOAuthInvalidation.Discovery) value.Remove("discovery");
        if (kind == CodingAgentMcpOAuthInvalidation.All) value.Remove("oauthState");
    }, token);

    /// <summary>【CodingAgent】【OAuth 串行读取】等待之前写入完成后读取，忽略其他服务器 URL 的凭据。</summary><param name="token">取消。</param><returns>本服务器状态。</returns>
    private async Task<JsonObject> LoadAsync(CancellationToken token)
    {
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try { return Own(await _store.LoadAsync(token).ConfigureAwait(false)); }
        finally { _writes.Release(); }
    }
    /// <summary>【CodingAgent】【OAuth 串行修改】每次重新读取状态并合并一个字段更新，避免并发写入互相覆盖。</summary><param name="update">修改操作。</param><param name="token">取消。</param><returns>提交任务。</returns>
    private async Task UpdateAsync(Action<JsonObject> update, CancellationToken token)
    {
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try { var value = Own(await _store.LoadAsync(token).ConfigureAwait(false)); update(value); await _store.SaveAsync(value, token).ConfigureAwait(false); }
        finally { _writes.Release(); }
    }
    /// <summary>【CodingAgent】【服务器身份校验】只有状态中完全相同的规范 URL 才允许读取其凭据。</summary><param name="state">存储状态。</param><returns>独立的本服务器状态。</returns>
    private JsonObject Own(JsonObject? state) => state is not null && String(state, "serverUrl") == _serverUrl ? state.DeepClone().AsObject() : new() { ["serverUrl"] = _serverUrl };
    /// <summary>【CodingAgent】【OAuth 字符串】读取可选字符串，损坏类型视为缺失。</summary><param name="value">对象。</param><param name="key">字段。</param><returns>字符串或空值。</returns>
    private static string? String(JsonObject value, string key) => value[key] is JsonValue field && field.TryGetValue<string>(out var text) ? text : null;
}
