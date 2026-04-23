namespace Tau.Ai.Auth.OAuth;

public sealed record StoredProviderAuth
{
    public string? ApiKey { get; init; }
    public OAuthCredentials? OAuth { get; init; }
}
