using System.Text;
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth.Providers;

public sealed class AntigravityOAuthProvider : IOAuthProvider
{
    private const string ClientId = "1071006060591-tuhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    private const string ClientSecret = "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";
    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const int CallbackPort = 51121;
    private const string CallbackPath = "/oauth-callback";
    private static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/cloud-platform",
        "https://www.googleapis.com/auth/userinfo.email",
        "https://www.googleapis.com/auth/userinfo.profile",
        "https://www.googleapis.com/auth/cclog",
        "https://www.googleapis.com/auth/experimentsandconfigs"
    ];

    private static string RedirectUri => $"http://localhost:{CallbackPort}{CallbackPath}";

    public string Id => "google-antigravity";
    public string Name => "Google Antigravity";
    public bool UsesCallbackServer => true;

    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        var (verifier, challenge) = OAuthPkce.Generate();

        callbacks.OnProgress("Starting local server for OAuth callback...");
        await using var server = OAuthCallbackServer.Start(CallbackPort, CallbackPath, verifier, cancellationToken);

        var authParams = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = string.Join(" ", Scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = verifier,
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        };
        var authUrl = $"{AuthUrl}?{BuildQueryString(authParams)}";

        callbacks.OnAuth(authUrl, "Complete the sign-in in your browser.");

        callbacks.OnProgress("Waiting for OAuth callback...");
        string? code = null;

        var manualTask = callbacks.OnManualCodeInputAsync();
        if (manualTask is not null)
        {
            var callbackTask = server.WaitForCodeAsync();
            var completed = await Task.WhenAny(callbackTask, manualTask).ConfigureAwait(false);

            if (completed == callbackTask)
            {
                if (callbacks is IOAuthManualCodeInputController manualCodeController)
                {
                    manualCodeController.CancelManualCodeInput();
                }

                var result = await callbackTask.ConfigureAwait(false);
                if (result is not null)
                {
                    if (result.Value.State != verifier)
                        throw new InvalidOperationException("OAuth state mismatch.");
                    code = result.Value.Code;
                }
            }
            else
            {
                server.Cancel();
                var manualInput = await manualTask.ConfigureAwait(false);
                var parsed = OAuthInputParser.ParseAuthorizationInput(manualInput);
                if (!string.IsNullOrEmpty(parsed.State) && parsed.State != verifier)
                    throw new InvalidOperationException("OAuth state mismatch.");
                code = parsed.Code;
            }
        }
        else
        {
            var result = await server.WaitForCodeAsync().ConfigureAwait(false);
            if (result is not null)
            {
                if (result.Value.State != verifier)
                    throw new InvalidOperationException("OAuth state mismatch.");
                code = result.Value.Code;
            }
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException("No authorization code received.");
        }

        callbacks.OnProgress("Exchanging authorization code for tokens...");
        var tokens = await ExchangeCodeAsync(code, verifier, cancellationToken).ConfigureAwait(false);

        callbacks.OnProgress("Discovering project...");
        var projectId = await GoogleProjectDiscovery.DiscoverProjectAsync(tokens.AccessToken, callbacks.OnProgress, cancellationToken).ConfigureAwait(false);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["projectId"] = projectId
        };

        var email = await GoogleProjectDiscovery.GetUserEmailAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(email))
        {
            metadata["email"] = email;
        }

        return new OAuthCredentials
        {
            Refresh = tokens.RefreshToken,
            Access = tokens.AccessToken,
            ExpiresAt = tokens.ExpiresAt,
            Metadata = metadata
        };
    }

    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (!credentials.Metadata.TryGetValue("projectId", out var projectId) || string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidOperationException("Google credentials missing projectId.");
        }

        using var client = TauHttpClientFactory.Create();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["refresh_token"] = credentials.Refresh,
            ["grant_type"] = "refresh_token"
        });

        using var response = await client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google token refresh failed: {response.StatusCode} {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var newRefresh = root.TryGetProperty("refresh_token", out var refreshProp) && refreshProp.ValueKind == JsonValueKind.String
            ? refreshProp.GetString()!
            : credentials.Refresh;

        var metadata = new Dictionary<string, string>(credentials.Metadata, StringComparer.OrdinalIgnoreCase);

        return new OAuthCredentials
        {
            Refresh = newRefresh,
            Access = root.GetProperty("access_token").GetString()!,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()) - TimeSpan.FromMinutes(5),
            Metadata = metadata
        };
    }

    public string GetApiKey(OAuthCredentials credentials)
    {
        if (!credentials.Metadata.TryGetValue("projectId", out var projectId) || string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidOperationException("Google credentials missing projectId.");
        }

        return $$"""{"token":"{{credentials.Access}}","projectId":"{{projectId}}"}""";
    }

    private static async Task<TokenExchangeResult> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        using var client = TauHttpClientFactory.Create();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier
        });

        using var response = await client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google token exchange failed: {response.StatusCode} {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var refreshToken = root.TryGetProperty("refresh_token", out var refreshProp) ? refreshProp.GetString() : null;
        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new InvalidOperationException("No refresh token received. Please try again.");
        }

        return new TokenExchangeResult(
            root.GetProperty("access_token").GetString()!,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()) - TimeSpan.FromMinutes(5));
    }

    private static string BuildQueryString(Dictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

    private sealed record TokenExchangeResult(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
}
