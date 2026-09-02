using System.Net.Http.Headers;
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>
/// xAI（Grok/X 订阅）的 OAuth 设备码认证 provider。
/// </summary>
public sealed class XaiOAuthProvider : IOAuthProvider
{
    private const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const string Scope = "openid profile email offline_access grok-cli:access api:access";
    private const string DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code";
    private const string TokenUrl = "https://auth.x.ai/oauth2/token";
    private const int RefreshSkewMinutes = 5;
    private const int DefaultTokenLifetimeSeconds = 3600;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 创建 xAI OAuth provider。
    /// </summary>
    /// <param name="httpClient">可选 HTTP 客户端；未提供时使用 Tau 默认客户端。</param>
    public XaiOAuthProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Id => "xai";
    public string Name => "xAI (Grok/X subscription)";

    /// <summary>
    /// 申请 xAI 设备码并轮询用户授权结果。
    /// </summary>
    /// <param name="callbacks">用于显示授权地址、验证码和进度的交互回调。</param>
    /// <param name="cancellationToken">取消登录流程的令牌。</param>
    /// <returns>登录成功后的 OAuth 凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(
        IOAuthLoginCallbacks callbacks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);

        callbacks.OnProgress("Starting xAI device code flow...");
        var device = await RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        callbacks.OnAuth(
            device.VerificationUriComplete ?? device.VerificationUri,
            $"Enter code: {device.UserCode}");
        callbacks.OnProgress("Waiting for xAI authorization...");

        return await PollForTokenAsync(device, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用 refresh token 获取新的 xAI access token。
    /// </summary>
    /// <param name="credentials">包含 refresh token 的旧凭据。</param>
    /// <param name="cancellationToken">取消网络请求的令牌。</param>
    /// <returns>刷新后的 OAuth 凭据。</returns>
    public async Task<OAuthCredentials> RefreshTokenAsync(
        OAuthCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var response = await PostFormAsync(
            TokenUrl,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = credentials.Refresh
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateRequestFailure("token refresh", response);
        }

        return ParseTokenCredentials(response.Body, credentials.Refresh);
    }

    /// <summary>
    /// 返回 provider 请求所使用的 access token。
    /// </summary>
    /// <param name="credentials">OAuth 凭据。</param>
    /// <returns>access token。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    private async Task<XaiDeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        var response = await PostFormAsync(
            DeviceCodeUrl,
            new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope,
                ["referrer"] = "pi"
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateRequestFailure("device authorization", response);
        }

        var root = response.Body;
        var deviceCode = RequiredString(root, "device_code");
        var userCode = RequiredString(root, "user_code");
        var verificationUri = ValidateVerificationUri(RequiredString(root, "verification_uri"));
        var verificationUriComplete = root.TryGetProperty("verification_uri_complete", out var complete) &&
                                      complete.ValueKind == JsonValueKind.String &&
                                      !string.IsNullOrWhiteSpace(complete.GetString())
            ? ValidateVerificationUri(complete.GetString()!)
            : null;
        var expiresIn = PositiveInt(root, "expires_in");
        var interval = root.TryGetProperty("interval", out var intervalElement) &&
                       intervalElement.ValueKind == JsonValueKind.Number &&
                       intervalElement.TryGetInt32(out var intervalValue) &&
                       intervalValue > 0
            ? intervalValue
            : 5;

        return new XaiDeviceCode(
            deviceCode,
            userCode,
            verificationUri,
            verificationUriComplete,
            interval,
            expiresIn);
    }

    private async Task<OAuthCredentials> PollForTokenAsync(
        XaiDeviceCode device,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresInSeconds);
        var intervalSeconds = Math.Max(1, device.IntervalSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);

            var response = await PostFormAsync(
                TokenUrl,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = device.DeviceCode
                },
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return ParseTokenCredentials(response.Body);
            }

            var error = GetString(response.Body, "error");
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    intervalSeconds = Math.Max(
                        intervalSeconds + 5,
                        GetPositiveInt(response.Body, "interval") ?? intervalSeconds + 5);
                    continue;
                case "access_denied":
                case "authorization_denied":
                    throw new InvalidOperationException("xAI device authorization was denied.");
                case "expired_token":
                    throw new InvalidOperationException("xAI device code expired.");
                default:
                    throw CreateRequestFailure("device token polling", response);
            }
        }

        throw new TimeoutException("xAI device flow timed out.");
    }

    private async Task<XaiHttpResponse> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(values)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("xAI login cancelled.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"xAI OAuth returned invalid JSON (HTTP {(int)response.StatusCode}).",
                    ex);
            }

            return new XaiHttpResponse(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                document.RootElement.Clone());
        }
    }

    private static OAuthCredentials ParseTokenCredentials(JsonElement body, string? previousRefreshToken = null)
    {
        var access = RequiredString(body, "access_token");
        var refresh = body.TryGetProperty("refresh_token", out var refreshElement) &&
                      refreshElement.ValueKind == JsonValueKind.String &&
                      !string.IsNullOrWhiteSpace(refreshElement.GetString())
            ? refreshElement.GetString()!
            : previousRefreshToken ?? throw new InvalidOperationException("Invalid xAI OAuth response field: refresh_token");
        var expiresIn = GetPositiveInt(body, "expires_in") ?? DefaultTokenLifetimeSeconds;

        return new OAuthCredentials
        {
            Access = access,
            Refresh = refresh,
            ExpiresAt = DateTimeOffset.UtcNow
                .AddSeconds(expiresIn)
                .Subtract(TimeSpan.FromMinutes(RefreshSkewMinutes))
        };
    }

    private static InvalidOperationException CreateRequestFailure(string action, XaiHttpResponse response)
    {
        var error = GetString(response.Body, "error");
        var description = GetString(response.Body, "error_description");
        var detail = string.Join(": ", new[] { error, description }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new InvalidOperationException(
            $"xAI OAuth {action} failed (HTTP {response.StatusCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
    }

    private static string RequiredString(JsonElement body, string field)
    {
        var value = GetString(body, field);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Invalid xAI OAuth response field: {field}");
    }

    private static int PositiveInt(JsonElement body, string field) =>
        GetPositiveInt(body, field)
        ?? throw new InvalidOperationException($"Invalid xAI OAuth response field: {field}");

    private static int? GetPositiveInt(JsonElement body, string field) =>
        body.TryGetProperty(field, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) &&
        number > 0
            ? number
            : null;

    private static string? GetString(JsonElement body, string field) =>
        body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ValidateVerificationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Untrusted verification URI in xAI OAuth response.");
        }

        return uri.AbsoluteUri;
    }

    private sealed record XaiDeviceCode(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string? VerificationUriComplete,
        int IntervalSeconds,
        int ExpiresInSeconds);

    private sealed record XaiHttpResponse(bool IsSuccessStatusCode, int StatusCode, JsonElement Body);
}
