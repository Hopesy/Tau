namespace Tau.Ai.Auth.OAuth;

public static class OAuthHelpers
{
    public static Task<OAuthCredentials> RefreshOAuthTokenAsync(
        OAuthProviderRegistry registry,
        string providerId,
        OAuthCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(credentials);

        return registry.Get(providerId).RefreshTokenAsync(credentials, cancellationToken);
    }

    public static async Task<OAuthApiKeyResult?> GetOAuthApiKeyAsync(
        OAuthProviderRegistry registry,
        string providerId,
        IReadOnlyDictionary<string, OAuthCredentials> credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!credentials.TryGetValue(providerId, out var providerCredentials))
        {
            return null;
        }

        var provider = registry.Get(providerId);
        if (providerCredentials.IsExpired())
        {
            providerCredentials = await provider
                .RefreshTokenAsync(providerCredentials, cancellationToken)
                .ConfigureAwait(false);
        }

        return new OAuthApiKeyResult(
            providerCredentials,
            provider.GetApiKey(providerCredentials));
    }

    public static OAuthCredentials RefreshOAuthToken(
        OAuthProviderRegistry registry,
        string providerId,
        OAuthCredentials credentials) =>
        RefreshOAuthTokenAsync(registry, providerId, credentials)
            .GetAwaiter()
            .GetResult();

    public static OAuthApiKeyResult? GetOAuthApiKey(
        OAuthProviderRegistry registry,
        string providerId,
        IReadOnlyDictionary<string, OAuthCredentials> credentials) =>
        GetOAuthApiKeyAsync(registry, providerId, credentials)
            .GetAwaiter()
            .GetResult();
}
