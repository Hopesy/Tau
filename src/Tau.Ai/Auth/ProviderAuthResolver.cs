using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Ai.Registry;

namespace Tau.Ai.Auth;

public sealed partial class ProviderAuthResolver
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

    /// <summary>【AI】【会话认证】保留凭据来源，为独立会话绑定自己的配置覆盖。</summary>
    /// <param name="configurationStore">会话配置存储。</param><returns>独立认证解析器。</returns>
    public ProviderAuthResolver WithConfigurationStore(ModelConfigurationStore configurationStore) =>
        new(_oauthProviders, _credentialStore, _logSink, configurationStore);

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
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyDictionary<string, StoredProviderAuth>? storedEntries = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitApiKey))
        {
            return new ProviderAuthStatus(provider, true, "explicit", false, false, "API key provided explicitly for this request.");
        }

        // 1. 【AI】【认证环境】预检使用与请求相同的模型环境，读取状态时不执行命令型秘密
        env = _configurationStore.GetRequestEnvironment(model ?? new Model { Provider = provider, Id = "", Name = "", Api = "" }, env);
        var authEntries = storedEntries ?? _credentialStore.LoadEntries();
        var storedEntry = authEntries.GetValueOrDefault(provider);
        if (HasNativeAuthentication(provider) && GetApiKeyProvider(provider) is null && storedEntry?.OAuth is null)
            return new(provider, false, "none", false, GetOAuthProvider(provider) is not null, "The native provider does not support API key authentication.");
        if (storedEntry?.OAuth is null && GetApiKeyProvider(provider) is { } apiKeyProvider)
        {
            var status = apiKeyProvider.CheckAsync?.Invoke(new(provider, storedEntry is null ? null : new(storedEntry.ApiKey, storedEntry.Env), env, default)).GetAwaiter().GetResult()
                ?? new(provider, false, "none", false, apiKeyProvider.LoginAsync is not null, "Provider authentication is not configured.");
            return status with { CanLogin = status.CanLogin || GetOAuthProvider(provider) is not null };
        }
        if (authEntries.TryGetValue(provider, out var entry))
        {
            if (!string.IsNullOrWhiteSpace(entry.ApiKey))
            {
                return new ProviderAuthStatus(provider, true, "auth.json api_key", false, false, "API key entry found in auth.json.");
            }

            if (entry.OAuth is not null)
            {
                var oauthProvider = GetOAuthProvider(provider);
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

            // 2. 【AI】【云凭据状态】无密钥的配置文件或项目参数同样参与环境认证检查
            if (entry.Env is not null && EnvironmentApiKeyResolver.GetApiKey(provider, ProviderEnvironment.Merge(entry.Env, env)) is not null)
                return new(provider, true, "auth.json api_key", false, true, "Provider environment credentials found in auth.json.");
        }

        if (_configurationStore.HasRuntimeApiKey(provider))
        {
            var configured = _configurationStore.InspectProviderConfigurationStatus(provider);
            return new ProviderAuthStatus(provider, configured.HasApiKey, "extension", false, false, "Runtime API key configuration.");
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

        var canLogin = GetOAuthProvider(provider) is not null;
        var message = canLogin
            ? "No credentials found. OAuth login is available for this provider; use the login flow, environment variables, auth.json, or models.json."
            : "No credentials found. Use environment variables, auth.json, or models.json provider configuration.";
        return new ProviderAuthStatus(provider, false, "none", false, canLogin, message);
    }

    /// <summary>【AI】【请求密钥】通过统一认证解析获取密钥，必要时刷新 OAuth。</summary>
    /// <param name="provider">提供方。</param><param name="explicitApiKey">显式密钥。</param><param name="env">环境覆盖。</param><returns>请求密钥或空值。</returns>
    public string? ResolveApiKey(string provider, string? explicitApiKey = null, IReadOnlyDictionary<string, string>? env = null) =>
        ResolveRequestAuth(provider, explicitApiKey, env).ApiKey;

    /// <summary>【AI】【请求认证】按显式覆盖、保存凭据及提供方定义解析完整认证，保留认证归属。</summary>
    /// <param name="provider">提供方。</param><param name="explicitApiKey">显式密钥。</param><param name="env">环境覆盖。</param>
    /// <param name="token">刷新和解析的取消信号。</param><returns>完整请求认证。</returns>
    public ProviderAuthResult ResolveRequestAuth(
        string provider,
        string? explicitApiKey = null,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(explicitApiKey))
        {
            return new(ApiKey: explicitApiKey);
        }

        var authEntries = _credentialStore.LoadEntries();
        var storedEntry = authEntries.GetValueOrDefault(provider);
        if (HasNativeAuthentication(provider) && GetApiKeyProvider(provider) is null && storedEntry?.OAuth is null)
            return new() { SuppressConfiguredApiKey = true };
        if (storedEntry?.OAuth is null && GetApiKeyProvider(provider) is { } apiKeyProvider)
        {
            try
            {
                var configuredKey = storedEntry is null ? _configurationStore.ResolveProviderApiKey(provider, env) : null;
                var credential = storedEntry is not null ? new ApiKeyCredential(storedEntry.ApiKey, storedEntry.Env) : configuredKey is null ? null : new ApiKeyCredential(configuredKey);
                return (apiKeyProvider.ResolveAsync(new(provider, credential, env, token)).WaitAsync(token).GetAwaiter().GetResult() ?? new())
                    with { SuppressConfiguredApiKey = true };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { throw new ProviderAuthException("api_key", $"API key auth resolution failed for {provider}.", error); }
        }
        if (authEntries.TryGetValue(provider, out var entry))
        {
            if (!string.IsNullOrWhiteSpace(entry.ApiKey))
            {
                return new(ApiKey: entry.ApiKey, Env: entry.Env) { SuppressConfiguredApiKey = true };
            }

            var oauthCredentials = entry.OAuth;
            if (oauthCredentials is null)
            {
                // 1. 【AI】【云凭据请求】保留无密钥凭据里的配置文件和项目参数，供后续 SDK 解析环境认证
                return new(ApiKey: EnvironmentApiKeyResolver.GetApiKey(provider, ProviderEnvironment.Merge(entry.Env, env)), Env: entry.Env)
                    { SuppressConfiguredApiKey = true };
            }

            var oauthProvider = GetOAuthProvider(provider);
            if (oauthProvider is null)
            {
                return new() { SuppressConfiguredApiKey = true };
            }

            if (oauthCredentials.IsExpired())
            {
                try
                {
                    oauthCredentials = _credentialStore.RefreshAsync(provider, oauthProvider, token).GetAwaiter().GetResult();
                    if (oauthCredentials is null) return new() { SuppressConfiguredApiKey = true };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    throw new ProviderAuthException("oauth", $"OAuth refresh failed for {provider}.", ex);
                }
            }

            try
            {
                return oauthProvider.ResolveAuth(oauthCredentials, token) with { SuppressConfiguredApiKey = true };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new ProviderAuthException("oauth", $"OAuth auth derivation failed for {provider}.", ex);
            }
        }

        if (_configurationStore.HasRuntimeApiKey(provider)) return new(ApiKey: _configurationStore.ResolveProviderApiKey(provider, env));
        var envApiKey = EnvironmentApiKeyResolver.GetApiKey(provider, env);
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            return new(ApiKey: envApiKey);
        }

        return new(ApiKey: _configurationStore.ResolveProviderApiKey(provider, env));
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

        var provider = GetOAuthProvider(model.Provider);
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

    /// <summary>【AI】【OAuth 注册】原生提供方只使用显式声明的方式，兼容扩展允许回退内置 OAuth。</summary>
    /// <param name="providerId">提供方标识。</param><returns>有效 OAuth 实现。</returns>
    public IOAuthProvider? GetOAuthProvider(string providerId)
    {
        var runtime = Volatile.Read(ref _runtimeAuthentication);
        return runtime.OAuth.GetValueOrDefault(providerId) ?? (runtime.NativeProviders.Contains(providerId) ? null : _oauthProviders.TryGet(providerId));
    }

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
