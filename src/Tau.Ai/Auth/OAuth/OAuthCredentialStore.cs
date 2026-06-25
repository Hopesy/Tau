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

    public void Save(string providerId, OAuthCredentials credentials)
    {
        var path = _searchPaths.FirstOrDefault(File.Exists) ?? _searchPaths.First();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Dictionary<string, JsonElement> existing = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        existing[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch
            {
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            foreach (var (key, value) in existing)
            {
                if (string.Equals(key, providerId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }

            writer.WritePropertyName(providerId);
            writer.WriteStartObject();
            writer.WriteString("type", "oauth");
            writer.WriteString("refresh", credentials.Refresh);
            writer.WriteString("access", credentials.Access);
            writer.WriteString("expiresAt", credentials.ExpiresAt.ToString("O"));
            foreach (var (metaKey, metaValue) in credentials.Metadata)
            {
                if (IsReservedCredentialProperty(metaKey))
                {
                    continue;
                }

                writer.WriteString(metaKey, metaValue);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        WriteAuthFile(path, stream.ToArray());
    }

    public bool Remove(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        var path = _searchPaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return false;
        }

        Dictionary<string, JsonElement> existing = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                existing[prop.Name] = prop.Value.Clone();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }

        var removed = false;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            foreach (var (key, value) in existing)
            {
                if (string.Equals(key, providerId, StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    continue;
                }

                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        if (!removed)
        {
            return false;
        }

        WriteAuthFile(path, stream.ToArray());
        return true;
    }

    public IReadOnlyDictionary<string, StoredProviderAuth> LoadEntries()
    {
        var path = _searchPaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return new Dictionary<string, StoredProviderAuth>(StringComparer.OrdinalIgnoreCase);
        }

        using var doc = LoadDocument(path);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
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

    private static JsonDocument? LoadDocument(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static StoredProviderAuth? ParseEntry(JsonElement element)
    {
        return ParseApiKeyEntry(element) ??
               ParseOauthEntry(element);
    }

    private static StoredProviderAuth? ParseApiKeyEntry(JsonElement element)
    {
        var env = ParseEnv(element);
        if (element.TryGetProperty("type", out var typeProp) &&
            typeProp.ValueKind == JsonValueKind.String)
        {
            var type = typeProp.GetString();
            if (string.Equals(type, "api_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "apiKey", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetString(element, "key", out var key) || TryGetString(element, "apiKey", out key))
                {
                    return new StoredProviderAuth { ApiKey = key, Env = env };
                }

                return null;
            }
        }

        if (TryGetString(element, "key", out var implicitKey) && !element.TryGetProperty("access", out _))
        {
            return new StoredProviderAuth { ApiKey = implicitKey, Env = env };
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

    private static bool IsReservedCredentialProperty(string propertyName) =>
        propertyName.Equals("type", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("refresh", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("access", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("expires", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("expiresAt", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("key", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("apiKey", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("env", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string>? ParseEnv(JsonElement element)
    {
        if (!element.TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in env.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = prop.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[prop.Name] = value!;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static void WriteAuthFile(string path, byte[] content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, content);
            return;
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, content);
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tempPath, path, overwrite: true);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
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
