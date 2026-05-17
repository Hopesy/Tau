using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tau.Ai.Providers.Bedrock;

internal static class BedrockInstanceMetadataResolver
{
    public const string DefaultEndpoint = "http://169.254.169.254";
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(1);

    private const string TokenPath = "/latest/api/token";
    private const string CredentialsBasePath = "/latest/meta-data/iam/security-credentials/";
    private const string TokenTtlHeader = "X-aws-ec2-metadata-token-ttl-seconds";
    private const string TokenHeader = "X-aws-ec2-metadata-token";

    public static async Task<BedrockCredentialProcessOutcome> ResolveAsync(
        BedrockOptions options,
        HttpClient httpClient,
        Func<DateTimeOffset> clock,
        CancellationToken cancellationToken = default)
    {
        if (IsDisabled(options))
        {
            return BedrockCredentialProcessOutcome.NotConfigured();
        }

        if (!TryResolveEndpoint(options, out var endpoint, out var endpointError))
        {
            return BedrockCredentialProcessOutcome.Failure(endpointError ?? "Invalid IMDS endpoint configuration.");
        }

        if (!IsAllowedImdsEndpoint(endpoint, out var allowError))
        {
            return BedrockCredentialProcessOutcome.Failure(allowError ?? "IMDS endpoint is not allowed.");
        }

        var requestTimeout = ResolveTimeout(options);
        var v1Disabled = IsV1Disabled(options);

        string? token = null;
        var (tokenStatus, tokenBody, tokenException) = await TryGetTokenAsync(
            httpClient,
            endpoint,
            requestTimeout,
            cancellationToken).ConfigureAwait(false);

        if (tokenException is not null)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"IMDS token request failed: {tokenException.Message}");
        }

        if (tokenStatus.HasValue && (int)tokenStatus.Value >= 200 && (int)tokenStatus.Value < 300)
        {
            token = tokenBody?.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                return BedrockCredentialProcessOutcome.Failure("IMDS token response was empty.");
            }
        }
        else if (!v1Disabled)
        {
            token = null;
        }
        else
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"IMDS token request returned HTTP {(int?)tokenStatus ?? -1} and IMDSv1 fallback is disabled.");
        }

        var roleEndpoint = CombineUri(endpoint, CredentialsBasePath);
        var (roleResponseStatus, roleBody, roleException) = await TryGetAsync(
            httpClient,
            roleEndpoint,
            token,
            requestTimeout,
            cancellationToken).ConfigureAwait(false);

        if (roleException is not null)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"IMDS role lookup failed: {roleException.Message}");
        }

        if (!roleResponseStatus.HasValue || (int)roleResponseStatus.Value < 200 || (int)roleResponseStatus.Value >= 300)
        {
            var detail = string.IsNullOrWhiteSpace(roleBody) ? $"HTTP {(int?)roleResponseStatus ?? -1}" : roleBody!.Trim();
            return BedrockCredentialProcessOutcome.Failure(
                $"IMDS role lookup returned HTTP {(int?)roleResponseStatus ?? -1}: {detail}");
        }

        var roleName = (roleBody ?? "").Trim();
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return BedrockCredentialProcessOutcome.Failure("IMDS role lookup returned an empty role name.");
        }

        if (roleName.Contains('\n', StringComparison.Ordinal) || roleName.Contains('\r', StringComparison.Ordinal))
        {
            // Some instance profiles list multiple roles; only the first line is used.
            var newlineIndex = roleName.IndexOfAny(['\r', '\n']);
            roleName = roleName[..newlineIndex].Trim();
        }

        var credentialsEndpoint = CombineUri(endpoint, CredentialsBasePath + Uri.EscapeDataString(roleName));
        var (credsResponseStatus, credsBody, credsException) = await TryGetAsync(
            httpClient,
            credentialsEndpoint,
            token,
            requestTimeout,
            cancellationToken).ConfigureAwait(false);

        if (credsException is not null)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"IMDS credentials request failed: {credsException.Message}");
        }

        if (!credsResponseStatus.HasValue || (int)credsResponseStatus.Value < 200 || (int)credsResponseStatus.Value >= 300)
        {
            var detail = string.IsNullOrWhiteSpace(credsBody) ? $"HTTP {(int?)credsResponseStatus ?? -1}" : credsBody!.Trim();
            return BedrockCredentialProcessOutcome.Failure(
                $"IMDS credentials returned HTTP {(int?)credsResponseStatus ?? -1}: {detail}");
        }

        return ParseInstanceCredentialsJson(credsBody ?? "", clock);
    }

    internal static BedrockCredentialProcessOutcome ParseInstanceCredentialsJson(string json, Func<DateTimeOffset> clock)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return BedrockCredentialProcessOutcome.Failure(
                $"IMDS credentials response is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BedrockCredentialProcessOutcome.Failure(
                    "IMDS credentials response must be a JSON object.");
            }

            if (document.RootElement.TryGetProperty("Code", out var codeProperty) &&
                codeProperty.ValueKind == JsonValueKind.String &&
                !string.Equals(codeProperty.GetString(), "Success", StringComparison.Ordinal))
            {
                var message = document.RootElement.TryGetProperty("Message", out var messageProperty)
                    ? messageProperty.GetString()
                    : null;
                return BedrockCredentialProcessOutcome.Failure(
                    string.IsNullOrWhiteSpace(message)
                        ? $"IMDS reported credentials error code: {codeProperty.GetString()}"
                        : $"IMDS credentials error {codeProperty.GetString()}: {message}");
            }

            var accessKeyId = GetString(document.RootElement, "AccessKeyId");
            var secretAccessKey = GetString(document.RootElement, "SecretAccessKey");
            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
            {
                return BedrockCredentialProcessOutcome.Failure(
                    "IMDS credentials response is missing AccessKeyId or SecretAccessKey.");
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
                        $"IMDS credentials response has unparseable Expiration value: {expirationRaw}");
                }

                expiresAt = parsed;
                if (parsed <= clock())
                {
                    return BedrockCredentialProcessOutcome.Failure(
                        $"IMDS credentials response returned already-expired credentials (Expiration={expirationRaw}).");
                }
            }

            return BedrockCredentialProcessOutcome.Success(new BedrockAwsCredentials(
                AccessKeyId: accessKeyId!,
                SecretAccessKey: secretAccessKey!,
                SessionToken: string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken,
                ExpiresAt: expiresAt,
                Source: "ec2_instance_metadata"));
        }
    }

    internal static bool IsAllowedImdsEndpoint(Uri uri, out string? error)
    {
        error = null;
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = $"IMDS endpoint must use http or https, got '{uri.Scheme}'.";
            return false;
        }

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "IMDS endpoint host is missing.";
            return false;
        }

        if (string.Equals(host, "169.254.169.254", StringComparison.Ordinal) ||
            string.Equals(host, "fd00:ec2::254", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
        {
            return true;
        }

        error = $"IMDS HTTP endpoint host '{host}' is not allowed; use 169.254.169.254, loopback, or HTTPS.";
        return false;
    }

    private static bool IsDisabled(BedrockOptions options)
    {
        if (options.Ec2MetadataDisabled is false)
        {
            return false;
        }

        if (options.Ec2MetadataDisabled is true)
        {
            return true;
        }

        var raw = Environment.GetEnvironmentVariable("AWS_EC2_METADATA_DISABLED");
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsV1Disabled(BedrockOptions options)
    {
        if (options.Ec2MetadataV1Disabled is true)
        {
            return true;
        }

        var raw = Environment.GetEnvironmentVariable("AWS_EC2_METADATA_V1_DISABLED");
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveEndpoint(BedrockOptions options, out Uri endpoint, out string? error)
    {
        error = null;
        var raw = FirstNonEmpty(
            options.Ec2MetadataServiceEndpoint,
            Environment.GetEnvironmentVariable("AWS_EC2_METADATA_SERVICE_ENDPOINT"));
        if (string.IsNullOrWhiteSpace(raw))
        {
            endpoint = new Uri(DefaultEndpoint, UriKind.Absolute);
            return true;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            endpoint = new Uri(DefaultEndpoint, UriKind.Absolute);
            error = $"AWS_EC2_METADATA_SERVICE_ENDPOINT is not a valid absolute URI: {raw}";
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static TimeSpan ResolveTimeout(BedrockOptions options)
    {
        if (options.Ec2MetadataServiceTimeout is { } explicitTimeout && explicitTimeout > TimeSpan.Zero)
        {
            return explicitTimeout;
        }

        var raw = Environment.GetEnvironmentVariable("AWS_METADATA_SERVICE_TIMEOUT");
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return DefaultRequestTimeout;
    }

    private static Uri CombineUri(Uri baseUri, string path)
    {
        var basePart = baseUri.GetLeftPart(UriPartial.Authority);
        return new Uri(basePart + path, UriKind.Absolute);
    }

    private static async Task<(HttpStatusCode? Status, string? Body, Exception? Exception)> TryGetTokenAsync(
        HttpClient httpClient,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, CombineUri(endpoint, TokenPath));
            request.Headers.TryAddWithoutValidation(TokenTtlHeader, "21600");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return (response.StatusCode, body, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex);
        }
    }

    private static async Task<(HttpStatusCode? Status, string? Body, Exception? Exception)> TryGetAsync(
        HttpClient httpClient,
        Uri uri,
        string? token,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.TryAddWithoutValidation(TokenHeader, token);
            }
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return (response.StatusCode, body, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex);
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
