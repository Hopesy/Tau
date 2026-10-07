using System.Text.Json;

namespace Tau.Ai.Auth.OAuth;

public sealed partial class OAuthCredentialStore : IProviderCredentialStore
{
    private readonly string[] _searchPaths;

    public OAuthCredentialStore(IEnumerable<string>? searchPaths = null)
    {
        _searchPaths = (searchPaths ?? GetDefaultSearchPaths()).Select(Path.GetFullPath).ToArray();
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

    /// <summary>【AI】【凭据保存】在跨进程锁内保存提供方的 OAuth 凭据。</summary>
    /// <param name="providerId">提供方标识。</param><param name="credentials">待保存凭据。</param>
    public void Save(string providerId, OAuthCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(credentials);
        var path = ResolveWritePath();
        using var lease = AcquireCredentialLockAsync(path, default).GetAwaiter().GetResult();
        SaveCore(path, providerId, credentials);
    }

    /// <summary>【AI】【凭据保存】保留其他条目并原子提交原生期限及兼容日期。</summary>
    /// <param name="path">凭据文件。</param><param name="providerId">提供方标识。</param><param name="credentials">待保存凭据。</param>
    private static void SaveCore(string path, string providerId, OAuthCredentials credentials)
    {
        var existing = ReadCredentialDocument(path);

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
            writer.WriteNumber("expires", OAuthCredentialJson.GetExpiryMilliseconds(credentials));
            foreach (var (metaKey, metaValue) in credentials.Metadata)
            {
                if (OAuthCredentialJson.IsReserved(metaKey))
                {
                    continue;
                }

                writer.WriteString(metaKey, metaValue);
            }
            foreach (var (key, value) in credentials.Properties)
            {
                if (OAuthCredentialJson.IsReserved(key) || credentials.Metadata.ContainsKey(key)) continue;
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        WriteAuthFile(path, stream.ToArray());
    }

    /// <summary>【AI】【凭据删除】在跨进程文件锁内删除提供方，避免并发刷新复活已退出的认证。</summary>
    /// <param name="providerId">提供方标识。</param><returns>是否删除已有凭据。</returns>
    public bool Remove(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        var path = _searchPaths.FirstOrDefault(File.Exists);
        if (path is null) return false;
        using var lease = AcquireCredentialLockAsync(path, default).GetAwaiter().GetResult();
        return RemoveCore(path, providerId);
    }

    /// <summary>【AI】【凭据删除】调用方已持锁时重写凭据文件，保留其他提供方条目。</summary>
    /// <param name="path">凭据路径。</param><param name="providerId">待删除标识。</param><param name="strict">是否报告损坏及读取错误。</param><returns>是否存在并删除目标。</returns>
    private static bool RemoveCore(string path, string providerId, bool strict = false)
    {
        Dictionary<string, JsonElement> existing = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                if (strict) throw new JsonException("Credential storage must contain an object.");
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                existing[prop.Name] = prop.Value.Clone();
            }
        }
        catch (FileNotFoundException) { return false; }
        catch (Exception ex) when (!strict && ex is IOException or UnauthorizedAccessException or JsonException)
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

                return new StoredProviderAuth { Env = env };
            }
        }

        if (TryGetString(element, "key", out var implicitKey) && !element.TryGetProperty("access", out _))
        {
            return new StoredProviderAuth { ApiKey = implicitKey, Env = env };
        }

        return null;
    }

    /// <summary>【AI】【凭据读取】解析 OAuth 字段并保留字符串、结构化扩展字段与原生期限。</summary>
    /// <param name="element">凭据对象。</param><returns>OAuth 条目，无访问和刷新字段时为空。</returns>
    private static StoredProviderAuth? ParseOauthEntry(JsonElement element)
    {
        if (!element.TryGetProperty("refresh", out var refreshValue) || refreshValue.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("access", out var accessValue) || accessValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var (expiresAt, nativeExpiry) = ParseExpiry(element);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (OAuthCredentialJson.IsReserved(prop.Name))
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                metadata[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
            else properties[prop.Name] = prop.Value.Clone();
        }

        return new StoredProviderAuth
        {
            OAuth = new OAuthCredentials
            {
                Refresh = refreshValue.GetString()!,
                Access = accessValue.GetString()!,
                ExpiresAt = expiresAt,
                ExpiresUnixTimeMilliseconds = nativeExpiry,
                Metadata = metadata,
                Properties = properties
            }
        };
    }

    /// <summary>【AI】【凭据期限】优先读取原生 expires，同时兼容旧文件的 expiresAt 日期。</summary>
    /// <param name="element">凭据对象。</param><returns>可表示日期及原始毫秒值。</returns>
    private static (DateTimeOffset Date, long? Milliseconds) ParseExpiry(JsonElement element)
    {
        foreach (var name in new[] { "expires", "expiresAt" })
        {
            if (!element.TryGetProperty(name, out var expiry)) continue;
            if (expiry.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(expiry.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)) return (date, null);
            if (expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out var milliseconds))
            {
                var dateView = OAuthCredentialJson.ClampExpiry(milliseconds);
                // 1. 【AI】【凭据期限】兼容日期和原生期限指向同一毫秒时，保留旧 .NET 文件的更细时间精度
                if (name == "expires" && element.TryGetProperty("expiresAt", out var legacy) && legacy.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(legacy.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var precise)
                    && precise.ToUnixTimeMilliseconds() == milliseconds) dateView = precise;
                return (dateView, milliseconds);
            }
        }
        return (DateTimeOffset.UtcNow.AddMinutes(-1), null);
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

    /// <summary>【AI】【凭据提交】通过同目录临时文件原子替换，短暂共享冲突时保留完整旧文件并重试。</summary>
    /// <param name="path">凭据目标。</param><param name="content">完整新内容。</param>
    private static void WriteAuthFile(string path, byte[] content)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, content);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // 1. 【AI】【凭据提交】Windows 读取者或扫描器可能短暂禁止替换，不能退化为截断写入
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tempPath, path, overwrite: true); break; }
                catch (Exception error) when (attempt < 20 && IsTransientCredentialReplaceError(error, path))
                { Thread.Sleep(25); }
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>【AI】【凭据提交】只重试 Windows 共享冲突及非只读文件的短暂访问拒绝。</summary>
    /// <param name="error">替换异常。</param><param name="path">原目标。</param><returns>是否适合有界重试。</returns>
    private static bool IsTransientCredentialReplaceError(Exception error, string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (error is IOException && (error.HResult & 0xffff) is 32 or 33) return true;
        if (error is not UnauthorizedAccessException || (error.HResult & 0xffff) != 5) return false;
        try { return File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
