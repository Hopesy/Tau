using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tau.Ai.Providers.Google;

internal sealed class GoogleVertexAccessTokenResolver
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    private readonly HttpClient _httpClient;

    public GoogleVertexAccessTokenResolver(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string?> ResolveAsync(StreamOptions options, CancellationToken cancellationToken = default)
    {
        var explicitAccessToken = GetAccessToken(options);
        if (!string.IsNullOrWhiteSpace(explicitAccessToken))
        {
            return explicitAccessToken;
        }

        var credentialsPath = ResolveCredentialsFile(options);
        if (string.IsNullOrWhiteSpace(credentialsPath))
        {
            return null;
        }

        var credentials = LoadCredentials(credentialsPath);
        return credentials switch
        {
            GoogleServiceAccountCredentials serviceAccount => await ExchangeServiceAccountAsync(serviceAccount, cancellationToken).ConfigureAwait(false),
            GoogleAuthorizedUserCredentials authorizedUser => await ExchangeAuthorizedUserAsync(authorizedUser, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported Vertex ADC credentials.")
        };
    }

    public static string? ResolveProjectId(StreamOptions options)
    {
        var configured = GetProject(options);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var credentialsPath = ResolveCredentialsFile(options);
        if (string.IsNullOrWhiteSpace(credentialsPath))
        {
            return null;
        }

        try
        {
            var credentials = LoadCredentials(credentialsPath);
            return credentials.ProjectId;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ResolveLocation(StreamOptions options) => GetLocation(options);

    public static bool HasCredentialsFile(StreamOptions options) => !string.IsNullOrWhiteSpace(ResolveCredentialsFile(options));

    private async Task<string> ExchangeServiceAccountAsync(
        GoogleServiceAccountCredentials credentials,
        CancellationToken cancellationToken)
    {
        var assertion = CreateServiceAccountAssertion(credentials);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion
        });

        return await ExchangeTokenAsync(credentials.TokenUri, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ExchangeAuthorizedUserAsync(
        GoogleAuthorizedUserCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret,
            ["refresh_token"] = credentials.RefreshToken
        });

        return await ExchangeTokenAsync(credentials.TokenUri, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ExchangeTokenAsync(
        string tokenUri,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = content
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Vertex ADC token exchange failed {(int)response.StatusCode}: {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new InvalidOperationException("Vertex ADC token exchange response did not include access_token.");
        }

        return tokenElement.GetString()!;
    }

    private static string CreateServiceAccountAssertion(GoogleServiceAccountCredentials credentials)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = BuildJson(writer =>
        {
            writer.WriteString("alg", "RS256");
            writer.WriteString("typ", "JWT");
        });
        var payload = BuildJson(writer =>
        {
            writer.WriteString("iss", credentials.ClientEmail);
            writer.WriteString("scope", CloudPlatformScope);
            writer.WriteString("aud", credentials.TokenUri);
            writer.WriteNumber("iat", now);
            writer.WriteNumber("exp", now + 3600);
        });

        var signingInput = $"{Base64UrlEncode(header)}.{Base64UrlEncode(payload)}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(NormalizePem(credentials.PrivateKey));
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static byte[] BuildJson(Action<Utf8JsonWriter> writeProperties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string NormalizePem(string privateKey)
    {
        return privateKey.Contains("\\n", StringComparison.Ordinal)
            ? privateKey.Replace("\\n", "\n", StringComparison.Ordinal)
            : privateKey;
    }

    private static GoogleAdcCredentials LoadCredentials(string credentialsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath));
        var root = document.RootElement;
        var type = RequiredString(root, "type", "ADC credentials type");
        return type switch
        {
            "service_account" => new GoogleServiceAccountCredentials(
                ClientEmail: RequiredString(root, "client_email", "service account client_email"),
                PrivateKey: RequiredString(root, "private_key", "service account private_key"),
                TokenUri: OptionalString(root, "token_uri") ?? DefaultTokenUri,
                ProjectId: OptionalString(root, "project_id")),
            "authorized_user" => new GoogleAuthorizedUserCredentials(
                ClientId: RequiredString(root, "client_id", "authorized user client_id"),
                ClientSecret: RequiredString(root, "client_secret", "authorized user client_secret"),
                RefreshToken: RequiredString(root, "refresh_token", "authorized user refresh_token"),
                TokenUri: OptionalString(root, "token_uri") ?? DefaultTokenUri,
                ProjectId: OptionalString(root, "quota_project_id")),
            _ => throw new InvalidOperationException($"Unsupported Vertex ADC credential type '{type}'. Supported types: service_account, authorized_user.")
        };
    }

    private static string? ResolveCredentialsFile(StreamOptions options)
    {
        var configured = GetCredentialsFile(options);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new InvalidOperationException($"Vertex ADC credentials file does not exist: {configured}");
        }

        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return File.Exists(envPath)
                ? envPath
                : throw new InvalidOperationException($"GOOGLE_APPLICATION_CREDENTIALS file does not exist: {envPath}");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        var adcPath = Path.Combine(home, ".config", "gcloud", "application_default_credentials.json");
        return File.Exists(adcPath) ? adcPath : null;
    }

    private static string RequiredString(JsonElement element, string propertyName, string displayName)
    {
        var value = OptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Vertex ADC credentials missing {displayName}.")
            : value;
    }

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? GetAccessToken(StreamOptions options) => options switch
    {
        GoogleVertexOptions vertexOptions => vertexOptions.AccessToken,
        GoogleVertexSimpleOptions vertexOptions => vertexOptions.AccessToken,
        _ => null
    };

    private static string? GetCredentialsFile(StreamOptions options) => options switch
    {
        GoogleVertexOptions vertexOptions => vertexOptions.CredentialsFile,
        GoogleVertexSimpleOptions vertexOptions => vertexOptions.CredentialsFile,
        _ => null
    };

    private static string? GetProject(StreamOptions options) => options switch
    {
        GoogleVertexOptions vertexOptions => vertexOptions.Project,
        GoogleVertexSimpleOptions vertexOptions => vertexOptions.Project,
        _ => null
    };

    private static string? GetLocation(StreamOptions options) => options switch
    {
        GoogleVertexOptions vertexOptions => vertexOptions.Location,
        GoogleVertexSimpleOptions vertexOptions => vertexOptions.Location,
        _ => null
    };
}

internal abstract record GoogleAdcCredentials(string? ProjectId);

internal sealed record GoogleServiceAccountCredentials(
    string ClientEmail,
    string PrivateKey,
    string TokenUri,
    string? ProjectId) : GoogleAdcCredentials(ProjectId);

internal sealed record GoogleAuthorizedUserCredentials(
    string ClientId,
    string ClientSecret,
    string RefreshToken,
    string TokenUri,
    string? ProjectId) : GoogleAdcCredentials(ProjectId);
