// 作者：xxx
using System.Runtime.ExceptionServices;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【Kimi Code 登录】设备码订阅授权与支持短暂失败重试的令牌刷新。</summary>
public sealed class KimiCodingOAuthProvider : IOAuthProvider
{
    private const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
    private readonly OAuthHttpClient _http;
    private readonly OAuthFlowClock _clock;
    private readonly string? _host;

    /// <summary>【AI】【Kimi Code 登录】创建默认三十秒请求期限的认证实现。</summary><param name="httpClient">可选客户端。</param>
    public KimiCodingOAuthProvider(HttpClient? httpClient = null) : this(httpClient ?? TauHttpClientFactory.Create(), OAuthFlowClock.System, TimeSpan.FromSeconds(30)) { }

    /// <summary>【AI】【Kimi Code 登录】注入网络、时钟、期限和测试主机。</summary>
    /// <param name="client">客户端。</param><param name="clock">时钟。</param><param name="timeout">请求期限。</param><param name="host">主机覆盖。</param>
    internal KimiCodingOAuthProvider(HttpClient client, OAuthFlowClock clock, TimeSpan timeout, string? host = null)
    { _http = new(client, timeout); _clock = clock; _host = host; }

    public string Id => "kimi-coding";
    public string Name => "Kimi Code (subscription)";
    public bool IsSubscription => true;
    public string LoginLabel => "Sign in with Kimi Code";

    /// <summary>【AI】【Kimi Code 登录】申请设备码并按服务端间隔等待，成功后返回访问与刷新令牌。</summary>
    /// <param name="callbacks">交互回调。</param><param name="cancellationToken">取消信号。</param><returns>登录凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        var host = GetHost();
        // 1. 【AI】【Kimi Code 登录】两个验证地址都必须是 HTTP 协议，展示包含设备码的完整地址
        var response = await _http.PostFormAsync(host + "/api/oauth/device_authorization", new Dictionary<string, string> { ["client_id"] = ClientId }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) throw new InvalidOperationException($"Kimi Code device authorization failed with status {response.Status}{(response.Text.Length > 0 ? ": " + response.Text : "")}");
        var device = OAuthHttpClient.String(response.Body, "device_code");
        var code = OAuthHttpClient.String(response.Body, "user_code");
        var uri = OAuthHttpClient.String(response.Body, "verification_uri");
        var completeUri = OAuthHttpClient.String(response.Body, "verification_uri_complete");
        if (device is null || code is null || OAuthHttpClient.TrustedHttpUrl(uri) is null || OAuthHttpClient.TrustedHttpUrl(completeUri) is null)
            throw new InvalidOperationException($"Invalid Kimi Code device authorization response: {response.JsonText}");
        var interval = OAuthHttpClient.PositiveNumber(response.Body, "interval") ?? 5;
        var expires = OAuthHttpClient.PositiveNumber(response.Body, "expires_in") ?? 900;
        callbacks.OnDeviceCode(new OAuthDeviceCodeNotification(code, completeUri!, interval, expires));
        // 2. 【AI】【Kimi Code 登录】先等待一个间隔，再开始令牌轮询
        return await OAuthDeviceCodePoller.PollAsync(() => PollAsync(host, device, cancellationToken), cancellationToken,
            interval, expires, true, _clock).ConfigureAwait(false);
    }

    /// <summary>【AI】【Kimi Code 刷新】网络、429 和 5xx 最多重试三次，拒绝或无效授权立即失败。</summary>
    /// <param name="credentials">已有刷新令牌。</param><param name="cancellationToken">取消信号。</param><returns>旋转后的凭据。</returns>
    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        var host = GetHost(); Exception? lastError = null;
        for (var attempt = 0; attempt <= 3; attempt++)
        {
            if (attempt > 0) await _clock.SleepAsync(1000 * Math.Pow(2, attempt - 1), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("Kimi Code token refresh aborted", cancellationToken);
            OAuthHttpResponse response;
            try
            {
                response = await _http.PostFormAsync(host + "/api/oauth/token", new Dictionary<string, string>
                { ["client_id"] = ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = credentials.Refresh }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
            { lastError = error; continue; }
            if (response.IsSuccess) return ParseToken(response, "refresh");
            if (response.Status is 401 or 403 || OAuthHttpClient.String(response.Body, "error") == "invalid_grant")
            {
                var description = OAuthHttpClient.String(response.Body, "error_description");
                throw new InvalidOperationException($"Kimi Code token refresh unauthorized (status {response.Status}){(description is null ? "" : ": " + description)}");
            }
            if ((response.Status == 429 || response.Status >= 500) && attempt < 3)
            { lastError = new InvalidOperationException($"Kimi Code token refresh failed with status {response.Status}"); continue; }
            throw new InvalidOperationException($"Kimi Code token refresh failed with status {response.Status}: {response.JsonText}");
        }
        ExceptionDispatchInfo.Capture(lastError ?? new InvalidOperationException("Kimi Code token refresh failed")).Throw();
        throw new InvalidOperationException("Kimi Code token refresh failed");
    }

    /// <summary>【AI】【Kimi Code 认证】兼容旧接口的访问令牌查询。</summary><param name="credentials">凭据。</param><returns>访问令牌。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    /// <summary>【AI】【Kimi Code 认证】订阅令牌只进入 Bearer 请求头，避免误发到 API key 请求头。</summary>
    /// <param name="credentials">凭据。</param><param name="token">取消信号。</param><returns>完整请求认证。</returns>
    public ProviderAuthResult ResolveAuth(OAuthCredentials credentials, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new(Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + credentials.Access });
    }

    /// <summary>【AI】【Kimi Code 轮询】按协议处理等待、限速及终止状态，服务端 5xx 不进行登录重试。</summary>
    /// <param name="host">认证主机。</param><param name="device">设备代码。</param><param name="token">取消信号。</param><returns>轮询结果。</returns>
    private async Task<OAuthDeviceCodePollResult<OAuthCredentials>> PollAsync(string host, string device, CancellationToken token)
    {
        var response = await _http.PostFormAsync(host + "/api/oauth/token", new Dictionary<string, string>
        { ["client_id"] = ClientId, ["device_code"] = device, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code" }, token).ConfigureAwait(false);
        if (response.Status >= 500) return new("failed", Message: $"Kimi Code device token request failed with status {response.Status}{(response.Text.Length > 0 ? ": " + response.Text : "")}");
        if (response.IsSuccess && OAuthHttpClient.String(response.Body, "access_token") is not null)
        {
            try { return new("complete", ParseToken(response, "poll")); }
            catch (InvalidOperationException error) { return new("failed", Message: error.Message); }
        }
        var code = OAuthHttpClient.String(response.Body, "error");
        var description = OAuthHttpClient.String(response.Body, "error_description");
        return code switch
        {
            "authorization_pending" => new("pending"),
            "slow_down" => new("slow_down", IntervalSeconds: OAuthHttpClient.PositiveNumber(response.Body, "interval")),
            "expired_token" => new("failed", Message: "Kimi Code device authorization expired. Please restart login."),
            "access_denied" => new("failed", Message: "Kimi Code login was denied."),
            _ => new("failed", Message: $"Kimi Code device token request failed (status {response.Status}){(code is null ? "" : ": " + code + (description is null ? "" : ": " + description))}")
        };
    }

    /// <summary>【AI】【Kimi Code 凭据】成功响应必须同时携带访问令牌、刷新令牌及有限正有效期。</summary>
    /// <param name="response">响应。</param><param name="operation">poll 或 refresh。</param><returns>新凭据。</returns>
    private OAuthCredentials ParseToken(OAuthHttpResponse response, string operation)
    {
        var access = OAuthHttpClient.String(response.Body, "access_token");
        var refresh = OAuthHttpClient.String(response.Body, "refresh_token");
        var expires = OAuthHttpClient.PositiveNumber(response.Body, "expires_in");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || expires is null)
            throw new InvalidOperationException($"Kimi Code token {operation} response missing fields: {response.JsonText}");
        var total = _clock.NowMilliseconds() + expires.Value * 1000;
        var milliseconds = total >= long.MaxValue ? long.MaxValue : (long)total;
        return new OAuthCredentials { Access = access, Refresh = refresh, ExpiresAt = OAuthCredentialJson.ClampExpiry(milliseconds), ExpiresUnixTimeMilliseconds = milliseconds };
    }

    /// <summary>【AI】【Kimi Code 主机】优先使用专用环境覆盖，兼容旧变量，移除尾部斜杠。</summary><returns>认证主机。</returns>
    private string GetHost()
    {
        var value = _host ?? Environment.GetEnvironmentVariable("KIMI_CODE_OAUTH_HOST");
        if (string.IsNullOrEmpty(value)) value = Environment.GetEnvironmentVariable("KIMI_OAUTH_HOST");
        return (string.IsNullOrEmpty(value) ? "https://auth.kimi.com" : value).TrimEnd('/');
    }

}
