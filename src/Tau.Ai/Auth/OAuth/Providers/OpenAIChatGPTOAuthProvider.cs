// 作者：xxx
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【ChatGPT 登录】动态注册公开客户端，直接为 OpenAI Responses API 获取订阅访问令牌。</summary>
public sealed class OpenAIChatGPTOAuthProvider : IOAuthProvider
{
    private const string AuthorizeUrl = "https://auth.openai.com/api/accounts/authorize";
    private const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";
    private const string Resource = "https://api.openai.com/v1";
    private const string DirectScope = "chatgpt.tokens.use.direct";
    private readonly OAuthHttpClient _http;
    private readonly OAuthFlowClock _clock;
    private readonly int _callbackPort;
    private readonly string? _callbackHost;

    /// <summary>【AI】【ChatGPT 登录】创建默认 1455 端口的订阅认证实现。</summary><param name="httpClient">可选 HTTP 客户端。</param>
    public OpenAIChatGPTOAuthProvider(HttpClient? httpClient = null)
        : this(httpClient ?? TauHttpClientFactory.Create(), OAuthFlowClock.System, 1455, null) { }

    /// <summary>【AI】【ChatGPT 登录】为隔离测试注入网络、时间和回调绑定。</summary>
    /// <param name="client">客户端。</param><param name="clock">时钟。</param><param name="callbackPort">端口。</param><param name="callbackHost">监听主机。</param>
    internal OpenAIChatGPTOAuthProvider(HttpClient client, OAuthFlowClock clock, int callbackPort, string? callbackHost)
    { _http = new(client, Timeout.InfiniteTimeSpan); _clock = clock; _callbackPort = callbackPort; _callbackHost = callbackHost; }

    public string Id => "openai";
    public string Name => "OpenAI (ChatGPT subscription)";
    public bool IsSubscription => true;
    public bool UsesCallbackServer => true;
    public string LoginLabel => "Sign in with ChatGPT";

    /// <summary>【AI】【ChatGPT 登录】兼容入口要求宿主通过带选项重载提供安装标识。</summary>
    /// <param name="callbacks">交互。</param><param name="cancellationToken">取消信号。</param><returns>凭据。</returns>
    public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) => LoginAsync(callbacks, null, cancellationToken);

    /// <summary>【AI】【ChatGPT 登录】用安装 UUID、PKCE 和 nonce 发起注册，竞争浏览器与手动完整回调 URL。</summary>
    /// <param name="callbacks">交互。</param><param name="options">安装设备标识回调。</param><param name="cancellationToken">取消信号。</param><returns>包含动态 clientId 与 scopes 的凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, OAuthLoginOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        cancellationToken.ThrowIfCancellationRequested();
        var hostId = AgentHostId(options?.GetDeviceId?.Invoke());
        var (verifier, challenge) = OAuthPkce.Generate();
        var state = RandomValue(); var nonce = RandomValue();
        var host = _callbackHost ?? Environment.GetEnvironmentVariable("PI_OAUTH_CALLBACK_HOST");
        if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
        OAuthLoopbackServer<AuthorizationResult> callback;
        try
        {
            // 1. 【AI】【ChatGPT 登录】先绑定监听端口，避免授权回调误送给另一个登录会话
            callback = OAuthLoopbackServer<AuthorizationResult>.Listen("ChatGPT", host, _callbackPort, "/auth/callback",
                (_, _) => throw new InvalidOperationException("Full registration callback required"), cancellationToken, Timeout.InfiniteTimeSpan,
                redirectHost: "127.0.0.1", validate: uri => ValidateCallback(uri, state),
                completeRequest: (uri, _) => Task.FromResult(ReadCallback(uri, state)));
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        { throw new InvalidOperationException($"Port {_callbackPort} is in use, probably by an unfinished login in another pi session or by the Codex CLI. Cancel that login and try again.", error); }
        await using (callback)
        {
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = "dynamic_agent_client", ["agent_name_hint"] = "Pi", ["ext_agent_host_id"] = hostId,
                ["response_type"] = "code", ["redirect_uri"] = callback.RedirectUri, ["resource"] = Resource,
                ["scope"] = "openid profile email offline_access resource.invoke " + DirectScope, ["state"] = state,
                ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["nonce"] = nonce
            };
            var url = AuthorizeUrl + "?" + string.Join("&", parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
            callbacks.OnAuth(url, "Complete sign-in in your browser. If the callback does not complete, paste the final redirect URL here.");
            using var manualCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var manual = ReadManualAsync(callbacks, callback.RedirectUri, state, manualCancellation.Token);
            // 2. 【AI】【ChatGPT 登录】浏览器先完成时仍观察后续输入取消，避免悬空异常
            _ = manual.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                var completed = await Task.WhenAny(callback.WaitAsync(), manual).WaitAsync(cancellationToken).ConfigureAwait(false);
                var authorization = await completed.ConfigureAwait(false) ?? throw new InvalidOperationException("OAuth callback did not complete.");
                callbacks.OnProgress("Exchanging authorization code for tokens...");
                var response = await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["client_id"] = authorization.ClientId, ["code"] = authorization.Code,
                    ["code_verifier"] = verifier, ["redirect_uri"] = callback.RedirectUri, ["resource"] = Resource
                }, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(OAuthHttpClient.String(response.Body, "id_token")))
                    throw new InvalidOperationException("OpenAI OAuth token response did not contain an ID token");
                return ParseCredential(response.Body, authorization.ClientId);
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("Login cancelled", error, cancellationToken); }
            finally { await manualCancellation.CancelAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>【AI】【ChatGPT 刷新】使用回调签发的 client ID 刷新，不再次要求 ID token。</summary>
    /// <param name="credentials">包含 clientId 的旧凭据。</param><param name="cancellationToken">取消信号。</param><returns>刷新凭据。</returns>
    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        var clientId = credentials.Metadata.GetValueOrDefault("clientId");
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Stored OpenAI OAuth credential does not contain an issued client ID; reconnect ChatGPT");
        var response = await RequestTokenAsync(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = credentials.Refresh, ["resource"] = Resource }, cancellationToken).ConfigureAwait(false);
        return ParseCredential(response.Body, clientId);
    }

    /// <summary>【AI】【ChatGPT 认证】返回可以直接发送给 OpenAI API 的访问令牌。</summary><param name="credentials">凭据。</param><returns>访问令牌。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    /// <summary>【AI】【ChatGPT 安装标识】只接受标准 UUID，并规范化为小写 URN。</summary><param name="deviceId">安装 UUID。</param><returns>agent host URI。</returns>
    internal static string AgentHostId(string? deviceId) => deviceId is { Length: 36 } && Guid.TryParseExact(deviceId, "D", out var id)
        ? "urn:uuid:" + id.ToString() : throw new InvalidOperationException("Sign in with ChatGPT requires a device ID (UUID) for this installation");

    /// <summary>【AI】【ChatGPT 手动回调】要求完整且同源同路径的回调 URL，随后校验代码、state 和 client ID。</summary>
    /// <param name="input">完整 URL。</param><param name="state">预期状态。</param><param name="redirect">本次回调地址。</param><returns>动态注册结果。</returns>
    internal static AuthorizationResult ParseManualInput(string input, string state, string redirect)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) throw new InvalidOperationException("Paste the full callback URL from the browser");
        var expected = new Uri(redirect);
        if (uri.Scheme != expected.Scheme || uri.Host != expected.Host || uri.Port != expected.Port || uri.AbsolutePath != expected.AbsolutePath)
            throw new InvalidOperationException($"The pasted callback URL must start with {redirect}");
        var error = OAuthLoopbackServer<AuthorizationResult>.Query(uri.Query).GetValueOrDefault("error");
        if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException("ChatGPT authorization failed: " + error);
        return ReadCallback(uri, state);
    }

    /// <summary>【AI】【ChatGPT 回调校验】缺失参数不会消费本次浏览器授权等待。</summary><param name="uri">回调地址。</param><param name="state">预期状态。</param><returns>诊断或空。</returns>
    private static string? ValidateCallback(Uri uri, string state)
    {
        var query = OAuthLoopbackServer<AuthorizationResult>.Query(uri.Query);
        if (string.IsNullOrEmpty(query.GetValueOrDefault("code"))) return "Missing authorization code";
        if (string.IsNullOrEmpty(query.GetValueOrDefault("state"))) return "Missing OAuth state";
        if (query["state"] != state) return "OAuth state mismatch";
        if (string.IsNullOrWhiteSpace(query.GetValueOrDefault("client_id"))) return "OpenAI OAuth registration callback did not contain an issued client ID";
        return null;
    }

    /// <summary>【AI】【ChatGPT 回调读取】校验后提取代码及动态 client ID。</summary><param name="uri">地址。</param><param name="state">预期状态。</param><returns>授权结果。</returns>
    private static AuthorizationResult ReadCallback(Uri uri, string state)
    {
        if (ValidateCallback(uri, state) is { } error) throw new InvalidOperationException(error);
        var query = OAuthLoopbackServer<AuthorizationResult>.Query(uri.Query);
        return new(query["code"], query["client_id"].Trim());
    }

    /// <summary>【AI】【ChatGPT 手动输入】读取可独立取消的完整回调 URL。</summary>
    /// <param name="callbacks">交互。</param><param name="redirect">占位地址。</param><param name="state">状态。</param><param name="token">提示取消。</param><returns>授权结果。</returns>
    private static async Task<AuthorizationResult?> ReadManualAsync(IOAuthLoginCallbacks callbacks, string redirect, string state, CancellationToken token) =>
        ParseManualInput(await callbacks.OnManualCodeInputAsync("Complete login in your browser, or paste the final redirect URL here:", redirect, token).WaitAsync(token).ConfigureAwait(false), state, redirect);

    /// <summary>【AI】【ChatGPT 令牌请求】发送公开客户端表单并校验响应是 JSON 对象。</summary>
    /// <param name="fields">表单字段。</param><param name="token">取消信号。</param><returns>响应对象。</returns>
    private async Task<OAuthHttpResponse> RequestTokenAsync(IReadOnlyDictionary<string, string> fields, CancellationToken token)
    {
        var response = await _http.PostFormAsync(TokenUrl, fields, token).ConfigureAwait(false);
        if (!response.IsSuccess) throw new InvalidOperationException($"OpenAI OAuth token request failed ({response.Status}): {(response.Text.Length > 0 ? response.Text : response.StatusText)}");
        if (response.Body.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("OpenAI OAuth token response must be an object");
        return response;
    }

    /// <summary>【AI】【ChatGPT 凭据】验证直接使用授权范围，保留 client ID 和范围数组，提前三分钟到期。</summary>
    /// <param name="body">令牌对象。</param><param name="clientId">签发客户端 ID。</param><returns>完整凭据。</returns>
    private OAuthCredentials ParseCredential(JsonElement body, string clientId)
    {
        var access = RequiredString(body, "access_token"); var refresh = RequiredString(body, "refresh_token"); var scope = RequiredString(body, "scope");
        var seconds = OAuthHttpClient.PositiveNumber(body, "expires_in") ?? throw new InvalidOperationException("OpenAI OAuth token response has invalid expires_in");
        var scopes = scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(DirectScope, StringComparer.Ordinal)) throw new InvalidOperationException("OpenAI OAuth grant did not include " + DirectScope);
        var total = _clock.NowMilliseconds() + seconds * 1000 - 180000;
        var milliseconds = total >= long.MaxValue ? long.MaxValue : (long)total;
        using var json = JsonDocument.Parse(new JsonArray(scopes.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()).ToJsonString());
        return new OAuthCredentials { Access = access, Refresh = refresh, ExpiresAt = OAuthCredentialJson.ClampExpiry(milliseconds), ExpiresUnixTimeMilliseconds = milliseconds,
            Metadata = new Dictionary<string, string> { ["clientId"] = clientId }, Properties = new Dictionary<string, JsonElement> { ["scopes"] = json.RootElement.Clone() } };
    }

    /// <summary>【AI】【ChatGPT 字段】必需字符串不能为空白，但保留原始令牌文本。</summary><param name="body">对象。</param><param name="field">字段。</param><returns>原字符串。</returns>
    private static string RequiredString(JsonElement body, string field) => OAuthHttpClient.String(body, field) is { } value && !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidOperationException("OpenAI OAuth token response has invalid " + field);

    /// <summary>【AI】【ChatGPT 随机数】生成三十二字节的 URL 安全随机 state 或 nonce。</summary><returns>Base64URL 随机值。</returns>
    private static string RandomValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>【AI】【ChatGPT 注册结果】授权码与此次动态签发的 client ID。</summary><param name="Code">授权码。</param><param name="ClientId">客户端 ID。</param>
    internal sealed record AuthorizationResult(string Code, string ClientId);
}
