// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【xAI OAuth】Grok/X 订阅设备码登录、轮询与刷新。</summary>
public sealed class XaiOAuthProvider : IOAuthProvider
{
    private const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const string Scope = "openid profile email offline_access grok-cli:access api:access";
    private const string DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code";
    private const string TokenUrl = "https://auth.x.ai/oauth2/token";
    private readonly OAuthHttpClient _http;
    private readonly OAuthFlowClock _clock;

    /// <summary>【AI】【xAI OAuth】创建设备码认证实现。</summary><param name="httpClient">可选网络客户端。</param>
    public XaiOAuthProvider(HttpClient? httpClient = null) : this(httpClient ?? TauHttpClientFactory.Create(), OAuthFlowClock.System) { }

    /// <summary>【AI】【xAI OAuth】注入网络和时钟以验证真实协议而不等待现实时间。</summary>
    /// <param name="httpClient">网络客户端。</param><param name="clock">可取消时钟。</param>
    internal XaiOAuthProvider(HttpClient httpClient, OAuthFlowClock clock)
    { _http = new(httpClient, Timeout.InfiniteTimeSpan); _clock = clock; }

    public string Id => "xai";
    public string Name => "xAI (Grok/X subscription)";
    public bool IsSubscription => true;
    public string LoginLabel => "Sign in with SuperGrok or X Premium";

    /// <summary>【AI】【xAI 登录】申请设备码、验证 HTTPS 地址、通知用户并按共享轮询规则等待。</summary>
    /// <param name="callbacks">登录交互。</param><param name="cancellationToken">流程取消信号。</param><returns>登录凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        var response = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        { ["client_id"] = ClientId, ["scope"] = Scope, ["referrer"] = "pi" }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) throw CreateRequestFailure("device authorization", response);
        var body = response.Body;
        // 1. 【AI】【xAI 设备码】完整验证地址如果存在也必须校验，缺省或无效间隔由轮询器采用五秒
        var complete = OAuthHttpClient.String(body, "verification_uri_complete");
        var completeUri = !string.IsNullOrEmpty(complete) ? ValidateVerificationUri(complete) : null;
        var device = RequiredString(body, "device_code");
        var user = RequiredString(body, "user_code");
        var uri = ValidateVerificationUri(RequiredString(body, "verification_uri"));
        var interval = OAuthHttpClient.PositiveNumber(body, "interval");
        var expires = PositiveNumber(body, "expires_in");
        callbacks.OnDeviceCode(new(user, completeUri ?? uri, interval, expires));
        cancellationToken.ThrowIfCancellationRequested();
        // 2. 【AI】【xAI 轮询】首次请求先等待，服务器的有效 slow_down 间隔直接替换当前间隔
        return await OAuthDeviceCodePoller.PollAsync(() => PollAsync(device, cancellationToken), cancellationToken,
            interval, expires, waitBeforeFirstPoll: true, _clock).ConfigureAwait(false);
    }

    /// <summary>【AI】【xAI 刷新】仅在响应省略 refresh_token 时复用旧令牌，显式损坏字段必须失败。</summary>
    /// <param name="credentials">当前凭据。</param><param name="cancellationToken">取消信号。</param><returns>新凭据。</returns>
    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var response = await PostFormAsync(TokenUrl, new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = credentials.Refresh }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) throw CreateRequestFailure("token refresh", response);
        return ParseTokenCredentials(response.Body, credentials.Refresh);
    }

    /// <summary>【AI】【xAI 认证】读取订阅访问令牌。</summary><param name="credentials">OAuth 凭据。</param><returns>访问令牌。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    /// <summary>【AI】【xAI 轮询】将协议响应映射到共享轮询状态，保留拒绝及失败原因。</summary>
    /// <param name="device">设备代码。</param><param name="token">取消信号。</param><returns>轮询状态或凭据。</returns>
    private async Task<OAuthDeviceCodePollResult<OAuthCredentials>> PollAsync(string device, CancellationToken token)
    {
        var response = await PostFormAsync(TokenUrl, new Dictionary<string, string>
        { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["client_id"] = ClientId, ["device_code"] = device }, token).ConfigureAwait(false);
        if (response.IsSuccess) return new("complete", ParseTokenCredentials(response.Body));
        return OAuthHttpClient.String(response.Body, "error") switch
        {
            "authorization_pending" => new("pending"),
            "slow_down" => new("slow_down", IntervalSeconds: OAuthHttpClient.PositiveNumber(response.Body, "interval")),
            "access_denied" or "authorization_denied" => new("failed", Message: "xAI device authorization was denied"),
            "expired_token" => new("failed", Message: "xAI device code expired"),
            _ => new("failed", Message: CreateRequestFailure("device token polling", response).Message)
        };
    }

    /// <summary>【AI】【xAI HTTP】发送表单并校验 JSON；取消即结束等待，正文读取和网络错误保持可辨别。</summary>
    /// <param name="url">端点。</param><param name="fields">表单字段。</param><param name="token">取消信号。</param><returns>响应快照。</returns>
    private async Task<OAuthHttpResponse> PostFormAsync(string url, IReadOnlyDictionary<string, string> fields, CancellationToken token)
    {
        try
        {
            var response = await _http.PostFormAsync(url, fields, token).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (response.Body.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException($"xAI OAuth returned invalid JSON (HTTP {response.Status})");
            return response;
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        { throw new OperationCanceledException("Login cancelled", error, token); }
    }

    /// <summary>【AI】【xAI 凭据】校验必需字段，保留小数秒并提前五分钟刷新，极大期限保留原生毫秒。</summary>
    /// <param name="body">令牌响应。</param><param name="previousRefreshToken">刷新时可复用的旧值。</param><returns>凭据。</returns>
    private OAuthCredentials ParseTokenCredentials(JsonElement body, string? previousRefreshToken = null)
    {
        var access = RequiredString(body, "access_token");
        var refresh = !body.TryGetProperty("refresh_token", out _) && !string.IsNullOrEmpty(previousRefreshToken)
            ? previousRefreshToken : RequiredString(body, "refresh_token");
        var expires = body.TryGetProperty("expires_in", out _) ? PositiveNumber(body, "expires_in") : 3600;
        var rawMilliseconds = _clock.NowMilliseconds() + expires * 1000 - 300000;
        var milliseconds = rawMilliseconds >= long.MaxValue ? long.MaxValue : rawMilliseconds <= long.MinValue ? long.MinValue : (long)rawMilliseconds;
        return new() { Access = access, Refresh = refresh, ExpiresUnixTimeMilliseconds = milliseconds, ExpiresAt = OAuthCredentialJson.ClampExpiry(milliseconds) };
    }

    /// <summary>【AI】【xAI 错误】拼接服务端字符串错误字段，不把整个响应写入错误。</summary>
    /// <param name="action">失败操作。</param><param name="response">HTTP 响应。</param><returns>可展示错误。</returns>
    private static InvalidOperationException CreateRequestFailure(string action, OAuthHttpResponse response)
    {
        var detail = string.Join(": ", new[] { OAuthHttpClient.String(response.Body, "error"), OAuthHttpClient.String(response.Body, "error_description") }.Where(value => !string.IsNullOrEmpty(value)));
        return new($"xAI OAuth {action} failed (HTTP {response.Status}){(detail.Length > 0 ? ": " + detail : "")}");
    }

    /// <summary>【AI】【xAI 字段】只接受非空字符串，空白字符串按原协议保留。</summary>
    /// <param name="body">响应。</param><param name="field">字段名称。</param><returns>原始字符串。</returns>
    private static string RequiredString(JsonElement body, string field) => OAuthHttpClient.String(body, field) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Invalid xAI OAuth response field: {field}");

    /// <summary>【AI】【xAI 字段】只接受有限正数，显式 null、字符串和非正数不使用缺省值。</summary>
    /// <param name="body">响应。</param><param name="field">字段名称。</param><returns>数值。</returns>
    private static double PositiveNumber(JsonElement body, string field) => OAuthHttpClient.PositiveNumber(body, field)
        ?? throw new InvalidOperationException($"Invalid xAI OAuth response field: {field}");

    /// <summary>【AI】【xAI 验证地址】仅允许 HTTPS 浏览器验证链接并规范化地址。</summary>
    /// <param name="value">服务端地址。</param><returns>规范地址。</returns>
    private static string ValidateVerificationUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        ? uri.AbsoluteUri : throw new InvalidOperationException("Untrusted verification URI in xAI OAuth response");
}
