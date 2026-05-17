using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockEcsContainerResolver
{
    public const string DefaultEcsRelativeBase = "http://169.254.170.2";
    private const string EksPodIdentityHost = "169.254.170.23";

    public static async Task<BedrockCredentialProcessOutcome> ResolveAsync(
        BedrockOptions options,
        HttpClient httpClient,
        Func<DateTimeOffset> clock,
        CancellationToken cancellationToken = default)
    {
        var uri = ResolveCredentialUri(options, out var resolutionError);
        if (uri is null)
        {
            return resolutionError is null
                ? BedrockCredentialProcessOutcome.NotConfigured()
                : BedrockCredentialProcessOutcome.Failure(resolutionError);
        }

        if (!IsAllowedContainerEndpoint(uri, out var securityError))
        {
            return BedrockCredentialProcessOutcome.Failure(securityError ?? "ECS credentials endpoint is not allowed.");
        }

        string? authorizationToken;
        try
        {
            authorizationToken = await ResolveAuthorizationTokenAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"failed to read ECS authorization token file: {ex.Message}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(authorizationToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorizationToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"ECS credentials request failed: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)response.StatusCode}" : body.Trim();
                return BedrockCredentialProcessOutcome.Failure(
                    $"ECS credentials endpoint returned HTTP {(int)response.StatusCode}: {detail}");
            }

            return ParseEcsCredentialsJson(body, clock);
        }
    }

    internal static Uri? ResolveCredentialUri(BedrockOptions options, out string? error)
    {
        error = null;
        var fullUri = FirstNonEmpty(
            options.ContainerCredentialsFullUri,
            Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI"));
        if (!string.IsNullOrWhiteSpace(fullUri))
        {
            if (!Uri.TryCreate(fullUri, UriKind.Absolute, out var parsed))
            {
                error = $"AWS_CONTAINER_CREDENTIALS_FULL_URI is not a valid absolute URI: {fullUri}";
                return null;
            }

            return parsed;
        }

        var relativeUri = FirstNonEmpty(
            options.ContainerCredentialsRelativeUri,
            Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI"));
        if (!string.IsNullOrWhiteSpace(relativeUri))
        {
            if (!relativeUri!.StartsWith('/'))
            {
                relativeUri = "/" + relativeUri;
            }

            return new Uri(DefaultEcsRelativeBase + relativeUri, UriKind.Absolute);
        }

        return null;
    }

    internal static bool IsAllowedContainerEndpoint(Uri uri, out string? error)
    {
        error = null;
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = $"ECS credentials endpoint must use http or https, got '{uri.Scheme}'.";
            return false;
        }

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "ECS credentials endpoint host is missing.";
            return false;
        }

        if (string.Equals(host, "169.254.170.2", StringComparison.Ordinal) ||
            string.Equals(host, EksPodIdentityHost, StringComparison.Ordinal))
        {
            return true;
        }

        if (IsLoopbackHost(host))
        {
            return true;
        }

        error = $"ECS credentials HTTP endpoint host '{host}' is not allowed; use loopback, 169.254.170.2, 169.254.170.23, or HTTPS.";
        return false;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return IPAddress.IsLoopback(ip);
        }

        return false;
    }

    private static async Task<string?> ResolveAuthorizationTokenAsync(BedrockOptions options, CancellationToken cancellationToken)
    {
        var tokenFile = FirstNonEmpty(
            options.ContainerAuthorizationTokenFile,
            Environment.GetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE"));
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            var content = await File.ReadAllTextAsync(tokenFile, cancellationToken).ConfigureAwait(false);
            return content.Trim();
        }

        return FirstNonEmpty(
            options.ContainerAuthorizationToken,
            Environment.GetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN"));
    }

    internal static BedrockCredentialProcessOutcome ParseEcsCredentialsJson(string json, Func<DateTimeOffset> clock)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"ECS credentials response is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BedrockCredentialProcessOutcome.Failure("ECS credentials response must be a JSON object.");
            }

            var accessKeyId = GetString(document.RootElement, "AccessKeyId");
            var secretAccessKey = GetString(document.RootElement, "SecretAccessKey");
            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
            {
                return BedrockCredentialProcessOutcome.Failure(
                    "ECS credentials response is missing AccessKeyId or SecretAccessKey.");
            }

            var sessionToken = GetString(document.RootElement, "Token");
            DateTimeOffset? expiresAt = null;
            var expirationRaw = GetString(document.RootElement, "Expiration");
            if (!string.IsNullOrWhiteSpace(expirationRaw))
            {
                if (!DateTimeOffset.TryParse(
                        expirationRaw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    return BedrockCredentialProcessOutcome.Failure(
                        $"ECS credentials response has unparseable Expiration value: {expirationRaw}");
                }

                expiresAt = parsed;
                if (parsed <= clock())
                {
                    return BedrockCredentialProcessOutcome.Failure(
                        $"ECS credentials response returned already-expired credentials (Expiration={expirationRaw}).");
                }
            }

            return BedrockCredentialProcessOutcome.Success(new BedrockAwsCredentials(
                AccessKeyId: accessKeyId!,
                SecretAccessKey: secretAccessKey!,
                SessionToken: string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken,
                ExpiresAt: expiresAt,
                Source: "ecs"));
        }
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
