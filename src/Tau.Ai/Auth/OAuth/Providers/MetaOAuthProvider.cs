// 作者：xxx
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【Meta 登录】先取得身份令牌，再铸造用于模型 API 的短期密钥。</summary>
public sealed class MetaOAuthProvider : IOAuthProvider
{
    private const string ClientId = "1031625952748946";
    private const string DeviceUrl = "https://auth.meta.com/oidc/device/authorization/";
    private const string TokenUrl = "https://auth.meta.com/oidc/device/token/";
    private const string MintUrl = "https://api.meta.ai/muse-code/key";
    private readonly OAuthHttpClient _http;
    private readonly OAuthFlowClock _clock;

    /// <summary>【AI】【Meta 登录】创建默认三十秒请求期限的认证实现。</summary><param name="httpClient">可选客户端。</param>
    public MetaOAuthProvider(HttpClient? httpClient = null) : this(httpClient ?? TauHttpClientFactory.Create(), OAuthFlowClock.System, TimeSpan.FromSeconds(30)) { }

    /// <summary>【AI】【Meta 登录】注入网络、时钟与请求期限以验证轮询行为。</summary>
    /// <param name="client">客户端。</param><param name="clock">时钟。</param><param name="timeout">请求期限。</param>
    internal MetaOAuthProvider(HttpClient client, OAuthFlowClock clock, TimeSpan timeout) { _http = new(client, timeout); _clock = clock; }

    public string Id => "meta";
    public string Name => "Meta (Muse subscription)";
    public bool IsSubscription => true;
    public string LoginLabel => "Sign in with Meta";

    /// <summary>【AI】【Meta 登录】申请设备码、等待授权，最后把身份令牌兑换成模型密钥。</summary>
    /// <param name="callbacks">登录交互。</param><param name="cancellationToken">取消信号。</param><returns>身份令牌与 API key 凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        try
        {
            // 1. 【AI】【Meta 登录】只向浏览器展示可信协议的验证地址，完整地址失效时回退普通地址
            var response = await _http.PostFormAsync(DeviceUrl, new Dictionary<string, string> { ["client_id"] = ClientId }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess) throw new InvalidOperationException($"Meta device authorization failed with status {response.Status}{ErrorDetail(response.Body)}");
            var deviceCode = OAuthHttpClient.String(response.Body, "device_code");
            var userCode = OAuthHttpClient.String(response.Body, "user_code");
            var verificationUri = OAuthHttpClient.TrustedHttpUrl(OAuthHttpClient.String(response.Body, "verification_uri_complete"))
                ?? OAuthHttpClient.TrustedHttpUrl(OAuthHttpClient.String(response.Body, "verification_uri"));
            if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode) || verificationUri is null)
                throw new InvalidOperationException($"Invalid Meta device authorization response: {response.JsonText}");
            var interval = OAuthHttpClient.PositiveNumber(response.Body, "interval");
            var expires = OAuthHttpClient.PositiveNumber(response.Body, "expires_in");
            callbacks.OnDeviceCode(new OAuthDeviceCodeNotification(userCode, verificationUri, interval, expires));
            // 2. 【AI】【Meta 登录】身份令牌只用于铸造密钥，不能直接提交给模型推理接口
            var identity = await OAuthDeviceCodePoller.PollAsync(() => PollIdentityAsync(deviceCode, cancellationToken), cancellationToken,
                interval, expires, true, _clock).ConfigureAwait(false);
            callbacks.OnProgress("Enabling Meta Model API access...");
            return await MintAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Login cancelled", error, cancellationToken); }
    }

    /// <summary>【AI】【Meta 续期】使用保存的身份令牌重新铸造模型密钥，不调用不受支持的 refresh_token 授权。</summary>
    /// <param name="credentials">已有凭据。</param><param name="cancellationToken">取消信号。</param><returns>新密钥凭据。</returns>
    public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) => MintAsync(credentials.Refresh, cancellationToken);

    /// <summary>【AI】【Meta 认证】返回铸造的模型 API key。</summary><param name="credentials">凭据。</param><returns>模型密钥。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    /// <summary>【AI】【Meta 轮询】解释等待、限速、拒绝及过期状态。</summary>
    /// <param name="deviceCode">设备授权码。</param><param name="token">取消信号。</param><returns>轮询状态或身份令牌。</returns>
    private async Task<OAuthDeviceCodePollResult<string>> PollIdentityAsync(string deviceCode, CancellationToken token)
    {
        var response = await _http.PostFormAsync(TokenUrl, new Dictionary<string, string>
        { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = deviceCode, ["client_id"] = ClientId }, token).ConfigureAwait(false);
        var access = OAuthHttpClient.String(response.Body, "access_token");
        if (response.IsSuccess && !string.IsNullOrEmpty(access)) return new("complete", access);
        return OAuthHttpClient.String(response.Body, "error") switch
        {
            "authorization_pending" => new("pending"),
            "slow_down" => new("slow_down", IntervalSeconds: OAuthHttpClient.PositiveNumber(response.Body, "interval")),
            "access_denied" => new("failed", Message: "Meta login was denied."),
            "expired_token" => new("failed", Message: "Meta device authorization expired. Please restart login."),
            _ => new("failed", Message: $"Meta device token request failed with status {response.Status}{ErrorDetail(response.Body)}")
        };
    }

    /// <summary>【AI】【Meta 密钥】用身份令牌创建约一天有效的密钥，身份失效时提示重新登录。</summary>
    /// <param name="identity">身份令牌。</param><param name="token">取消信号。</param><returns>可用于模型请求的凭据。</returns>
    private async Task<OAuthCredentials> MintAsync(string identity, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MintUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity);
        request.Headers.Add("x-api-version", "1.0.0");
        var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (response.Status is 401 or 403) throw new InvalidOperationException($"Meta session expired (status {response.Status}). Run `/login meta` to sign in again.{ErrorDetail(response.Body)}");
        if (!response.IsSuccess) throw new InvalidOperationException($"Meta API key mint failed with status {response.Status}{ErrorDetail(response.Body)}");
        var key = OAuthHttpClient.String(response.Body, "api_key");
        if (string.IsNullOrEmpty(key))
        {
            var action = OAuthHttpClient.TrustedHttpUrl(OAuthHttpClient.String(response.Body, "action_url"));
            throw new InvalidOperationException("Meta did not issue an API key." + (action is null ? "" : " Complete setup at " + action));
        }
        var expiry = (long)(_clock.NowMilliseconds() + 24 * 60 * 60 * 1000);
        return new OAuthCredentials { Refresh = identity, Access = key, ExpiresAt = OAuthCredentialJson.ClampExpiry(expiry), ExpiresUnixTimeMilliseconds = expiry };
    }

    /// <summary>【AI】【Meta 错误】按上游优先级提取非空字符串诊断。</summary><param name="body">响应。</param><returns>带冒号的诊断或空串。</returns>
    private static string ErrorDetail(JsonElement body)
    {
        foreach (var key in new[] { "error_description", "detail", "message", "error" })
            if (OAuthHttpClient.String(body, key)?.Trim() is { Length: > 0 } value) return ": " + value;
        return "";
    }

}
