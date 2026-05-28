using System.Text;
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth.Providers;

public sealed class OpenAICodexOAuthProvider : IOAuthProvider
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const int CallbackPort = 1455;
    private const string CallbackPath = "/auth/callback";
    private const string Scope = "openid profile email offline_access";
    private const string JwtClaimPath = "https://api.openai.com/auth";

    private static string RedirectUri => $"http://localhost:{CallbackPort}{CallbackPath}";

    public string Id => "openai-codex";
    public string Name => "ChatGPT Plus/Pro (Codex Subscription)";

    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        var (verifier, challenge) = OAuthPkce.Generate();
        var state = Guid.NewGuid().ToString("N");

        await using var server = OAuthCallbackServer.Start(CallbackPort, CallbackPath, state, cancellationToken);

        var authParams = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "tau"
        };
        var authUrl = $"{AuthorizeUrl}?{BuildQueryString(authParams)}";

        callbacks.OnAuth(authUrl, "A browser window should open. Complete login to finish.");

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
                if (result is not null) code = result.Value.Code;
            }
            else
            {
                server.Cancel();
                var manualInput = await manualTask.ConfigureAwait(false);
                var parsed = OAuthInputParser.ParseAuthorizationInput(manualInput);
                if (!string.IsNullOrEmpty(parsed.State) && parsed.State != state)
                {
                    throw new InvalidOperationException("State mismatch.");
                }
                code = parsed.Code;
            }
        }
        else
        {
            var result = await server.WaitForCodeAsync().ConfigureAwait(false);
            if (result is not null) code = result.Value.Code;
        }

        if (string.IsNullOrEmpty(code))
        {
            var input = await callbacks.OnPromptAsync("Paste the authorization code (or full redirect URL):").ConfigureAwait(false);
            var parsed = OAuthInputParser.ParseAuthorizationInput(input);
            if (!string.IsNullOrEmpty(parsed.State) && parsed.State != state)
            {
                throw new InvalidOperationException("State mismatch.");
            }
            code = parsed.Code;
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException("Missing authorization code.");
        }

        callbacks.OnProgress("Exchanging authorization code for tokens...");
        return await ExchangeCodeAsync(code, verifier, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.Refresh,
            ["client_id"] = ClientId
        });

        using var response = await client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI Codex token refresh failed: {response.StatusCode} {body}");
        }

        return ParseTokenResponse(body);
    }

    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    private static async Task<OAuthCredentials> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = RedirectUri
        });

        using var response = await client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI Codex token exchange failed: {response.StatusCode} {body}");
        }

        return ParseTokenResponse(body);
    }

    private static OAuthCredentials ParseTokenResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        var refreshToken = root.GetProperty("refresh_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        var accountId = ExtractAccountId(accessToken);
        if (string.IsNullOrEmpty(accountId))
        {
            throw new InvalidOperationException("Failed to extract accountId from OpenAI token.");
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["accountId"] = accountId
        };

        return new OAuthCredentials
        {
            Refresh = refreshToken,
            Access = accessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Metadata = metadata
        };
    }

    public static string? ExtractAccountId(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            var payload = parts[1];
            var remainder = payload.Length % 4;
            var padded = remainder == 2 ? payload + "==" : remainder == 3 ? payload + "=" : payload;
            var decoded = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
            using var doc = JsonDocument.Parse(decoded);
            var root = doc.RootElement;

            if (root.TryGetProperty(JwtClaimPath, out var authClaim) &&
                authClaim.TryGetProperty("chatgpt_account_id", out var accountIdProp) &&
                accountIdProp.ValueKind == JsonValueKind.String)
            {
                var value = accountIdProp.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string BuildQueryString(Dictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
}
