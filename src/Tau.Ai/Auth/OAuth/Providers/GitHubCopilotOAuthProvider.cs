using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tau.Ai.Auth.OAuth.Providers;

/// <summary>【AI】【Copilot OAuth】GitHub 设备授权、Copilot 令牌兑换及账户专属端点。</summary>
public sealed partial class GitHubCopilotOAuthProvider : IOAuthProvider
{
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private readonly OAuthHttpClient _http;
    private readonly HttpClient _client;
    private readonly OAuthFlowClock _clock;
    private readonly IReadOnlySet<string> _knownModelIds;
    private static readonly Dictionary<string, string> CopilotHeaders = new()
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat"
    };

    /// <summary>【AI】【Copilot OAuth】创建认证实现，可注入共享客户端。</summary><param name="httpClient">可选 HTTP 客户端。</param>
    public GitHubCopilotOAuthProvider(HttpClient? httpClient = null) : this(httpClient ?? TauHttpClientFactory.Create(), OAuthFlowClock.System) { }

    /// <summary>【AI】【Copilot OAuth】注入网络、设备码时钟与可启用模型基线，便于验证期限及账户策略。</summary>
    /// <param name="client">客户端。</param><param name="clock">时钟。</param><param name="knownModelIds">可选测试模型基线。</param>
    internal GitHubCopilotOAuthProvider(HttpClient client, OAuthFlowClock clock, IReadOnlySet<string>? knownModelIds = null)
    {
        _client = client; _http = new(client, Timeout.InfiniteTimeSpan); _clock = clock;
        _knownModelIds = knownModelIds ?? new[] { Registry.BuiltInModels.Catalog, Registry.GeneratedBuiltInModels.Catalog }
            .Where(catalog => catalog.ContainsKey("github-copilot")).SelectMany(catalog => catalog["github-copilot"].Keys).ToHashSet(StringComparer.Ordinal);
    }

    public string Id => "github-copilot";
    public string Name => "GitHub Copilot";
    public bool IsSubscription => true;

    /// <summary>【AI】【Copilot 登录】读取可取消的企业域名，发布类型化设备码，并兑换账户访问令牌。</summary>
    /// <param name="callbacks">登录交互。</param><param name="cancellationToken">整个流程取消信号。</param><returns>可保存凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = await callbacks.OnPromptAsync("GitHub Enterprise URL/domain (blank for github.com)", "company.ghe.com",
            true, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var enterprise = NormalizeDomain(input);
        if (input.Trim().Length > 0 && string.IsNullOrEmpty(enterprise)) throw new InvalidOperationException("Invalid GitHub Enterprise URL/domain");
        var domain = string.IsNullOrEmpty(enterprise) ? "github.com" : enterprise;
        // 1. 【AI】【设备授权】校验后才展示浏览器链接，保留缺省及小数秒数
        var device = await StartDeviceFlowAsync(domain, cancellationToken).ConfigureAwait(false);
        callbacks.OnDeviceCode(new OAuthDeviceCodeNotification(device.UserCode, device.VerificationUri, device.Interval, device.ExpiresIn));
        var githubToken = await PollForAccessTokenAsync(domain, device, cancellationToken).ConfigureAwait(false);
        // 2. 【AI】【令牌兑换】GitHub access 作为后续 Copilot 刷新的凭据
        var credentials = await RefreshCopilotTokenAsync(githubToken, enterprise, cancellationToken).ConfigureAwait(false);
        var catalog = await FetchModelsAsync(credentials.Access, enterprise, cancellationToken, maxRetries: 2).ConfigureAwait(false);
        var enabled = new List<string>();
        if (catalog.PolicyModelIds.Count > 0)
        {
            callbacks.OnProgress("Enabling models...");
            enabled = await EnableModelsAsync(credentials.Access, catalog.PolicyModelIds, enterprise, cancellationToken).ConfigureAwait(false);
        }
        return WithAvailableModels(credentials, catalog.AvailableModelIds.Concat(enabled).Distinct(StringComparer.Ordinal));
    }

    /// <summary>【AI】【Copilot 刷新】规范化保存的企业域名后兑换新的访问令牌。</summary>
    /// <param name="credentials">旧凭据。</param><param name="cancellationToken">取消信号。</param><returns>新凭据。</returns>
    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        var enterprise = GetEnterpriseDomain(credentials);
        var refreshed = await RefreshCopilotTokenAsync(credentials.Refresh, enterprise, cancellationToken).ConfigureAwait(false);
        var catalog = await FetchModelsAsync(refreshed.Access, enterprise, cancellationToken, maxRetries: 0).ConfigureAwait(false);
        return WithAvailableModels(refreshed, catalog.AvailableModelIds);
    }

    /// <summary>【AI】【Copilot 密钥】读取请求访问令牌。</summary><param name="credentials">凭据。</param><returns>访问令牌。</returns>
    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    /// <summary>【AI】【Copilot 认证】请求端点优先取令牌 proxy-ep，再回退企业域名或个人账户。</summary>
    /// <param name="credentials">凭据。</param><param name="token">取消信号。</param><returns>密钥及端点。</returns>
    public ProviderAuthResult ResolveAuth(OAuthCredentials credentials, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new(ApiKey: credentials.Access, BaseUrl: GetBaseUrl(credentials.Access, GetEnterpriseDomain(credentials)));
    }

    /// <summary>【AI】【旧模型适配】让兼容调用方使用当前凭据决定的端点。</summary><param name="model">模型。</param><param name="credentials">凭据。</param><returns>适配模型。</returns>
    public Model ModifyModel(Model model, OAuthCredentials credentials) => model with { BaseUrl = GetBaseUrl(credentials.Access, GetEnterpriseDomain(credentials)) };

    /// <summary>【AI】【企业域名】接受 URL 或主机名，只保留主机部分。</summary><param name="input">输入。</param><returns>主机或空值。</returns>
    public static string? NormalizeDomain(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;
        return Uri.TryCreate(trimmed.Contains("://") ? trimmed : "https://" + trimmed, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>【AI】【账户端点】解析令牌代理端点，兼容企业及个人账户。</summary><param name="token">可选访问令牌。</param><param name="enterpriseDomain">企业主机。</param><returns>API 基地址。</returns>
    public static string GetBaseUrl(string? token, string? enterpriseDomain)
    {
        if (!string.IsNullOrEmpty(token))
        {
            var match = ProxyEpPattern().Match(token);
            if (match.Success)
            {
                var host = match.Groups[1].Value;
                return "https://" + (host.StartsWith("proxy.", StringComparison.Ordinal) ? "api." + host[6..] : host);
            }
        }
        return string.IsNullOrEmpty(enterpriseDomain) ? "https://api.individual.githubcopilot.com" : "https://copilot-api." + enterpriseDomain;
    }

    /// <summary>【AI】【保存域名】同时读取兼容元数据和原生 JSON 字段，并规范化完整 URL。</summary><param name="credentials">凭据。</param><returns>企业域名或空值。</returns>
    private static string? GetEnterpriseDomain(OAuthCredentials credentials)
    {
        var value = credentials.Metadata.GetValueOrDefault("enterpriseUrl");
        if (value is null && credentials.Properties.TryGetValue("enterpriseUrl", out var raw) && raw.ValueKind == JsonValueKind.String) value = raw.GetString();
        return string.IsNullOrEmpty(value) ? null : NormalizeDomain(value);
    }

    /// <summary>【AI】【设备响应】启动 GitHub 设备流程并校验必需字段、间隔类型和验证 URI 协议。</summary>
    /// <param name="domain">GitHub 主机。</param><param name="token">取消。</param><returns>规范设备信息。</returns>
    private async Task<DeviceCodeResponse> StartDeviceFlowAsync(string domain, CancellationToken token)
    {
        var raw = await FetchJsonAsync("https://" + domain + "/login/device/code", new Dictionary<string, string> { ["client_id"] = ClientId, ["scope"] = "read:user" }, null, token).ConfigureAwait(false);
        if (raw.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid device code response");
        var device = OAuthHttpClient.String(raw, "device_code"); var user = OAuthHttpClient.String(raw, "user_code"); var uri = OAuthHttpClient.String(raw, "verification_uri");
        var interval = Number(raw, "interval"); var expires = Number(raw, "expires_in");
        if (device is null || user is null || uri is null || expires is null || raw.TryGetProperty("interval", out _) && interval is null)
            throw new InvalidOperationException("Invalid device code response fields");
        var trusted = OAuthHttpClient.TrustedHttpUrl(uri) ?? throw new InvalidOperationException("Untrusted verification_uri in device code response");
        return new(device, user, trusted, interval, expires.Value);
    }

    /// <summary>【AI】【GitHub 轮询】等待首轮间隔，按统一设备流程处理 pending、slow_down 及无效响应。</summary>
    /// <param name="domain">GitHub 主机。</param><param name="device">设备信息。</param><param name="token">取消。</param><returns>GitHub access。</returns>
    private Task<string> PollForAccessTokenAsync(string domain, DeviceCodeResponse device, CancellationToken token) =>
        OAuthDeviceCodePoller.PollAsync<string>(async () =>
        {
            var raw = await FetchJsonAsync("https://" + domain + "/login/oauth/access_token", new Dictionary<string, string>
            { ["client_id"] = ClientId, ["device_code"] = device.DeviceCode, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code" }, null, token).ConfigureAwait(false);
            if (OAuthHttpClient.String(raw, "access_token") is { } access) return new("complete", access);
            if (OAuthHttpClient.String(raw, "error") is { } error)
            {
                if (error == "authorization_pending") return new("pending");
                if (error == "slow_down") return new("slow_down", IntervalSeconds: Number(raw, "interval"));
                var description = OAuthHttpClient.String(raw, "error_description");
                return new("failed", Message: "Device flow failed: " + error + (string.IsNullOrEmpty(description) ? "" : ": " + description));
            }
            return new("failed", Message: "Invalid device token response");
        }, token, device.Interval, device.ExpiresIn, waitBeforeFirstPoll: true, clock: _clock);

    /// <summary>【AI】【Copilot 令牌】校验兑换响应，保存五分钟期限余量及原生毫秒值。</summary>
    /// <param name="githubToken">GitHub access。</param><param name="enterpriseDomain">企业域名。</param><param name="token">取消。</param><returns>新凭据。</returns>
    private async Task<OAuthCredentials> RefreshCopilotTokenAsync(string githubToken, string? enterpriseDomain, CancellationToken token)
    {
        var raw = await FetchJsonAsync("https://api." + (string.IsNullOrEmpty(enterpriseDomain) ? "github.com" : enterpriseDomain) + "/copilot_internal/v2/token", null, githubToken, token).ConfigureAwait(false);
        if (raw.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid Copilot token response");
        var access = OAuthHttpClient.String(raw, "token"); var seconds = Number(raw, "expires_at");
        if (access is null || seconds is null) throw new InvalidOperationException("Invalid Copilot token response fields");
        var rawMilliseconds = seconds.Value * 1000 - 300000;
        var milliseconds = rawMilliseconds >= long.MaxValue ? long.MaxValue : rawMilliseconds <= long.MinValue ? long.MinValue : (long)rawMilliseconds;
        return new() { Refresh = githubToken, Access = access, ExpiresUnixTimeMilliseconds = milliseconds,
            ExpiresAt = OAuthCredentialJson.ClampExpiry(milliseconds),
            Metadata = string.IsNullOrEmpty(enterpriseDomain) ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["enterpriseUrl"] = enterpriseDomain } };
    }

    /// <summary>【AI】【GitHub HTTP】发送设备表单或 Copilot GET，非成功状态统一保留 HTTP 诊断。</summary>
    /// <param name="url">地址。</param><param name="fields">表单，空值使用 GET。</param><param name="bearer">可选 Copilot 兑换认证。</param><param name="token">取消。</param><returns>JSON 快照。</returns>
    private async Task<JsonElement> FetchJsonAsync(string url, IReadOnlyDictionary<string, string>? fields, string? bearer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(fields is null ? HttpMethod.Get : HttpMethod.Post, url);
        if (fields is not null) request.Content = new FormUrlEncodedContent(fields);
        request.Headers.TryAddWithoutValidation("User-Agent", CopilotHeaders["User-Agent"]);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            foreach (var header in CopilotHeaders.Where(header => header.Key != "User-Agent")) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var response = await _http.SendAsync(request, token).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!response.IsSuccess) throw new InvalidOperationException($"{response.Status} {response.StatusText}: {response.Text}");
        return response.Body;
    }

    /// <summary>【AI】【协议数字】读取有限数字，保留小数秒数而不要求正数。</summary><param name="value">JSON。</param><param name="field">字段。</param><returns>数字或空值。</returns>
    private static double? Number(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var raw)
        && raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    /// <summary>【AI】【代理端点匹配】获取令牌中的 proxy-ep 字段。</summary><returns>匹配表达式。</returns>
    [GeneratedRegex(@"proxy-ep=([^;]+)")]
    private static partial Regex ProxyEpPattern();

    /// <summary>【AI】【设备信息】保留服务器给出的原始秒数。</summary>
    private sealed record DeviceCodeResponse(string DeviceCode, string UserCode, string VerificationUri, double? Interval, double ExpiresIn);
}
