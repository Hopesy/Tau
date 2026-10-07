// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth;

/// <summary>【AI】【OAuth 协议】原生扩展凭据与 .NET 凭据之间的无损转换。</summary>
public static class OAuthCredentialJson
{
    /// <summary>【AI】【OAuth 协议】解析凭据及扩展字段，空刷新令牌也是有效值。</summary>
    /// <param name="value">原生 OAuth 对象。</param><returns>独立凭据快照。</returns>
    public static OAuthCredentials Read(JsonElement value)
    {
        var expires = value.TryGetProperty("expires", out var native) ? native : value.GetProperty("expiresAt");
        long? nativeExpiry = expires.ValueKind == JsonValueKind.Number ? expires.GetInt64() : null;
        var expiry = nativeExpiry is { } milliseconds ? ClampExpiry(milliseconds) : DateTimeOffset.Parse(expires.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (IsReserved(field.Name)) continue;
            if (field.Value.ValueKind == JsonValueKind.String) metadata[field.Name] = field.Value.GetString()!;
            else properties[field.Name] = field.Value.Clone();
        }
        return new() { Access = value.GetProperty("access").GetString() ?? throw new JsonException("OAuth access must be a string."),
            Refresh = value.GetProperty("refresh").GetString() ?? throw new JsonException("OAuth refresh must be a string."),
            ExpiresAt = expiry, ExpiresUnixTimeMilliseconds = nativeExpiry, Metadata = metadata, Properties = properties };
    }

    /// <summary>【AI】【OAuth 协议】输出原生毫秒过期时间和未丢失类型的扩展字段。</summary>
    /// <param name="credentials">凭据。</param><returns>包含 type 的原生对象。</returns>
    public static JsonElement Write(OAuthCredentials credentials)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "oauth");
            writer.WriteString("access", credentials.Access);
            writer.WriteString("refresh", credentials.Refresh);
            writer.WriteNumber("expires", GetExpiryMilliseconds(credentials));
            foreach (var (key, value) in credentials.Metadata)
                if (!IsReserved(key)) writer.WriteString(key, value);
            foreach (var (key, value) in credentials.Properties)
                if (!IsReserved(key) && !credentials.Metadata.ContainsKey(key)) { writer.WritePropertyName(key); value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>【AI】【OAuth 协议】原生期限超出 .NET 范围时，仅限制日期视图，原毫秒值仍单独保留。</summary>
    /// <param name="milliseconds">Unix 毫秒值。</param><returns>可表示的日期。</returns>
    internal static DateTimeOffset ClampExpiry(long milliseconds) => milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ? DateTimeOffset.MaxValue
        : milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() ? DateTimeOffset.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    /// <summary>【AI】【OAuth 协议】日期未被调用方更新时保留原生期限，日期已更新则避免沿用旧值。</summary>
    /// <param name="credentials">凭据。</param><returns>有效的 Unix 毫秒期限。</returns>
    internal static long GetExpiryMilliseconds(OAuthCredentials credentials) => credentials.ExpiresUnixTimeMilliseconds is { } raw && ClampExpiry(raw) == credentials.ExpiresAt
        ? raw : credentials.ExpiresAt.ToUnixTimeMilliseconds();

    /// <summary>【AI】【OAuth 协议】防止附加字段覆盖认证控制字段。</summary>
    /// <param name="name">字段名称。</param><returns>是否为保留名称。</returns>
    internal static bool IsReserved(string name) => name.Equals("type", StringComparison.OrdinalIgnoreCase) || name.Equals("access", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("refresh", StringComparison.OrdinalIgnoreCase) || name.Equals("expires", StringComparison.OrdinalIgnoreCase) || name.Equals("expiresAt", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("key", StringComparison.OrdinalIgnoreCase) || name.Equals("apiKey", StringComparison.OrdinalIgnoreCase);
}
