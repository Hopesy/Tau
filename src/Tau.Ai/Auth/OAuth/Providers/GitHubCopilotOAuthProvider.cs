using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tau.Ai.Auth.OAuth.Providers;

public sealed partial class GitHubCopilotOAuthProvider : IOAuthProvider
{
    private const string ClientId = "Iv1.b507a08c87ecfe98";

    private static readonly Dictionary<string, string> CopilotHeaders = new()
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat"
    };

    public string Id => "github-copilot";
    public string Name => "GitHub Copilot";

    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        var input = await callbacks.OnPromptAsync(
            "GitHub Enterprise URL/domain (blank for github.com):",
            "company.ghe.com",
            allowEmpty: true).ConfigureAwait(false);

        var enterpriseDomain = NormalizeDomain(input);
        if (!string.IsNullOrWhiteSpace(input.Trim()) && enterpriseDomain is null)
        {
            throw new InvalidOperationException("Invalid GitHub Enterprise URL/domain.");
        }

        var domain = enterpriseDomain ?? "github.com";

        callbacks.OnProgress("Starting device code flow...");
        var device = await StartDeviceFlowAsync(domain, cancellationToken).ConfigureAwait(false);

        callbacks.OnAuth(device.VerificationUri, $"Enter code: {device.UserCode}");

        callbacks.OnProgress("Waiting for authorization...");
        var githubAccessToken = await PollForAccessTokenAsync(
            domain, device.DeviceCode, device.Interval, device.ExpiresIn, cancellationToken).ConfigureAwait(false);

        callbacks.OnProgress("Exchanging for Copilot token...");
        var credentials = await RefreshCopilotTokenAsync(githubAccessToken, enterpriseDomain, cancellationToken).ConfigureAwait(false);

        return credentials;
    }

    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        credentials.Metadata.TryGetValue("enterpriseUrl", out var enterpriseDomain);
        return await RefreshCopilotTokenAsync(credentials.Refresh, enterpriseDomain, cancellationToken).ConfigureAwait(false);
    }

    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    public Model ModifyModel(Model model, OAuthCredentials credentials)
    {
        var baseUrl = GetBaseUrl(credentials.Access, credentials.Metadata.GetValueOrDefault("enterpriseUrl"));
        return model with { BaseUrl = baseUrl };
    }

    public static string? NormalizeDomain(string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        try
        {
            var url = trimmed.Contains("://") ? new Uri(trimmed) : new Uri($"https://{trimmed}");
            return url.Host;
        }
        catch
        {
            return null;
        }
    }

    public static string GetBaseUrl(string? token, string? enterpriseDomain)
    {
        if (!string.IsNullOrEmpty(token))
        {
            var match = ProxyEpPattern().Match(token);
            if (match.Success)
            {
                var proxyHost = match.Groups[1].Value;
                var apiHost = proxyHost.StartsWith("proxy.", StringComparison.Ordinal)
                    ? "api." + proxyHost[6..]
                    : proxyHost;
                return $"https://{apiHost}";
            }
        }

        if (!string.IsNullOrEmpty(enterpriseDomain))
        {
            return $"https://copilot-api.{enterpriseDomain}";
        }

        return "https://api.individual.githubcopilot.com";
    }

    private static async Task<DeviceCodeResponse> StartDeviceFlowAsync(string domain, CancellationToken cancellationToken)
    {
        using var client = TauHttpClientFactory.Create();
        var url = $"https://{domain}/login/device/code";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.35.0");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new DeviceCodeResponse(
            root.GetProperty("device_code").GetString()!,
            root.GetProperty("user_code").GetString()!,
            root.GetProperty("verification_uri").GetString()!,
            root.GetProperty("interval").GetInt32(),
            root.GetProperty("expires_in").GetInt32());
    }

    private static async Task<string> PollForAccessTokenAsync(
        string domain, string deviceCode, int intervalSeconds, int expiresIn, CancellationToken cancellationToken)
    {
        var url = $"https://{domain}/login/oauth/access_token";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        var intervalMs = Math.Max(1000, intervalSeconds * 1000);

        using var client = TauHttpClientFactory.Create();

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.35.0");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String)
            {
                return tokenProp.GetString()!;
            }

            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String)
            {
                var error = errorProp.GetString()!;
                if (error == "authorization_pending") continue;
                if (error == "slow_down")
                {
                    intervalMs = root.TryGetProperty("interval", out var newInterval) && newInterval.ValueKind == JsonValueKind.Number
                        ? newInterval.GetInt32() * 1000
                        : intervalMs + 5000;
                    continue;
                }

                var description = root.TryGetProperty("error_description", out var descProp) ? descProp.GetString() : null;
                throw new InvalidOperationException($"Device flow failed: {error}{(description is not null ? $": {description}" : "")}");
            }
        }

        throw new TimeoutException("Device flow timed out.");
    }

    private static async Task<OAuthCredentials> RefreshCopilotTokenAsync(
        string accessToken, string? enterpriseDomain, CancellationToken cancellationToken)
    {
        var domain = enterpriseDomain ?? "github.com";
        var url = $"https://api.{domain}/copilot_internal/v2/token";

        using var client = TauHttpClientFactory.Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        foreach (var header in CopilotHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Copilot token refresh failed: {response.StatusCode} {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var token = root.GetProperty("token").GetString()!;
        var expiresAt = root.GetProperty("expires_at").GetInt64();

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(enterpriseDomain))
        {
            metadata["enterpriseUrl"] = enterpriseDomain;
        }

        return new OAuthCredentials
        {
            Refresh = accessToken,
            Access = token,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAt) - TimeSpan.FromMinutes(5),
            Metadata = metadata
        };
    }

    [GeneratedRegex(@"proxy-ep=([^;]+)")]
    private static partial Regex ProxyEpPattern();

    private sealed record DeviceCodeResponse(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int Interval,
        int ExpiresIn);
}
