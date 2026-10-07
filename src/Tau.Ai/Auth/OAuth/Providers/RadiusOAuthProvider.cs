// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers.PiMessages;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【Radius 登录】面向指定网关的浏览器 PKCE 和设备码登录，模型目录由网关提供方独立管理。</summary>
public sealed class RadiusOAuthProvider : IOAuthProvider
{
    private const string ClientId = "pi-gateway";
    private const string Scope = "gateway offline_access";
    private readonly string _gateway;
    private readonly OAuthHttpClient _http;
    private readonly OAuthFlowClock _clock;
    private readonly int _callbackPort;

    /// <summary>【AI】【Radius 登录】创建网关认证，默认使用 Radius 公共网关。</summary>
    /// <param name="gateway">网关地址。</param><param name="name">显示名。</param><param name="id">提供方标识。</param><param name="httpClient">可选客户端。</param>
    public RadiusOAuthProvider(string? gateway = null, string name = "Radius", string id = "radius", HttpClient? httpClient = null)
        : this(gateway ?? RadiusGatewayConfigLoader.DefaultGateway, name, id, httpClient ?? TauHttpClientFactory.Create(), OAuthFlowClock.System, 1456) { }

    /// <summary>【AI】【Radius 登录】注入网关、时钟与回调端口以隔离测试。</summary>
    /// <param name="gateway">网关。</param><param name="name">显示名。</param><param name="id">标识。</param><param name="client">客户端。</param><param name="clock">时钟。</param><param name="callbackPort">回调端口。</param>
    internal RadiusOAuthProvider(string gateway, string name, string id, HttpClient client, OAuthFlowClock clock, int callbackPort)
    {
        _gateway = RadiusGatewayConfigLoader.NormalizeGatewayUrl(gateway); Name = name; Id = id;
        _http = new(client, Timeout.InfiniteTimeSpan); _clock = clock; _callbackPort = callbackPort;
    }

    public string Id { get; }
    public string Name { get; }
    public bool UsesCallbackServer => true;

    /// <summary>【AI】【Radius 登录】让用户选择浏览器或跨设备授权，只在浏览器模式读取授权发现信息。</summary>
    /// <param name="callbacks">交互。</param><param name="cancellationToken">取消信号。</param><returns>网关凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        cancellationToken.ThrowIfCancellationRequested();
        var method = await callbacks.OnSelectAsync($"Sign in to {Name}:",
            [new("browser", "Sign in with browser (recommended)"), new("device-code", "Sign in with device code (when signing in from another device)")]).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return method switch
        {
            "browser" => await LoginBrowserAsync(callbacks, cancellationToken).ConfigureAwait(false),
            "device-code" => await LoginDeviceAsync(callbacks, cancellationToken).ConfigureAwait(false),
            null => throw new OperationCanceledException("Login cancelled"),
            _ => throw new InvalidOperationException($"Unknown {Name} sign-in method: {method}")
        };
    }

    /// <summary>【AI】【Radius 刷新】向同一网关提交刷新令牌并保留返回的 scope。</summary>
    /// <param name="credentials">旧凭据。</param><param name="cancellationToken">取消信号。</param><returns>新凭据。</returns>
    public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) => RequestTokenAsync(
        new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = credentials.Refresh }, cancellationToken);

    /// <summary>【AI】【Radius 认证】返回网关访问令牌。</summary><param name="credentials">凭据。</param><returns>访问令牌。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    /// <summary>【AI】【Radius 浏览器】发现授权地址，绑定固定回调路径并以 PKCE 与 state 验证登录。</summary>
    /// <param name="callbacks">交互。</param><param name="token">取消信号。</param><returns>兑换后的凭据。</returns>
    private async Task<OAuthCredentials> LoginBrowserAsync(IOAuthLoginCallbacks callbacks, CancellationToken token)
    {
        // 1. 【AI】【Radius 浏览器】OAuth API 留在网关，仅浏览器授权入口由发现响应指定
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("/v1/oauth"));
        var discovery = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (!discovery.IsSuccess) throw new InvalidOperationException($"Could not load Radius OAuth config from {_gateway}: {discovery.Status} {discovery.Text}");
        var endpoint = OAuthHttpClient.String(discovery.Body, "authorizationEndpoint");
        if (endpoint is null) throw new InvalidOperationException($"Invalid Radius OAuth config from {_gateway}");
        var (verifier, challenge) = OAuthPkce.Generate();
        var state = Guid.NewGuid().ToString();
        string redirect = "";
        await using var callback = OAuthLoopbackServer<OAuthCredentials>.Listen("Radius", "127.0.0.1", _callbackPort, "/oauth/callback",
            (code, signal) => RequestTokenAsync(new Dictionary<string, string>
            { ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["redirect_uri"] = redirect, ["code"] = code, ["code_verifier"] = verifier }, signal),
            token, Timeout.InfiniteTimeSpan, state);
        redirect = callback.RedirectUri;
        var url = new UriBuilder(endpoint) { Query = string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = ClientId, ["redirect_uri"] = redirect, ["scope"] = Scope,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["handoff"] = "url", ["state"] = state
        }.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))) };
        callbacks.OnProgress($"Listening for OAuth callback on {redirect}");
        callbacks.OnAuth(url.Uri.AbsoluteUri, "Continue in your browser.");
        // 2. 【AI】【Radius 浏览器】共享回调在密钥兑换完成后才向浏览器展示成功
        return await callback.WaitAsync().ConfigureAwait(false) ?? throw new InvalidOperationException("OAuth callback did not complete.");
    }

    /// <summary>【AI】【Radius 设备码】申请网关设备码并立即开始轮询，支持等待与限速状态。</summary>
    /// <param name="callbacks">交互。</param><param name="token">取消信号。</param><returns>凭据。</returns>
    private async Task<OAuthCredentials> LoginDeviceAsync(IOAuthLoginCallbacks callbacks, CancellationToken token)
    {
        var response = await _http.PostFormAsync(Endpoint("/v1/oauth/device"), new Dictionary<string, string> { ["client_id"] = ClientId, ["scope"] = Scope }, token).ConfigureAwait(false);
        if (!response.IsSuccess) throw ResponseError(response, "Radius OAuth device authorization failed");
        var device = OAuthHttpClient.String(response.Body, "device_code");
        var user = OAuthHttpClient.String(response.Body, "user_code");
        var uri = OAuthHttpClient.String(response.Body, "verification_uri");
        var expires = OAuthHttpClient.PositiveNumber(response.Body, "expires_in");
        var interval = OAuthHttpClient.PositiveNumber(response.Body, "interval");
        if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(uri) || expires is null)
            throw new InvalidOperationException("Radius OAuth device authorization response is missing required fields");
        callbacks.OnDeviceCode(new OAuthDeviceCodeNotification(user, uri, interval, expires));
        return await OAuthDeviceCodePoller.PollAsync(() => PollDeviceAsync(device, token), token, interval, expires, clock: _clock).ConfigureAwait(false);
    }

    /// <summary>【AI】【Radius 轮询】只解释设备码状态，其他网关错误保留原诊断。</summary>
    /// <param name="device">设备代码。</param><param name="token">取消信号。</param><returns>轮询结果。</returns>
    private async Task<OAuthDeviceCodePollResult<OAuthCredentials>> PollDeviceAsync(string device, CancellationToken token)
    {
        try
        {
            return new("complete", await RequestTokenAsync(new Dictionary<string, string>
            { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["client_id"] = ClientId, ["device_code"] = device }, token).ConfigureAwait(false));
        }
        catch (RadiusResponseException error)
        {
            switch (error.OAuthError)
            {
                case "authorization_pending": return new("pending");
                case "slow_down": return new("slow_down");
                case "expired_token": return new("failed", Message: "Device authorization expired.");
                case "access_denied": return new("failed", Message: "Device authorization was denied.");
                default: throw;
            }
        }
    }

    /// <summary>【AI】【Radius 令牌】读取访问、刷新和范围，期限提前一分钟以避免边界请求过期。</summary>
    /// <param name="fields">授权表单。</param><param name="token">取消信号。</param><returns>凭据。</returns>
    private async Task<OAuthCredentials> RequestTokenAsync(IReadOnlyDictionary<string, string> fields, CancellationToken token)
    {
        var response = await _http.PostFormAsync(Endpoint("/v1/oauth/token"), fields, token).ConfigureAwait(false);
        if (!response.IsSuccess) throw ResponseError(response, "Radius OAuth token request failed");
        var access = OAuthHttpClient.String(response.Body, "access_token");
        var refresh = OAuthHttpClient.String(response.Body, "refresh_token");
        if (access is null || refresh is null || response.Body.ValueKind != JsonValueKind.Object || !response.Body.TryGetProperty("expires_in", out var expiry)
            || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetDouble(out var seconds) || !double.IsFinite(seconds))
            throw new InvalidOperationException("Invalid Radius OAuth token response");
        var total = _clock.NowMilliseconds() + seconds * 1000 - 60000;
        var milliseconds = total >= long.MaxValue ? long.MaxValue : total <= long.MinValue ? long.MinValue : (long)total;
        return new OAuthCredentials { Access = access, Refresh = refresh, ExpiresAt = OAuthCredentialJson.ClampExpiry(milliseconds), ExpiresUnixTimeMilliseconds = milliseconds,
            Metadata = OAuthHttpClient.String(response.Body, "scope") is { } scope ? new Dictionary<string, string> { ["scope"] = scope } : new Dictionary<string, string>() };
    }

    /// <summary>【AI】【Radius 错误】保留 OAuth 错误代码以驱动轮询，正文不是 JSON 时作为详情。</summary>
    /// <param name="response">响应。</param><param name="message">操作说明。</param><returns>包含 OAuth 状态的错误。</returns>
    private static RadiusResponseException ResponseError(OAuthHttpResponse response, string message)
    {
        var error = OAuthHttpClient.String(response.Body, "error");
        var description = OAuthHttpClient.String(response.Body, "error_description") ?? (response.Body.ValueKind == JsonValueKind.Undefined ? response.Text : null);
        var detail = !string.IsNullOrEmpty(error) ? error + (string.IsNullOrEmpty(description) ? "" : ": " + description)
            : string.IsNullOrEmpty(description) ? response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : description;
        return new(message + ": " + detail, error);
    }

    /// <summary>【AI】【Radius 地址】OAuth 路径始终相对网关 origin，忽略配置地址的子路径。</summary><param name="path">绝对路径。</param><returns>完整地址。</returns>
    private string Endpoint(string path) => new Uri(new Uri(_gateway), path).AbsoluteUri;

    /// <summary>【AI】【Radius 错误】保存可供设备轮询判断的协议错误。</summary>
    /// <param name="message">诊断。</param><param name="oauthError">协议错误代码。</param>
    private sealed class RadiusResponseException(string message, string? oauthError) : InvalidOperationException(message)
    { internal string? OAuthError { get; } = oauthError; }
}
