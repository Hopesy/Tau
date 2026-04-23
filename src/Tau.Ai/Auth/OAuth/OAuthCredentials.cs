namespace Tau.Ai.Auth.OAuth;

public sealed record OAuthCredentials
{
    public required string Refresh { get; init; }
    public required string Access { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsExpired(TimeSpan? skew = null)
    {
        var effectiveSkew = skew ?? TimeSpan.FromMinutes(5);
        return DateTimeOffset.UtcNow >= ExpiresAt - effectiveSkew;
    }
}
