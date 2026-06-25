using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Ai.Registry;

namespace Tau.Ai.Auth;

public sealed class ProviderAuthResolver
{
    private readonly OAuthProviderRegistry _oauthProviders;
    private readonly OAuthCredentialStore _credentialStore;
    private readonly ITauLogSink _logSink;
    private readonly ModelConfigurationStore _configurationStore;

    public ProviderAuthResolver(
        OAuthProviderRegistry? oauthProviders = null,
        OAuthCredentialStore? credentialStore = null,
        ITauLogSink? logSink = null,
        ModelConfigurationStore? configurationStore = null)
    {
        _oauthProviders = oauthProviders ?? new OAuthProviderRegistry();
        _credentialStore = credentialStore ?? new OAuthCredentialStore();
        _logSink = logSink ?? NullTauLogSink.Instance;
        _configurationStore = configurationStore ?? new ModelConfigurationStore();
    }

    public ProviderAuthStatus GetStatus(
        Model model,
        string? explicitApiKey = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        return GetStatus(model.Provider, model, explicitApiKey, env);
    }

    public ProviderAuthStatus GetStatus(
        string provider,
        Model? model = null,
        string? explicitApiKey = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var status = ResolveStatus(provider, model, explicitApiKey, env);
        _logSink.Log(new TauLogEvent(
            "auth",
            "status.checked",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["provider"] = provider,
                ["configured"] = status.IsConfigured ? "true" : "false",
                ["source"] = status.Source,
                ["usesOAuth"] = status.UsesOAuth ? "true" : "false",
                ["canLogin"] = status.CanLogin ? "true" : "false"
            }));
        return status;
    }

    private ProviderAuthStatus ResolveStatus(
        string provider,
        Model? model = null,
        string? explicitApiKey = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitApiKey))
        {
            return new ProviderAuthStatus(provider, true, "explicit", false, false, "API key provided explicitly for this request.");
        }

        var authEntries = _credentialStore.LoadEntries();
        if (authEntries.TryGetValue(provider, out var entry))
        {
            if (!string.IsNullOrWhiteSpace(entry.ApiKey))
            {
                return new ProviderAuthStatus(provider, true, "auth.json api_key", false, false, "API key entry found in auth.json.");
            }

            if (entry.OAuth is not null)
            {
                var oauthProvider = _oauthProviders.TryGet(provider);
                if (oauthProvider is null)
                {
                    return new ProviderAuthStatus(provider, false, "auth.json oauth", true, false, "OAuth credentials exist, but no OAuth provider is registered for this provider.");
                }

                if (entry.OAuth.IsExpired())
                {
                    return new ProviderAuthStatus(provider, false, "auth.json oauth", true, true, "OAuth credentials exist but are expired; refresh/login flow is available for this provider.");
                }

                return new ProviderAuthStatus(provider, true, "auth.json oauth", true, true, "OAuth credentials found in auth.json.");
            }
        }

        var envApiKey = EnvironmentApiKeyResolver.GetApiKey(provider, env);
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            var source = EnvironmentApiKeyResolver.IsAuthenticatedMarker(envApiKey) ? "environment/ambient" : "environment";
            return new ProviderAuthStatus(provider, true, source, false, false, "Credentials are available from environment or ambient provider credentials.");
        }

        var requestConfig = model is null
            ? _configurationStore.InspectProviderConfigurationStatus(provider)
            : _configurationStore.InspectRequestConfigurationStatus(model);
        if (requestConfig.IsConfigured)
        {
            var detail = requestConfig.HasCommandBackedSecret
                ? "models.json contains command-backed credential configuration; status checks do not execute it or reveal its value."
                : "models.json contains request credential configuration; status checks do not reveal its value.";
            return new ProviderAuthStatus(provider, true, "models.json", false, false, detail);
        }

        var canLogin = _oauthProviders.TryGet(provider) is not null;
        var message = canLogin
            ? "No credentials found. OAuth login is available for this provider; use the login flow, environment variables, auth.json, or models.json."
            : "No credentials found. Use environment variables, auth.json, or models.json provider configuration.";
        return new ProviderAuthStatus(provider, false, "none", false, canLogin, message);
    }

    public string? ResolveApiKey(
        string provider,
        string? explicitApiKey = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitApiKey))
        {
            return explicitApiKey;
        }

        var authEntries = _credentialStore.LoadEntries();
        if (authEntries.TryGetValue(provider, out var entry))
        {
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
                    _credentialStore.Save(provider, oauthCredentials);
                }
                catch (Exception ex)
                {
                    throw new ProviderAuthException("oauth", $"OAuth refresh failed for {provider}.", ex);
                }
            }

            try
            {
                return oauthProvider.GetApiKey(oauthCredentials);
            }
            catch (Exception ex)
            {
                throw new ProviderAuthException("oauth", $"OAuth auth derivation failed for {provider}.", ex);
            }
        }

        var envApiKey = EnvironmentApiKeyResolver.GetApiKey(provider, env);
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            return envApiKey;
        }

        return null;
    }

    internal StoredProviderAuth? GetStoredAuthEntry(string provider)
    {
        var authEntries = _credentialStore.LoadEntries();
        return authEntries.TryGetValue(provider, out var entry) ? entry : null;
    }

    public Model ResolveModel(Model model)
    {
        var credentials = _credentialStore.LoadEntries();
        if (!credentials.TryGetValue(model.Provider, out var entry) || entry.OAuth is null)
        {
            return model;
        }

        var provider = _oauthProviders.TryGet(model.Provider);
        if (provider is null)
        {
            return model;
        }

        try
        {
            return provider.ModifyModel(model, entry.OAuth);
        }
        catch (Exception ex)
        {
            throw new ProviderAuthException("oauth", $"OAuth model derivation failed for {model.Provider}.", ex);
        }
    }

    public IOAuthProvider? GetOAuthProvider(string providerId) => _oauthProviders.TryGet(providerId);

    public void SaveOAuthCredentials(string providerId, OAuthCredentials credentials) =>
        _credentialStore.Save(providerId, credentials);

    public bool Logout(string providerId)
    {
        var removed = _credentialStore.Remove(providerId);
        _logSink.Log(new TauLogEvent(
            "auth",
            "logout",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["provider"] = providerId,
                ["removed"] = removed ? "true" : "false"
            }));
        return removed;
    }
}
