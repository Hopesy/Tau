using System.Text.Json;

namespace Tau.Ai.Auth.OAuth;

public sealed class OAuthCredentialStore
{
    private readonly string[] _searchPaths;

    public OAuthCredentialStore(IEnumerable<string>? searchPaths = null)
    {
        _searchPaths = searchPaths?.ToArray() ?? GetDefaultSearchPaths().ToArray();
    }

    public IReadOnlyDictionary<string, OAuthCredentials> Load()
    {
        return LoadEntries()
            .Where(pair => pair.Value.OAuth is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OAuth!,
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, StoredProviderAuth> LoadEntries()
    {
        var path = _searchPaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return new Dictionary<string, StoredProviderAuth>(StringComparer.OrdinalIgnoreCase);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, StoredProviderAuth>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, StoredProviderAuth>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerProp in doc.RootElement.EnumerateObject())
        {
            if (providerProp.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var credential = ParseEntry(providerProp.Value);
            if (credential is not null)
            {
                result[providerProp.Name] = credential;
            }
        }

        return result;
    }

    private static StoredProviderAuth? ParseEntry(JsonElement element)
    {
        return ParseApiKeyEntry(element) ??
               ParseOauthEntry(element);
    }

    private static StoredProviderAuth? ParseApiKeyEntry(JsonElement element)
    {
        if (element.TryGetProperty("type", out var typeProp) &&
            typeProp.ValueKind == JsonValueKind.String)
        {
            var type = typeProp.GetString();
            if (string.Equals(type, "api_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "apiKey", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetString(element, "key", out var key) || TryGetString(element, "apiKey", out key))
                {
                    return new StoredProviderAuth { ApiKey = key };
                }

                return null;
            }
        }

        if (TryGetString(element, "key", out var implicitKey) && !element.TryGetProperty("access", out _))
        {
            return new StoredProviderAuth { ApiKey = implicitKey };
        }

        return null;
    }

    private static StoredProviderAuth? ParseOauthEntry(JsonElement element)
    {
        if (!TryGetString(element, "refresh", out var refresh) || !TryGetString(element, "access", out var access))
        {
            return null;
        }

        var expiresAt = ParseExpiry(element);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.NameEquals("refresh") || prop.NameEquals("access") || prop.NameEquals("expires") || prop.NameEquals("expiresAt"))
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                metadata[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return new StoredProviderAuth
        {
            OAuth = new OAuthCredentials
            {
                Refresh = refresh!,
                Access = access!,
                ExpiresAt = expiresAt,
                Metadata = metadata
            }
        };
    }

    private static DateTimeOffset ParseExpiry(JsonElement element)
    {
        if (element.TryGetProperty("expiresAt", out var expiresAtProp))
        {
            if (expiresAtProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expiresAtProp.GetString(), out var expiresAt))
            {
                return expiresAt;
            }

            if (expiresAtProp.ValueKind == JsonValueKind.Number && expiresAtProp.TryGetInt64(out var unixMs))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            }
        }

        if (element.TryGetProperty("expires", out var expiresProp))
        {
            if (expiresProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expiresProp.GetString(), out var expires))
            {
                return expires;
            }

            if (expiresProp.ValueKind == JsonValueKind.Number && expiresProp.TryGetInt64(out var unixMs))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            }
        }

        return DateTimeOffset.UtcNow.AddMinutes(-1);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static IEnumerable<string> GetDefaultSearchPaths()
    {
        var configured = Environment.GetEnvironmentVariable("TAU_AUTH_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        yield return Path.Combine(Directory.GetCurrentDirectory(), ".tau", "auth.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".tau", "auth.json");
        }
    }
}
