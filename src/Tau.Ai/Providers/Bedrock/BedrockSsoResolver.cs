using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tau.Ai;

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

        if (string.IsNullOrWhiteSpace(profile.SsoSession) &&
            string.IsNullOrWhiteSpace(profile.SsoStartUrl))
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"profile '{profile.Name}' references SSO but is missing sso_session or sso_start_url.");
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

        SsoTokenCache tokenCache;
        try
        {
            tokenCache = ParseTokenCache(cacheContent, out var tokenError);
            if (tokenCache.AccessToken is null && string.IsNullOrWhiteSpace(tokenCache.RefreshToken))
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

        var accessToken = ResolveUsableAccessToken(tokenCache, clock);
        if (accessToken is null)
        {
            var refreshOutcome = await RefreshAccessTokenAsync(
                options,
                profile,
                cachePath!,
                cacheContent,
                tokenCache,
                httpClient,
                clock,
                cancellationToken).ConfigureAwait(false);
            if (refreshOutcome.Error is not null)
            {
                return BedrockCredentialProcessOutcome.Failure(refreshOutcome.Error);
            }

            if (string.IsNullOrWhiteSpace(refreshOutcome.AccessToken))
            {
                return BedrockCredentialProcessOutcome.Failure("SSO OIDC CreateToken refresh did not return a usable access token.");
            }

            accessToken = refreshOutcome.AccessToken;
        }

        var ssoRegion = ResolveSsoRegion(profile, tokenCache);
        if (string.IsNullOrWhiteSpace(ssoRegion))
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"SSO token cache cannot call GetRoleCredentials because profile/cache is missing sso_region or region; run `aws sso login --profile {profile.Name}` again.");
        }

        var portalEndpoint = ResolveSsoPortalEndpoint(options, ssoRegion!);
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
        var tokenCache = ParseTokenCache(json, out error);
        if (tokenCache.AccessToken is null)
        {
            return null;
        }

        return ResolveUsableAccessToken(tokenCache, clock, out error);
    }

    internal static SsoTokenCache ParseTokenCache(string json, out string? error)
    {
        error = null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            error = "SSO token cache must be a JSON object.";
            return SsoTokenCache.Empty;
        }

        var accessToken = GetString(document.RootElement, "accessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            error = "SSO token cache does not contain accessToken.";
        }

        var expiresAt = GetString(document.RootElement, "expiresAt");
        var registrationExpiresAt = GetString(document.RootElement, "registrationExpiresAt");
        return new SsoTokenCache(
            accessToken,
            expiresAt,
            GetString(document.RootElement, "clientId"),
            GetString(document.RootElement, "clientSecret"),
            registrationExpiresAt,
            GetString(document.RootElement, "refreshToken"),
            GetString(document.RootElement, "startUrl"),
            GetString(document.RootElement, "region"),
            GetScopes(document.RootElement));
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

    internal static string? ResolveUsableAccessToken(
        SsoTokenCache tokenCache,
        Func<DateTimeOffset> clock) =>
        ResolveUsableAccessToken(tokenCache, clock, out _);

    internal static string? ResolveUsableAccessToken(
        SsoTokenCache tokenCache,
        Func<DateTimeOffset> clock,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(tokenCache.AccessToken))
        {
            error = "SSO token cache does not contain accessToken.";
            return null;
        }

        if (!TryParseOptionalTimestamp(tokenCache.ExpiresAt, "expiresAt", out var expiresAt, out error))
        {
            return null;
        }

        if (expiresAt is not null && expiresAt.Value <= clock())
        {
            error = $"SSO access token expired at {tokenCache.ExpiresAt}; run `aws sso login` again.";
            return null;
        }

        return tokenCache.AccessToken;
    }

    private static async Task<SsoTokenRefreshOutcome> RefreshAccessTokenAsync(
        BedrockOptions options,
        BedrockProfileSnapshot profile,
        string cachePath,
        string cacheContent,
        SsoTokenCache tokenCache,
        HttpClient httpClient,
        Func<DateTimeOffset> clock,
        CancellationToken cancellationToken)
    {
        var expiredAt = string.IsNullOrWhiteSpace(tokenCache.ExpiresAt) ? "unknown" : tokenCache.ExpiresAt;
        if (string.IsNullOrWhiteSpace(tokenCache.RefreshToken))
        {
            return SsoTokenRefreshOutcome.Failure(
                $"SSO access token expired at {expiredAt} and token cache cannot refresh because it is missing refreshToken; run `aws sso login --profile {profile.Name}` again.");
        }

        var registration = await ResolveClientRegistrationAsync(
            options,
            profile,
            tokenCache,
            httpClient,
            clock,
            cancellationToken).ConfigureAwait(false);
        if (registration.Error is not null)
        {
            return SsoTokenRefreshOutcome.Failure(
                RedactKnownSsoCacheValues(registration.Error, tokenCache));
        }

        var ssoRegion = ResolveSsoRegion(profile, tokenCache);
        if (string.IsNullOrWhiteSpace(ssoRegion))
        {
            return SsoTokenRefreshOutcome.Failure(
                $"SSO token cache cannot refresh because profile/cache is missing sso_region or region; run `aws sso login --profile {profile.Name}` again.");
        }

        var oidcEndpoint = ResolveSsoOidcEndpoint(options, ssoRegion!);
        var uri = new Uri($"{oidcEndpoint.TrimEnd('/')}/token", UriKind.Absolute);
        var requestBody = new Dictionary<string, object>
        {
            ["clientId"] = registration.ClientId!,
            ["clientSecret"] = registration.ClientSecret!,
            ["grantType"] = "refresh_token",
            ["refreshToken"] = tokenCache.RefreshToken!
        };
        var requestJson = JsonSerializer.Serialize(requestBody, BedrockJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SsoTokenRefreshOutcome.Failure($"SSO OIDC CreateToken refresh request failed: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = RedactKnownSsoValues(
                    ExtractOidcError(body),
                    registration.ClientSecret,
                    tokenCache.ClientSecret,
                    tokenCache.RefreshToken);
                return SsoTokenRefreshOutcome.Failure(
                    $"SSO OIDC CreateToken refresh returned HTTP {(int)response.StatusCode}: {detail}");
            }

            var refreshResult = ParseCreateTokenResponse(body, clock, registration);
            if (refreshResult.Error is not null)
            {
                return refreshResult;
            }

            try
            {
                await WriteUpdatedTokenCacheAsync(cachePath, cacheContent, refreshResult, profile, tokenCache, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The freshly returned access token is still usable for this request. Cache persistence
                // is a best-effort durability step and must not discard a successful refresh.
            }

            return refreshResult;
        }
    }

    private static async Task<SsoClientRegistrationOutcome> ResolveClientRegistrationAsync(
        BedrockOptions options,
        BedrockProfileSnapshot profile,
        SsoTokenCache tokenCache,
        HttpClient httpClient,
        Func<DateTimeOffset> clock,
        CancellationToken cancellationToken)
    {
        var startUrl = ResolveSsoStartUrl(profile, tokenCache);
        var ssoRegion = ResolveSsoRegion(profile, tokenCache);
        var scopes = ResolveRegistrationScopes(profile, tokenCache);

        var hasClientRegistration =
            !string.IsNullOrWhiteSpace(tokenCache.ClientId) &&
            !string.IsNullOrWhiteSpace(tokenCache.ClientSecret);
        var registrationExpirationParsed = TryParseOptionalTimestamp(
            tokenCache.RegistrationExpiresAt,
            "registrationExpiresAt",
            out var registrationExpiresAt,
            out _);
        var existingRegistrationUsable = hasClientRegistration &&
            registrationExpirationParsed &&
            registrationExpiresAt is not null &&
            registrationExpiresAt.Value > clock();

        if (existingRegistrationUsable)
        {
            return SsoClientRegistrationOutcome.Success(
                tokenCache.ClientId!,
                tokenCache.ClientSecret!,
                registrationExpiresAt,
                clientIdIssuedAt: null,
                startUrl,
                ssoRegion,
                scopes);
        }

        if (hasClientRegistration &&
            registrationExpirationParsed &&
            registrationExpiresAt is null &&
            (string.IsNullOrWhiteSpace(startUrl) || string.IsNullOrWhiteSpace(ssoRegion)))
        {
            // Older cache entries can omit registrationExpiresAt. Keep that legacy
            // refresh path only when there is not enough metadata to renew the client.
            return SsoClientRegistrationOutcome.Success(
                tokenCache.ClientId!,
                tokenCache.ClientSecret!,
                registrationExpiresAt,
                clientIdIssuedAt: null,
                startUrl,
                ssoRegion,
                scopes);
        }

        if (string.IsNullOrWhiteSpace(startUrl))
        {
            return SsoClientRegistrationOutcome.Failure(
                $"SSO client registration cannot be renewed because profile/cache is missing sso_start_url or startUrl; run `aws sso login --profile {profile.Name}` again.");
        }

        if (string.IsNullOrWhiteSpace(ssoRegion))
        {
            return SsoClientRegistrationOutcome.Failure(
                $"SSO client registration cannot be renewed because profile/cache is missing sso_region or region; run `aws sso login --profile {profile.Name}` again.");
        }

        var oidcEndpoint = ResolveSsoOidcEndpoint(options, ssoRegion!);
        var uri = new Uri($"{oidcEndpoint.TrimEnd('/')}/client/register", UriKind.Absolute);
        var requestBody = new Dictionary<string, object>
        {
            ["clientName"] = "tau-bedrock-sso",
            ["clientType"] = "public",
            ["grantTypes"] = new List<object> { "refresh_token" },
            ["scopes"] = scopes.Select(scope => (object)scope).ToList()
        };
        var requestJson = JsonSerializer.Serialize(requestBody, BedrockJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = RedactKnownSsoCacheValues(ex.Message, tokenCache);
            return SsoClientRegistrationOutcome.Failure($"SSO OIDC RegisterClient request failed: {detail}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = RedactKnownSsoCacheValues(ExtractOidcError(body), tokenCache);
                return SsoClientRegistrationOutcome.Failure(
                    $"SSO OIDC RegisterClient returned HTTP {(int)response.StatusCode}: {detail}");
            }

            return ParseRegisterClientResponse(
                body,
                startUrl,
                ssoRegion,
                scopes,
                tokenCache.AccessToken,
                tokenCache.ClientSecret,
                tokenCache.RefreshToken);
        }
    }

    private static SsoClientRegistrationOutcome ParseRegisterClientResponse(
        string json,
        string startUrl,
        string ssoRegion,
        IReadOnlyList<string> scopes,
        params string?[] sensitiveValues)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            var detail = RedactKnownSsoValues(ex.Message, sensitiveValues);
            return SsoClientRegistrationOutcome.Failure(
                $"SSO OIDC RegisterClient response is not valid JSON: {detail}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SsoClientRegistrationOutcome.Failure("SSO OIDC RegisterClient response must be a JSON object.");
            }

            var clientId = GetString(document.RootElement, "clientId");
            var clientSecret = GetString(document.RootElement, "clientSecret");
            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                !TryGetInt64(document.RootElement, "clientSecretExpiresAt", out var clientSecretExpiresAt))
            {
                return SsoClientRegistrationOutcome.Failure(
                    "SSO OIDC RegisterClient response is missing clientId, clientSecret, or clientSecretExpiresAt.");
            }

            TryGetInt64(document.RootElement, "clientIdIssuedAt", out var clientIdIssuedAt);
            return SsoClientRegistrationOutcome.Success(
                clientId!,
                clientSecret!,
                DateTimeOffset.FromUnixTimeSeconds(clientSecretExpiresAt),
                clientIdIssuedAt == 0 ? null : clientIdIssuedAt,
                startUrl,
                ssoRegion,
                scopes);
        }
    }

    private static SsoTokenRefreshOutcome ParseCreateTokenResponse(
        string json,
        Func<DateTimeOffset> clock,
        SsoClientRegistrationOutcome registration)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return SsoTokenRefreshOutcome.Failure(
                $"SSO OIDC CreateToken response is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SsoTokenRefreshOutcome.Failure("SSO OIDC CreateToken response must be a JSON object.");
            }

            var accessToken = GetString(document.RootElement, "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return SsoTokenRefreshOutcome.Failure("SSO OIDC CreateToken response is missing accessToken.");
            }

            DateTimeOffset? expiresAt = null;
            if (document.RootElement.TryGetProperty("expiresIn", out var expiresInProperty) &&
                expiresInProperty.ValueKind == JsonValueKind.Number &&
                expiresInProperty.TryGetInt64(out var expiresInSeconds))
            {
                expiresAt = clock().AddSeconds(expiresInSeconds);
            }

            return SsoTokenRefreshOutcome.Success(
                accessToken!,
                expiresAt,
                GetString(document.RootElement, "refreshToken"),
                registration);
        }
    }

    private static async Task WriteUpdatedTokenCacheAsync(
        string cachePath,
        string originalJson,
        SsoTokenRefreshOutcome refreshResult,
        BedrockProfileSnapshot profile,
        SsoTokenCache tokenCache,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(originalJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            await using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    seen.Add(property.Name);
                    if (string.Equals(property.Name, "accessToken", StringComparison.Ordinal))
                    {
                        writer.WriteString("accessToken", refreshResult.AccessToken);
                    }
                    else if (string.Equals(property.Name, "expiresAt", StringComparison.Ordinal))
                    {
                        WriteExpiresAt(writer, refreshResult);
                    }
                    else if (string.Equals(property.Name, "refreshToken", StringComparison.Ordinal) &&
                             !string.IsNullOrWhiteSpace(refreshResult.RefreshToken))
                    {
                        writer.WriteString("refreshToken", refreshResult.RefreshToken);
                    }
                    else if (string.Equals(property.Name, "clientId", StringComparison.Ordinal) &&
                             !string.IsNullOrWhiteSpace(refreshResult.ClientId))
                    {
                        writer.WriteString("clientId", refreshResult.ClientId);
                    }
                    else if (string.Equals(property.Name, "clientSecret", StringComparison.Ordinal) &&
                             !string.IsNullOrWhiteSpace(refreshResult.ClientSecret))
                    {
                        writer.WriteString("clientSecret", refreshResult.ClientSecret);
                    }
                    else if (string.Equals(property.Name, "registrationExpiresAt", StringComparison.Ordinal) &&
                             refreshResult.RegistrationExpiresAt is not null)
                    {
                        WriteTimestamp(writer, "registrationExpiresAt", refreshResult.RegistrationExpiresAt.Value);
                    }
                    else if (string.Equals(property.Name, "clientIdIssuedAt", StringComparison.Ordinal) &&
                             refreshResult.ClientIdIssuedAt is not null)
                    {
                        writer.WriteNumber("clientIdIssuedAt", refreshResult.ClientIdIssuedAt.Value);
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                if (!seen.Contains("accessToken"))
                {
                    writer.WriteString("accessToken", refreshResult.AccessToken);
                }

                if (!seen.Contains("expiresAt") && refreshResult.ExpiresAt is not null)
                {
                    WriteExpiresAt(writer, refreshResult);
                }

                if (!seen.Contains("refreshToken") && !string.IsNullOrWhiteSpace(refreshResult.RefreshToken))
                {
                    writer.WriteString("refreshToken", refreshResult.RefreshToken);
                }

                if (!seen.Contains("clientId") && !string.IsNullOrWhiteSpace(refreshResult.ClientId))
                {
                    writer.WriteString("clientId", refreshResult.ClientId);
                }

                if (!seen.Contains("clientSecret") && !string.IsNullOrWhiteSpace(refreshResult.ClientSecret))
                {
                    writer.WriteString("clientSecret", refreshResult.ClientSecret);
                }

                if (!seen.Contains("registrationExpiresAt") && refreshResult.RegistrationExpiresAt is not null)
                {
                    WriteTimestamp(writer, "registrationExpiresAt", refreshResult.RegistrationExpiresAt.Value);
                }

                if (!seen.Contains("clientIdIssuedAt") && refreshResult.ClientIdIssuedAt is not null)
                {
                    writer.WriteNumber("clientIdIssuedAt", refreshResult.ClientIdIssuedAt.Value);
                }

                var startUrl = ResolveSsoStartUrl(profile, tokenCache);
                if (!seen.Contains("startUrl") && !string.IsNullOrWhiteSpace(startUrl))
                {
                    writer.WriteString("startUrl", startUrl);
                }

                var ssoRegion = ResolveSsoRegion(profile, tokenCache);
                if (!seen.Contains("region") && !string.IsNullOrWhiteSpace(ssoRegion))
                {
                    writer.WriteString("region", ssoRegion);
                }

                if (!seen.Contains("registrationScopes") && refreshResult.RegistrationScopes.Count > 0)
                {
                    WriteStringArray(writer, "registrationScopes", refreshResult.RegistrationScopes);
                }

                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void WriteExpiresAt(Utf8JsonWriter writer, SsoTokenRefreshOutcome refreshResult)
    {
        if (refreshResult.ExpiresAt is null)
        {
            return;
        }

        WriteTimestamp(writer, "expiresAt", refreshResult.ExpiresAt.Value);
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string propertyName, DateTimeOffset value)
    {
        writer.WriteString(propertyName, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string? ResolveSsoStartUrl(BedrockProfileSnapshot profile, SsoTokenCache tokenCache) =>
        FirstNonEmpty(profile.SsoStartUrl, tokenCache.StartUrl);

    private static string? ResolveSsoRegion(BedrockProfileSnapshot profile, SsoTokenCache tokenCache) =>
        FirstNonEmpty(profile.SsoRegion, tokenCache.Region);

    private static IReadOnlyList<string> ResolveRegistrationScopes(
        BedrockProfileSnapshot profile,
        SsoTokenCache tokenCache)
    {
        var profileScopes = ParseScopeList(profile.SsoRegistrationScopes);
        if (profileScopes.Count > 0)
        {
            return profileScopes;
        }

        return tokenCache.RegistrationScopes.Count > 0
            ? tokenCache.RegistrationScopes
            : ["sso:account:access"];
    }

    private static IReadOnlyList<string> ParseScopeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> GetScopes(JsonElement element)
    {
        if (TryGetScopeProperty(element, "registrationScopes", out var scopes) ||
            TryGetScopeProperty(element, "scopes", out scopes))
        {
            return scopes;
        }

        return [];
    }

    private static bool TryGetScopeProperty(JsonElement element, string propertyName, out IReadOnlyList<string> scopes)
    {
        scopes = [];
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            scopes = ParseScopeList(property.GetString());
            return scopes.Count > 0;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        scopes = property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return scopes.Count > 0;
    }

    private static bool TryParseOptionalTimestamp(
        string? value,
        string propertyName,
        out DateTimeOffset? parsed,
        out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedValue))
        {
            error = $"unparseable {propertyName} value: {value}";
            return false;
        }

        parsed = parsedValue;
        return true;
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
            ProviderEnvironment.GetValue("AWS_ENDPOINT_URL_SSO", options.Env));
        if (!string.IsNullOrWhiteSpace(overrideEndpoint))
        {
            return overrideEndpoint!;
        }

        return $"https://portal.sso.{region}.amazonaws.com";
    }

    private static string ResolveSsoOidcEndpoint(BedrockOptions options, string region)
    {
        var overrideEndpoint = FirstNonEmpty(
            options.SsoOidcEndpoint,
            ProviderEnvironment.GetValue("AWS_ENDPOINT_URL_SSO_OIDC", options.Env));
        if (!string.IsNullOrWhiteSpace(overrideEndpoint))
        {
            return overrideEndpoint!;
        }

        return $"https://oidc.{region}.amazonaws.com";
    }

    private static string ExtractOidcError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "empty response body";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var code = GetString(document.RootElement, "error");
                var description = GetString(document.RootElement, "error_description");
                if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(description))
                {
                    return $"{code}: {description}";
                }

                if (!string.IsNullOrWhiteSpace(code))
                {
                    return code!;
                }
            }
        }
        catch (JsonException)
        {
        }

        return body.Trim();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private static string RedactKnownSsoValues(string value, params string?[] sensitiveValues)
    {
        var redacted = new TauSecretRedactor(enabled: true).Redact(value);
        foreach (var sensitiveValue in sensitiveValues)
        {
            if (!string.IsNullOrWhiteSpace(sensitiveValue))
            {
                redacted = redacted.Replace(sensitiveValue!, TauSecretRedactor.Placeholder, StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    private static string RedactKnownSsoCacheValues(string value, SsoTokenCache tokenCache) =>
        RedactKnownSsoValues(
            value,
            tokenCache.AccessToken,
            tokenCache.ClientSecret,
            tokenCache.RefreshToken);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    internal sealed record SsoTokenCache(
        string? AccessToken,
        string? ExpiresAt,
        string? ClientId,
        string? ClientSecret,
        string? RegistrationExpiresAt,
        string? RefreshToken,
        string? StartUrl,
        string? Region,
        IReadOnlyList<string> RegistrationScopes)
    {
        public static SsoTokenCache Empty { get; } = new(null, null, null, null, null, null, null, null, []);
    }

    internal sealed record SsoClientRegistrationOutcome(
        string? ClientId,
        string? ClientSecret,
        DateTimeOffset? RegistrationExpiresAt,
        long? ClientIdIssuedAt,
        string? StartUrl,
        string? Region,
        IReadOnlyList<string> RegistrationScopes,
        string? Error)
    {
        public static SsoClientRegistrationOutcome Success(
            string clientId,
            string clientSecret,
            DateTimeOffset? registrationExpiresAt,
            long? clientIdIssuedAt,
            string? startUrl,
            string? region,
            IReadOnlyList<string> registrationScopes) =>
            new(
                clientId,
                clientSecret,
                registrationExpiresAt,
                clientIdIssuedAt,
                startUrl,
                region,
                registrationScopes,
                null);

        public static SsoClientRegistrationOutcome Failure(string error) =>
            new(null, null, null, null, null, null, [], error);
    }

    internal sealed record SsoTokenRefreshOutcome(
        string? AccessToken,
        DateTimeOffset? ExpiresAt,
        string? RefreshToken,
        string? ClientId,
        string? ClientSecret,
        DateTimeOffset? RegistrationExpiresAt,
        long? ClientIdIssuedAt,
        IReadOnlyList<string> RegistrationScopes,
        string? Error)
    {
        public static SsoTokenRefreshOutcome Success(
            string accessToken,
            DateTimeOffset? expiresAt,
            string? refreshToken,
            SsoClientRegistrationOutcome registration) =>
            new(
                accessToken,
                expiresAt,
                refreshToken,
                registration.ClientId,
                registration.ClientSecret,
                registration.RegistrationExpiresAt,
                registration.ClientIdIssuedAt,
                registration.RegistrationScopes,
                null);

        public static SsoTokenRefreshOutcome Failure(string error) =>
            new(null, null, null, null, null, null, null, [], error);
    }
}
