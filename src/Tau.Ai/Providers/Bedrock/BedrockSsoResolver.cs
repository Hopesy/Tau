using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockSsoResolver
{
    public static async Task<BedrockCredentialProcessOutcome> ResolveAsync(
        BedrockOptions options,
        BedrockProfileSnapshot? profile,
        HttpClient httpClient,
        Func<DateTimeOffset> clock,
        CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            return BedrockCredentialProcessOutcome.NotConfigured();
        }

        if (string.IsNullOrWhiteSpace(profile.SsoAccountId) ||
            string.IsNullOrWhiteSpace(profile.SsoRoleName))
        {
            return BedrockCredentialProcessOutcome.NotConfigured();
        }

        if (string.IsNullOrWhiteSpace(profile.SsoStartUrl) ||
            string.IsNullOrWhiteSpace(profile.SsoRegion))
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"profile '{profile.Name}' references SSO but is missing sso_start_url or sso_region.");
        }

        var cachePath = ResolveTokenCachePath(options, profile);
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"SSO token cache not found at {cachePath ?? "(unresolved path)"}; run `aws sso login --profile {profile.Name}` first.");
        }

        string cacheContent;
        try
        {
            cacheContent = await File.ReadAllTextAsync(cachePath!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"failed to read SSO token cache ({cachePath}): {ex.Message}");
        }

        string? accessToken;
        try
        {
            accessToken = ParseAccessToken(cacheContent, clock, out var tokenError);
            if (accessToken is null)
            {
                return BedrockCredentialProcessOutcome.Failure(
                    tokenError ?? "SSO token cache did not contain a usable access token.");
            }
        }
        catch (JsonException ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"SSO token cache is not valid JSON: {ex.Message}");
        }

        var portalEndpoint = ResolveSsoPortalEndpoint(options, profile.SsoRegion!);
        var uri = new Uri(
            $"{portalEndpoint.TrimEnd('/')}/federation/credentials?role_name={Uri.EscapeDataString(profile.SsoRoleName!)}&account_id={Uri.EscapeDataString(profile.SsoAccountId!)}",
            UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("x-amz-sso_bearer_token", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"SSO GetRoleCredentials request failed: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)response.StatusCode}" : body.Trim();
                return BedrockCredentialProcessOutcome.Failure(
                    $"SSO GetRoleCredentials returned HTTP {(int)response.StatusCode}: {detail}");
            }

            return ParseRoleCredentialsJson(body, clock);
        }
    }

    internal static string? ParseAccessToken(string json, Func<DateTimeOffset> clock, out string? error)
    {
        error = null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            error = "SSO token cache must be a JSON object.";
            return null;
        }

        var accessToken = GetString(document.RootElement, "accessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            error = "SSO token cache does not contain accessToken.";
            return null;
        }

        var expiresAt = GetString(document.RootElement, "expiresAt");
        if (!string.IsNullOrWhiteSpace(expiresAt))
        {
            if (!DateTimeOffset.TryParse(
                    expiresAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                error = $"SSO token cache has unparseable expiresAt value: {expiresAt}";
                return null;
            }

            if (parsed <= clock())
            {
                error = $"SSO access token expired at {expiresAt}; run `aws sso login` again.";
                return null;
            }
        }

        return accessToken;
    }

    internal static BedrockCredentialProcessOutcome ParseRoleCredentialsJson(string json, Func<DateTimeOffset> clock)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"SSO GetRoleCredentials response is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("roleCredentials", out var roleCredentials) ||
                roleCredentials.ValueKind != JsonValueKind.Object)
            {
                return BedrockCredentialProcessOutcome.Failure(
                    "SSO GetRoleCredentials response did not contain roleCredentials.");
            }

            var accessKeyId = GetString(roleCredentials, "accessKeyId");
            var secretAccessKey = GetString(roleCredentials, "secretAccessKey");
            var sessionToken = GetString(roleCredentials, "sessionToken");
            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
            {
                return BedrockCredentialProcessOutcome.Failure(
                    "SSO GetRoleCredentials response is missing accessKeyId or secretAccessKey.");
            }

            DateTimeOffset? expiresAt = null;
            if (roleCredentials.TryGetProperty("expiration", out var expirationProperty) &&
                expirationProperty.ValueKind == JsonValueKind.Number &&
                expirationProperty.TryGetInt64(out var expirationMillis))
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expirationMillis);
                if (expiresAt.Value <= clock())
                {
                    return BedrockCredentialProcessOutcome.Failure(
                        $"SSO role credentials already expired (expiration={expiresAt.Value:O}).");
                }
            }

            return BedrockCredentialProcessOutcome.Success(new BedrockAwsCredentials(
                AccessKeyId: accessKeyId!,
                SecretAccessKey: secretAccessKey!,
                SessionToken: string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken,
                ExpiresAt: expiresAt,
                Source: "sso"));
        }
    }

    internal static string? ResolveTokenCachePath(BedrockOptions options, BedrockProfileSnapshot profile)
    {
        if (!string.IsNullOrWhiteSpace(options.SsoTokenCacheFile))
        {
            return options.SsoTokenCacheFile;
        }

        var directory = !string.IsNullOrWhiteSpace(options.SsoTokenCacheDirectory)
            ? options.SsoTokenCacheDirectory
            : GetDefaultSsoCacheDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        // Per AWS SSO contract: the cache hash is the SHA-1 (lowercase hex) of the
        // sso_session name when present, otherwise of the sso_start_url.
        var hashInput = !string.IsNullOrWhiteSpace(profile.SsoSession)
            ? profile.SsoSession!
            : profile.SsoStartUrl;
        if (string.IsNullOrWhiteSpace(hashInput))
        {
            return null;
        }

        var hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(hashInput));
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(directory!, hashHex + ".json");
    }

    private static string? GetDefaultSsoCacheDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".aws", "sso", "cache");
    }

    private static string ResolveSsoPortalEndpoint(BedrockOptions options, string region)
    {
        var overrideEndpoint = FirstNonEmpty(
            options.SsoPortalEndpoint,
            Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_SSO"));
        if (!string.IsNullOrWhiteSpace(overrideEndpoint))
        {
            return overrideEndpoint!;
        }

        return $"https://portal.sso.{region}.amazonaws.com";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
