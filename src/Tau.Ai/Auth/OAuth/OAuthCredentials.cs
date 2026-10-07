using System.Text.Json;

namespace Tau.Ai.Auth.OAuth;

public sealed record OAuthCredentials
{
    public required string Refresh { get; init; }
    public required string Access { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>【AI】【OAuth 凭据】保留原生毫秒期限，包括超出 .NET 日期范围的长期凭据。</summary>
    public long? ExpiresUnixTimeMilliseconds { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>【AI】【OAuth 凭据】保留扩展定义的对象、数组及标量，往返存储时不丢失类型。</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>【AI】【OAuth 凭据】按原生期限判断过期，极早日期不执行可能越界的日期减法。</summary>
    /// <param name="skew">提前刷新时间，默认五分钟。</param><returns>是否需要刷新。</returns>
    public bool IsExpired(TimeSpan? skew = null)
    {
        var effectiveSkew = skew ?? TimeSpan.FromMinutes(5);
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= OAuthCredentialJson.GetExpiryMilliseconds(this) - effectiveSkew.TotalMilliseconds;
    }
}
