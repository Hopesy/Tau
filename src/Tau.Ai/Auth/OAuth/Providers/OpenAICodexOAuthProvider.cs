// 作者：xxx
using System.Security.Cryptography;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【Codex OAuth】兼容 ChatGPT Plus/Pro 的浏览器及设备码授权。</summary>
public sealed partial class OpenAICodexOAuthProvider : IOAuthProvider
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string Scope = "openid profile email offline_access";
    private const string JwtClaimPath = "https://api.openai.com/auth";
    private readonly HttpClient _client;
    private readonly OAuthFlowClock _clock;
    private readonly int _callbackPort;
    private readonly string? _callbackHost;

    /// <summary>【AI】【Codex OAuth】创建默认 1455 端口的认证提供方。</summary><param name="httpClient">可选客户端。</param>
    public OpenAICodexOAuthProvider(HttpClient? httpClient = null)
        : this(httpClient ?? TauHttpClientFactory.Create(), OAuthFlowClock.System, 1455, null) { }

    /// <summary>【AI】【Codex OAuth】为测试注入网络、时间和监听地址。</summary>
    /// <param name="client">客户端。</param><param name="clock">时钟。</param><param name="callbackPort">端口。</param><param name="callbackHost">主机。</param>
    internal OpenAICodexOAuthProvider(HttpClient client, OAuthFlowClock clock, int callbackPort, string? callbackHost)
    { _client = client; _clock = clock; _callbackPort = callbackPort; _callbackHost = callbackHost; }

    public string Id => "openai-codex";
    public string Name => "OpenAI (ChatGPT Plus/Pro)";
    public bool IsSubscription => true;
    public bool UsesCallbackServer => true;

    /// <summary>【AI】【Codex 登录】选择浏览器或设备码方式，所有交互均可被流程取消。</summary>
    /// <param name="callbacks">登录交互。</param><param name="cancellationToken">取消信号。</param><returns>包含账户 ID 的凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        cancellationToken.ThrowIfCancellationRequested();
        var method = await callbacks.OnSelectAsync("Select OpenAI Codex login method:",
            [new("browser", "Browser login (default)"), new("device_code", "Device code login (headless)")], cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return method switch
        {
            "browser" => await LoginBrowserAsync(callbacks, cancellationToken).ConfigureAwait(false),
            "device_code" => await LoginDeviceCodeAsync(callbacks, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown OpenAI Codex login method: " + (method ?? "undefined"))
        };
    }

    /// <summary>【AI】【Codex 浏览器】使用独立随机 state 和 PKCE，端口占用时回退手工回调。</summary>
    /// <param name="callbacks">交互。</param><param name="token">取消信号。</param><returns>登录凭据。</returns>
    private async Task<OAuthCredentials> LoginBrowserAsync(IOAuthLoginCallbacks callbacks, CancellationToken token)
    {
        var (verifier, challenge) = OAuthPkce.Generate();
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var host = _callbackHost ?? Environment.GetEnvironmentVariable("PI_OAUTH_CALLBACK_HOST");
        if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
        OAuthLoopbackServer<string>? callback = null;
        try
        {
            callback = OAuthLoopbackServer<string>.Listen("OpenAI", host, _callbackPort, "/auth/callback",
                (code, _) => Task.FromResult(code), token, Timeout.InfiniteTimeSpan, state: state, redirectHost: "localhost");
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
        await using (callback)
        {
            token.ThrowIfCancellationRequested();
            var redirect = callback?.RedirectUri ?? $"http://localhost:{_callbackPort}/auth/callback";
            var fields = new Dictionary<string, string> { ["response_type"] = "code", ["client_id"] = ClientId, ["redirect_uri"] = redirect,
                ["scope"] = Scope, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state,
                ["id_token_add_organizations"] = "true", ["codex_cli_simplified_flow"] = "true", ["originator"] = "pi" };
            callbacks.OnAuth(AuthorizeUrl + "?" + string.Join("&", fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
                "A browser window should open. Complete login to finish.");
            // 1. 【AI】【Codex 回调】共享输入竞争在开始兑换前取消手工提示，并观察后续输入异常
            var result = await OAuthAuthorizationInput.WaitAsync(callbacks, callback,
                "Complete login in your browser, or paste the authorization code / redirect URL here:", redirect, token).ConfigureAwait(false);
            var code = result.CallbackCode;
            if (code is null)
            {
                var parsed = OAuthAuthorizationInput.Parse(result.ManualInput!);
                if (!string.IsNullOrEmpty(parsed.State) && parsed.State != state) throw new InvalidOperationException("State mismatch");
                code = parsed.Code;
            }
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
            return await ExchangeAsync(code, verifier, redirect, token).ConfigureAwait(false);
        }
    }

    /// <summary>【AI】【Codex 认证】读取访问令牌。</summary><param name="credentials">OAuth 凭据。</param><returns>访问令牌。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
}
