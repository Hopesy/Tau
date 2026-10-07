// 作者：xxx
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.Ai.Auth.OAuth.Providers;

public sealed partial class OpenAICodexOAuthProvider
{
    /// <summary>【AI】【Codex 兑换】浏览器和设备模式共用表单端点，重定向地址由当前方式决定。</summary>
    /// <param name="code">授权码。</param><param name="verifier">PKCE 校验器。</param><param name="redirect">当前回调。</param>
    /// <param name="token">取消信号。</param><returns>账户凭据。</returns>
    private async Task<OAuthCredentials> ExchangeAsync(string code, string verifier, string redirect, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect }) };
        return ParseToken(await FetchAsync(request, token).ConfigureAwait(false), "exchange");
    }

    /// <summary>【AI】【Codex 刷新】不添加登录交互，保留网络失败与 HTTP 失败的不同诊断。</summary>
    /// <param name="credentials">旧凭据。</param><param name="cancellationToken">取消信号。</param><returns>新账户凭据。</returns>
    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["refresh_token"] = credentials.Refresh, ["client_id"] = ClientId }) };
        OAuthHttpResponse response;
        try { response = await FetchAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("OpenAI Codex token refresh error: " + error.Message, error); }
        return ParseToken(response, "refresh");
    }

    /// <summary>【AI】【Codex HTTP】读取独立响应快照；设备等待状态直接释放正文，错误正文读取失败时使用状态说明。</summary>
    /// <param name="request">请求。</param><param name="token">取消信号。</param><param name="skipBodyStatuses">无须读取正文的设备状态。</param><returns>响应。</returns>
    private async Task<OAuthHttpResponse> FetchAsync(HttpRequestMessage request, CancellationToken token, int[]? skipBodyStatuses = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (skipBodyStatuses?.Contains((int)response.StatusCode) == true) return new((int)response.StatusCode, "", default, response.ReasonPhrase);
            string text;
            try { text = await response.Content.ReadAsStringAsync(token).WaitAsync(token).ConfigureAwait(false); }
            catch (Exception) when (!response.IsSuccessStatusCode && !token.IsCancellationRequested) { text = ""; }
            token.ThrowIfCancellationRequested();
            JsonElement body = default;
            try { using var document = JsonDocument.Parse(text); body = document.RootElement.Clone(); }
            catch (JsonException) { }
            return new((int)response.StatusCode, text, body, response.ReasonPhrase);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        { throw new OperationCanceledException("Login cancelled", error, token); }
    }

    /// <summary>【AI】【Codex JSON】设备端点使用原生 JSON 且不附加额外请求头。</summary>
    /// <param name="url">端点。</param><param name="fields">字段。</param><param name="token">取消。</param><param name="skipBodyStatuses">直接返回的状态。</param><returns>响应。</returns>
    private async Task<OAuthHttpResponse> PostDeviceAsync(string url, JsonObject fields, CancellationToken token, int[] skipBodyStatuses)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(fields.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Content.Headers.ContentType!.CharSet = null;
        return await FetchAsync(request, token, skipBodyStatuses).ConfigureAwait(false);
    }

    /// <summary>【AI】【Codex 令牌】校验令牌字段，保留不减余量的原生期限，并提取账户 ID。</summary>
    /// <param name="response">响应。</param><param name="operation">兑换或刷新。</param><returns>凭据。</returns>
    private OAuthCredentials ParseToken(OAuthHttpResponse response, string operation)
    {
        if (!response.IsSuccess) throw new InvalidOperationException($"OpenAI Codex token {operation} failed ({response.Status}): {(response.Text.Length > 0 ? response.Text : response.StatusText)}");
        var body = RequireJson(response);
        var access = OAuthHttpClient.String(body, "access_token"); var refresh = OAuthHttpClient.String(body, "refresh_token");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetDouble(out var seconds))
            throw new InvalidOperationException($"OpenAI Codex token {operation} response missing fields: {response.JsonText}");
        var accountId = ExtractAccountId(access) ?? throw new InvalidOperationException("Failed to extract accountId from token");
        var total = _clock.NowMilliseconds() + seconds * 1000;
        var milliseconds = total >= long.MaxValue ? long.MaxValue : total <= long.MinValue ? long.MinValue : (long)total;
        return new() { Access = access, Refresh = refresh, ExpiresAt = OAuthCredentialJson.ClampExpiry(milliseconds), ExpiresUnixTimeMilliseconds = milliseconds,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["accountId"] = accountId } };
    }

    /// <summary>【AI】【Codex JSON】成功响应必须具有可解析 JSON，结构验证由各操作负责。</summary>
    /// <param name="response">HTTP 响应。</param><returns>JSON 快照。</returns>
    private static JsonElement RequireJson(OAuthHttpResponse response) => response.Body.ValueKind != JsonValueKind.Undefined ? response.Body
        : throw new JsonException("OpenAI Codex OAuth returned invalid JSON");

    /// <summary>【AI】【Codex 账户】从 JWT payload 提取非空账户标识，损坏令牌返回空引用。</summary>
    /// <param name="accessToken">访问令牌。</param><returns>账户 ID；此方法不执行签名认证。</returns>
    public static string? ExtractAccountId(string accessToken)
    {
        var parts = accessToken.Split('.'); if (parts.Length != 3) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(JwtClaimPath, out var claim) &&
                OAuthHttpClient.String(claim, "chatgpt_account_id") is { Length: > 0 } id ? id : null;
        }
        catch (Exception error) when (error is JsonException or FormatException) { return null; }
    }
}
