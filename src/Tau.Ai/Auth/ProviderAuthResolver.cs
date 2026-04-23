using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Auth;

public sealed class ProviderAuthResolver
{
    private readonly OAuthProviderRegistry _oauthProviders;
    private readonly OAuthCredentialStore _credentialStore;

    public ProviderAuthResolver(
        OAuthProviderRegistry? oauthProviders = null,
        OAuthCredentialStore? credentialStore = null)
    {
        _oauthProviders = oauthProviders ?? new OAuthProviderRegistry();
        _credentialStore = credentialStore ?? new OAuthCredentialStore();
    }

    public string? ResolveApiKey(string provider, string? explicitApiKey = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitApiKey))
        {
            return explicitApiKey;
        }

        var envApiKey = EnvironmentApiKeyResolver.GetApiKey(provider);
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            return envApiKey;
        }

        var authEntries = _credentialStore.LoadEntries();
        if (!authEntries.TryGetValue(provider, out var entry))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            return entry.ApiKey;
        }

        var oauthCredentials = entry.OAuth;
        if (oauthCredentials is null)
        {
            return null;
        }

        var oauthProvider = _oauthProviders.TryGet(provider);
        if (oauthProvider is null)
        {
            return null;
        }

        if (oauthCredentials.IsExpired())
        {
            try
            {
                oauthCredentials = oauthProvider.RefreshTokenAsync(oauthCredentials).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        return oauthProvider.GetApiKey(oauthCredentials);
    }

    public Model ResolveModel(Model model)
    {
        var credentials = _credentialStore.LoadEntries();
        if (!credentials.TryGetValue(model.Provider, out var entry) || entry.OAuth is null)
        {
            return model;
        }

        var provider = _oauthProviders.TryGet(model.Provider);
        return provider?.ModifyModel(model, entry.OAuth) ?? model;
    }
}
