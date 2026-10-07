// 作者：xxx
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【Anthropic OAuth】支持浏览器回调及无本地端口的复制授权码登录。</summary>
public sealed class AnthropicOAuthProvider : IOAuthProvider
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string CallbackPath = "/callback";
    private const string CopyCodeRedirectUri = "https://platform.claude.com/oauth/code/callback";
    private const string Scopes = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    private readonly OAuthHttpClient _http;
    private readonly OAuthFlowClock _clock;
    private readonly int _callbackPort;
    private readonly string? _callbackHost;
    private readonly TimeSpan _requestTimeout;

    /// <summary>【AI】【Anthropic OAuth】创建默认端口 53692、请求时限三十秒的认证实现。</summary><param name="httpClient">可选 HTTP 客户端。</param>
    public AnthropicOAuthProvider(HttpClient? httpClient = null)
        : this(httpClient ?? TauHttpClientFactory.Create(), OAuthFlowClock.System, 53692, null, TimeSpan.FromSeconds(30)) { }

    /// <summary>【AI】【Anthropic OAuth】注入协议测试需要的网络、时间、绑定地址及请求时限。</summary>
    /// <param name="client">HTTP 客户端。</param><param name="clock">时间来源。</param><param name="callbackPort">回调端口，零为自动分配。</param>
    /// <param name="callbackHost">监听地址。</param><param name="requestTimeout">单次请求时限。</param>
    internal AnthropicOAuthProvider(HttpClient client, OAuthFlowClock clock, int callbackPort, string? callbackHost, TimeSpan requestTimeout)
    { _http = new(client, Timeout.InfiniteTimeSpan); _clock = clock; _callbackPort = callbackPort; _callbackHost = callbackHost; _requestTimeout = requestTimeout; }

    public string Id => "anthropic";
    public string Name => "Anthropic (Claude Pro/Max)";
    public bool IsSubscription => true;
    public bool UsesCallbackServer => true;

    /// <summary>【AI】【Anthropic 登录】先选择浏览器或复制代码方式，再以 PKCE 交换授权码。</summary>
    /// <param name="callbacks">类型化登录交互。</param><param name="cancellationToken">流程取消信号。</param><returns>订阅凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        cancellationToken.ThrowIfCancellationRequested();
        var method = await callbacks.OnSelectAsync("Select Anthropic login method:",
            [new("browser", "Browser login (default)"), new("copy_code", "Copy code login (headless)")], cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (method is not ("browser" or "copy_code")) throw new InvalidOperationException("Unknown Anthropic login method: " + (method ?? "undefined"));
        var (verifier, challenge) = OAuthPkce.Generate();
        if (method == "copy_code")
        {
            NotifyAuthorization(callbacks, challenge, verifier, CopyCodeRedirectUri,
                "Complete login in your browser, then copy the code Anthropic shows and paste it here.");
            var input = await callbacks.OnManualCodeInputAsync("Paste the code Anthropic shows after you sign in:", "code#state", cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await ExchangeInputAsync(callbacks, input, verifier, CopyCodeRedirectUri, cancellationToken).ConfigureAwait(false);
        }
        return await LoginBrowserAsync(callbacks, verifier, challenge, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>【AI】【Anthropic 浏览器】监听失败时保留手工回调入口，浏览器与手工输入共享一次登录。</summary>
    /// <param name="callbacks">交互。</param><param name="verifier">PKCE 校验器及 state。</param><param name="challenge">PKCE 挑战。</param>
    /// <param name="token">取消信号。</param><returns>登录凭据。</returns>
    private async Task<OAuthCredentials> LoginBrowserAsync(IOAuthLoginCallbacks callbacks, string verifier, string challenge, CancellationToken token)
    {
        var host = _callbackHost ?? Environment.GetEnvironmentVariable("PI_OAUTH_CALLBACK_HOST");
        if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
        OAuthLoopbackServer<string>? callback = null;
        try
        {
            // 1. 【AI】【Anthropic 回调】浏览器地址固定为 localhost，监听地址可按运行环境覆盖
            callback = OAuthLoopbackServer<string>.Listen("Anthropic", host, _callbackPort, CallbackPath,
                (code, _) => Task.FromResult(code), token, Timeout.InfiniteTimeSpan, state: verifier, redirectHost: "localhost");
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
        await using (callback)
        {
            token.ThrowIfCancellationRequested();
            var redirect = callback?.RedirectUri ?? $"http://localhost:{_callbackPort}{CallbackPath}";
            NotifyAuthorization(callbacks, challenge, verifier, redirect,
                "Complete login in your browser. If the browser is on another machine, paste the final redirect URL here.");
            // 2. 【AI】【Anthropic 回调】共享等待负责输入竞争及取消，兑换开始前结束手工提示
            var result = await OAuthAuthorizationInput.WaitAsync(callbacks, callback,
                "Complete login in your browser, or paste the authorization code / redirect URL here:", redirect, token).ConfigureAwait(false);
            return await ExchangeInputAsync(callbacks, result.CallbackCode ?? result.ManualInput!, verifier, redirect, token,
                fromCallback: result.CallbackCode is not null).ConfigureAwait(false);
        }
    }

    /// <summary>【AI】【Anthropic 授权地址】展示所选方式的回调地址及完整 PKCE 请求。</summary>
    /// <param name="callbacks">交互。</param><param name="challenge">挑战值。</param><param name="state">状态。</param>
    /// <param name="redirect">当前方式的重定向地址。</param><param name="instructions">方式说明。</param>
    private static void NotifyAuthorization(IOAuthLoginCallbacks callbacks, string challenge, string state, string redirect, string instructions)
    {
        var fields = new Dictionary<string, string> { ["code"] = "true", ["client_id"] = ClientId, ["response_type"] = "code",
            ["redirect_uri"] = redirect, ["scope"] = Scopes, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state };
        callbacks.OnAuth(AuthorizeUrl + "?" + string.Join("&", fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))), instructions);
    }

    /// <summary>【AI】【Anthropic 授权码】支持完整 URL、code#state、查询串及原始代码，重复查询参数采用第一项。</summary>
    /// <param name="input">用户输入。</param><returns>代码和可选状态。</returns>
    internal static (string? Code, string? State) ParseAuthorizationInput(string input) => OAuthAuthorizationInput.Parse(input);

    /// <summary>【AI】【Anthropic 兑换】校验手工 state 后发送与所选方式一致的 redirect_uri。</summary>
    /// <param name="callbacks">交互。</param><param name="input">授权输入。</param><param name="verifier">本次 PKCE 校验器。</param>
    /// <param name="redirect">本次重定向地址。</param><param name="token">取消信号。</param><param name="fromCallback">代码是否已由回调服务器校验。</param><returns>凭据。</returns>
    private Task<OAuthCredentials> ExchangeInputAsync(IOAuthLoginCallbacks callbacks, string input, string verifier, string redirect, CancellationToken token, bool fromCallback = false)
    {
        token.ThrowIfCancellationRequested();
        var parsed = fromCallback ? (Code: (string?)input, State: (string?)verifier) : ParseAuthorizationInput(input);
        if (!string.IsNullOrEmpty(parsed.State) && parsed.State != verifier) throw new InvalidOperationException("OAuth state mismatch");
        if (string.IsNullOrEmpty(parsed.Code)) throw new InvalidOperationException("Missing authorization code");
        callbacks.OnProgress("Exchanging authorization code for tokens...");
        return RequestTokenAsync(new JsonObject { ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = parsed.Code,
            ["state"] = parsed.State ?? verifier, ["redirect_uri"] = redirect, ["code_verifier"] = verifier }, redirect, token);
    }

    /// <summary>【AI】【Anthropic 刷新】发送刷新令牌并使用同样的三十秒请求期限和错误链。</summary>
    /// <param name="credentials">旧凭据。</param><param name="cancellationToken">取消信号。</param><returns>刷新后的凭据。</returns>
    public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) =>
        RequestTokenAsync(new JsonObject { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = credentials.Refresh }, null, cancellationToken);

    /// <summary>【AI】【Anthropic 认证】读取订阅访问令牌。</summary><param name="credentials">凭据。</param><returns>访问令牌。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    /// <summary>【AI】【Anthropic HTTP】独立限制发送和正文读取的总时长，保留 HTTP 与 JSON 错误的操作上下文。</summary>
    /// <param name="fields">JSON 请求字段。</param><param name="redirect">兑换时的地址，刷新时为空。</param><param name="token">取消信号。</param><returns>原生期限凭据。</returns>
    private async Task<OAuthCredentials> RequestTokenAsync(JsonObject fields, string? redirect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var operation = redirect is null ? "Anthropic token refresh" : "Token exchange";
        OAuthHttpResponse response;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new StringContent(fields.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Content.Headers.ContentType!.CharSet = null;
        try
        {
            response = await _http.SendAsync(request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!response.IsSuccess) throw new InvalidOperationException($"HTTP request failed. status={response.Status}; url={TokenUrl}; body={response.Text}");
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested) { throw new OperationCanceledException("Login cancelled", error, token); }
        catch (Exception error)
        { throw new InvalidOperationException($"{operation} request failed. url={TokenUrl};{(redirect is null ? "" : $" redirect_uri={redirect}; response_type=authorization_code;")} details={error}", error); }
        if (response.Body.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"{operation} returned invalid JSON. url={TokenUrl}; body={response.Text}");
        var body = response.Body;
        var access = body.GetProperty("access_token").GetString()!;
        var refresh = body.GetProperty("refresh_token").GetString()!;
        var total = _clock.NowMilliseconds() + body.GetProperty("expires_in").GetDouble() * 1000 - 300000;
        if (!double.IsFinite(total)) throw new InvalidOperationException($"{operation} returned an invalid token expiry");
        var milliseconds = total >= long.MaxValue ? long.MaxValue : total <= long.MinValue ? long.MinValue : (long)total;
        return new() { Access = access, Refresh = refresh, ExpiresAt = OAuthCredentialJson.ClampExpiry(milliseconds), ExpiresUnixTimeMilliseconds = milliseconds };
    }

}
