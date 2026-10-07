// 作者：xxx
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【OpenRouter 登录】使用 PKCE、本地回调或手动授权码换取由用户控制的长期 API key。</summary>
public sealed class OpenRouterOAuthProvider : IOAuthProvider
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _loginTimeout;
    private readonly TimeSpan _exchangeTimeout;
    private readonly string? _callbackHost;

    /// <summary>【AI】【OpenRouter 登录】创建认证实现，默认登录期限五分钟、兑换期限三十秒。</summary>
    /// <param name="httpClient">可选 HTTP 客户端。</param>
    public OpenRouterOAuthProvider(HttpClient? httpClient = null)
        : this(httpClient ?? TauHttpClientFactory.Create(), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), null) { }

    /// <summary>【AI】【OpenRouter 登录】为本地验证注入网络、期限和监听地址。</summary>
    /// <param name="httpClient">HTTP 客户端。</param><param name="loginTimeout">登录期限。</param>
    /// <param name="exchangeTimeout">兑换期限。</param><param name="callbackHost">回调地址。</param>
    internal OpenRouterOAuthProvider(HttpClient httpClient, TimeSpan loginTimeout, TimeSpan exchangeTimeout, string? callbackHost)
    { _httpClient = httpClient; _loginTimeout = loginTimeout; _exchangeTimeout = exchangeTimeout; _callbackHost = callbackHost; }

    public string Id => "openrouter";
    public string Name => "OpenRouter OAuth";
    public bool UsesCallbackServer => true;
    public string LoginLabel => "Sign in with OpenRouter";

    /// <summary>【AI】【OpenRouter 登录】展示授权地址，同时接收浏览器回调和可独立取消的手动输入。</summary>
    /// <param name="callbacks">认证交互。</param><param name="cancellationToken">登录取消信号。</param>
    /// <returns>没有刷新令牌的长期 OAuth 凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        cancellationToken.ThrowIfCancellationRequested();
        // 1. 【AI】【OpenRouter 登录】OpenRouter 不返回 state，用随机路径绑定本次授权
        var (verifier, challenge) = OAuthPkce.Generate();
        var host = _callbackHost ?? Environment.GetEnvironmentVariable("PI_OAUTH_CALLBACK_HOST");
        if (string.IsNullOrEmpty(host)) host = Environment.GetEnvironmentVariable("TAU_OAUTH_CALLBACK_HOST");
        if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
        await using var callback = OAuthLoopbackServer<OAuthCredentials>.Listen("OpenRouter", host, 0,
            "/oauth/callback/" + Guid.NewGuid().ToString(),
            (code, token) => ExchangeAsync(code, verifier, token), cancellationToken, _loginTimeout);
        var url = "https://openrouter.ai/auth?callback_url=" + Uri.EscapeDataString(callback.RedirectUri)
            + "&code_challenge=" + Uri.EscapeDataString(challenge) + "&code_challenge_method=S256";
        callbacks.OnProgress($"Listening for OpenRouter OAuth callback on {callback.RedirectUri}");
        callbacks.OnAuth(url, "Complete sign-in in your browser. If the browser is on another machine, paste the final redirect URL here.");

        // 2. 【AI】【OpenRouter 登录】输入只取消尚未认领的回调，已开始兑换的浏览器请求继续完成
        using var manualCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var manual = ReadManualAsync(callbacks, callback, manualCancellation.Token);
        try
        {
            var value = await callback.WaitAsync().ConfigureAwait(false);
            if (manual.IsCompletedSuccessfully && manual.Result.Error is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            if (value is not null) return value;
            var input = await manual.ConfigureAwait(false);
            if (input.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            var code = ParseAuthorizationInput(input.Value ?? "");
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
            callbacks.OnProgress("Exchanging authorization code for an API key...");
            return await ExchangeAsync(code, verifier, cancellationToken).ConfigureAwait(false);
        }
        finally { await manualCancellation.CancelAsync().ConfigureAwait(false); }
    }

    /// <summary>【AI】【OpenRouter 登录】长期密钥不需要刷新，原样返回凭据。</summary>
    /// <param name="credentials">已有凭据。</param><param name="cancellationToken">兼容刷新接口的信号。</param><returns>原凭据。</returns>
    public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult(credentials);

    /// <summary>【AI】【OpenRouter 登录】获取 API 请求密钥。</summary><param name="credentials">凭据。</param><returns>密钥。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    /// <summary>【AI】【OpenRouter 登录】观察手动输入的成功与异常，避免浏览器先完成后出现未观察异常。</summary>
    /// <param name="callbacks">认证交互。</param><param name="callback">回调服务器。</param>
    /// <param name="token">单次提示取消信号。</param><returns>输入或异常。</returns>
    private static async Task<ManualResult> ReadManualAsync(IOAuthLoginCallbacks callbacks, OAuthLoopbackServer<OAuthCredentials> callback, CancellationToken token)
    {
        try
        {
            var value = await callbacks.OnPromptAsync("Complete sign-in in your browser, or paste the authorization code / redirect URL here:",
                callback.RedirectUri, true, token).WaitAsync(token).ConfigureAwait(false);
            callback.Cancel();
            return new(value, null);
        }
        catch (Exception error) { callback.Cancel(); return new(null, error); }
    }

    /// <summary>【AI】【OpenRouter 登录】支持完整重定向 URL、查询串和原始授权码。</summary>
    /// <param name="input">用户输入。</param><returns>授权码，缺失时为空。</returns>
    internal static string? ParseAuthorizationInput(string input)
    {
        var value = input.Trim();
        if (value.Length == 0) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return OAuthLoopbackServer<OAuthCredentials>.Query(uri.Query).GetValueOrDefault("code");
        return value.Contains("code=", StringComparison.Ordinal) ? OAuthLoopbackServer<OAuthCredentials>.Query(value).GetValueOrDefault("code") : value;
    }

    /// <summary>【AI】【OpenRouter 登录】在独立期限内提交 PKCE 授权码，解析 API key 与服务端错误。</summary>
    /// <param name="code">授权码。</param><param name="verifier">本次 PKCE 验证器。</param>
    /// <param name="token">登录取消信号。</param><returns>长期凭据。</returns>
    private async Task<OAuthCredentials> ExchangeAsync(string code, string verifier, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_exchangeTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/auth/keys")
        {
            Content = new StringContent(new JsonObject { ["code"] = code, ["code_verifier"] = verifier, ["code_challenge_method"] = "S256" }.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            JsonDocument? document = null;
            try { document = JsonDocument.Parse(text); }
            catch (JsonException error) when (response.IsSuccessStatusCode) { throw new InvalidOperationException("OpenRouter OAuth returned invalid JSON", error); }
            catch (JsonException) { }
            using (document)
            {
                var body = document?.RootElement ?? default;
                if (!response.IsSuccessStatusCode)
                {
                    var detail = ErrorDetail(body);
                    throw new InvalidOperationException($"OpenRouter OAuth key exchange failed (HTTP {(int)response.StatusCode}){(string.IsNullOrEmpty(detail) ? "" : ": " + detail)}");
                }
                var key = GetString(body, "key");
                if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("OpenRouter OAuth response carries no \"key\"");
                return new OAuthCredentials { Access = key, Refresh = "", ExpiresAt = DateTimeOffset.MaxValue, ExpiresUnixTimeMilliseconds = 9007199254740991L };
            }
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested) { throw new OperationCanceledException("Login cancelled", error, token); }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested) { throw new TimeoutException("OpenRouter OAuth token exchange timed out", error); }
    }

    /// <summary>【AI】【OpenRouter 登录】按上游优先级提取错误详情，非对象响应不提供详情。</summary>
    /// <param name="body">响应 JSON。</param><returns>错误详情。</returns>
    private static string? ErrorDetail(JsonElement body) => GetString(body, "error_description") ?? GetString(body, "message") ?? GetString(body, "error")
        ?? (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) ? GetString(error, "message") : null);

    /// <summary>【AI】【OpenRouter 登录】安全读取字符串字段。</summary><param name="body">对象。</param><param name="key">字段。</param><returns>字符串或空。</returns>
    private static string? GetString(JsonElement body, string key) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>【AI】【OpenRouter 登录】手工输入及已观察到的异常。</summary>
    private sealed record ManualResult(string? Value, Exception? Error);
}
