using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai.Auth.OAuth.Providers;

public sealed class AnthropicOAuthProvider : IOAuthProvider
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const int CallbackPort = 53692;
    private const string CallbackPath = "/callback";
    private const string Scopes = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    private static string RedirectUri =>
        $"http://localhost:{CallbackPort}{CallbackPath}";

    public string Id => "anthropic";
    public string Name => "Anthropic (Claude Pro/Max)";

    public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        var (verifier, challenge) = OAuthPkce.Generate();

        await using var server = OAuthCallbackServer.Start(CallbackPort, CallbackPath, verifier, cancellationToken);

        var authParams = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = verifier
        };
        var authUrl = $"{AuthorizeUrl}?{BuildQueryString(authParams)}";

        callbacks.OnAuth(authUrl, "Complete login in your browser. If the browser is on another machine, paste the final redirect URL here.");

        string? code = null;
        string? state = null;

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
                    code = result.Value.Code;
                    state = result.Value.State;
                }
            }
            else
            {
                server.Cancel();
                var manualInput = await manualTask.ConfigureAwait(false);
                var parsed = OAuthInputParser.ParseAuthorizationInput(manualInput);
                code = parsed.Code;
                state = parsed.State ?? verifier;
            }
        }
        else
        {
            var result = await server.WaitForCodeAsync().ConfigureAwait(false);
            if (result is not null)
            {
                code = result.Value.Code;
                state = result.Value.State;
            }
        }

        if (string.IsNullOrEmpty(code))
        {
            var input = await callbacks.OnPromptAsync("Paste the authorization code or full redirect URL:", RedirectUri).ConfigureAwait(false);
            var parsed = OAuthInputParser.ParseAuthorizationInput(input);
            if (!string.IsNullOrEmpty(parsed.State) && parsed.State != verifier)
            {
                throw new InvalidOperationException("OAuth state mismatch.");
            }

            code = parsed.Code;
            state = parsed.State ?? verifier;
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException("Missing authorization code.");
        }

        callbacks.OnProgress("Exchanging authorization code for tokens...");
        return await ExchangeCodeAsync(code, state ?? verifier, verifier, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        var body = BuildJsonBody(
            ("grant_type", "refresh_token"),
            ("client_id", ClientId),
            ("refresh_token", credentials.Refresh));

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Anthropic token refresh failed: {response.StatusCode} {responseBody}");
        }

        return ParseTokenResponse(responseBody);
    }

    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    private static async Task<OAuthCredentials> ExchangeCodeAsync(
        string code, string state, string verifier, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var body = BuildJsonBody(
            ("grant_type", "authorization_code"),
            ("client_id", ClientId),
            ("code", code),
            ("state", state),
            ("redirect_uri", RedirectUri),
            ("code_verifier", verifier));

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Anthropic token exchange failed: {response.StatusCode} {responseBody}");
        }

        return ParseTokenResponse(responseBody);
    }

    private static OAuthCredentials ParseTokenResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        return new OAuthCredentials
        {
            Refresh = root.GetProperty("refresh_token").GetString()!,
            Access = root.GetProperty("access_token").GetString()!,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()) - TimeSpan.FromMinutes(5)
        };
    }

    private static string BuildJsonBody(params (string Key, string Value)[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var (key, value) in fields)
        {
            writer.WriteString(key, value);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildQueryString(Dictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
}
