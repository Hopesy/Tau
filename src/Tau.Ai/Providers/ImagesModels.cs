using Tau.Ai.Auth;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

public sealed class ImagesModelsException : Exception
{
    public ImagesModelsException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public class ImagesProviderDefinition
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ImagesModel>>>? _refreshModelsAsync;
    private readonly object _refreshLock = new();
    private IReadOnlyList<ImagesModel> _models;
    private Task? _inflightRefresh;

    public ImagesProviderDefinition(
        string id,
        IImagesProvider provider,
        IEnumerable<ImagesModel> models,
        string? name = null,
        Func<CancellationToken, Task<IReadOnlyList<ImagesModel>>>? refreshModelsAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _models = models?.ToArray() ?? throw new ArgumentNullException(nameof(models));
        _refreshModelsAsync = refreshModelsAsync;
    }

    public string Id { get; }

    public string Name { get; }

    public IImagesProvider Provider { get; }

    public virtual IReadOnlyList<ImagesModel> GetModels() => _models;

    public Task RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshModelsAsync is null)
        {
            return Task.CompletedTask;
        }

        lock (_refreshLock)
        {
            if (_inflightRefresh is null)
            {
                _inflightRefresh = RunRefreshAsync(cancellationToken);
                _ = ClearInflightRefreshAsync(_inflightRefresh);
            }

            return _inflightRefresh;
        }
    }

    public Task<AssistantImages> GenerateImagesAsync(
        ImagesModel model,
        ImagesContext context,
        ImagesOptions options) =>
        Provider.GenerateImagesAsync(model, context, options);

    private async Task RunRefreshAsync(CancellationToken cancellationToken)
    {
        _models = await _refreshModelsAsync!(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearInflightRefreshAsync(Task refreshTask)
    {
        try
        {
            await refreshTask.ConfigureAwait(false);
        }
        catch
        {
            // The original refresh task carries the failure to its caller.
        }

        lock (_refreshLock)
        {
            if (ReferenceEquals(_inflightRefresh, refreshTask))
            {
                _inflightRefresh = null;
            }
        }
    }
}

public sealed class ImagesModels
{
    private readonly Dictionary<string, ImagesProviderDefinition> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _providersLock = new();
    private readonly ProviderAuthResolver _authResolver;
    private readonly ModelConfigurationStore _configurationStore;

    public ImagesModels(
        IEnumerable<ImagesProviderDefinition>? providers = null,
        ProviderAuthResolver? authResolver = null,
        ModelConfigurationStore? configurationStore = null)
    {
        _authResolver = authResolver ?? new ProviderAuthResolver();
        _configurationStore = configurationStore ?? new ModelConfigurationStore();

        if (providers is null)
        {
            return;
        }

        foreach (var provider in providers)
        {
            SetProvider(provider);
        }
    }

    public void SetProvider(ImagesProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_providersLock)
        {
            _providers[provider.Id] = provider;
        }
    }

    public bool DeleteProvider(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_providersLock)
        {
            return _providers.Remove(id);
        }
    }

    public void ClearProviders()
    {
        lock (_providersLock)
        {
            _providers.Clear();
        }
    }

    public IReadOnlyList<ImagesProviderDefinition> GetProviders()
    {
        lock (_providersLock)
        {
            return [.. _providers.Values];
        }
    }

    public ImagesProviderDefinition? GetProvider(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_providersLock)
        {
            return _providers.TryGetValue(id, out var provider) ? provider : null;
        }
    }

    public IReadOnlyList<ImagesModel> GetModels(string? provider = null)
    {
        if (provider is not null)
        {
            var entry = GetProvider(provider);
            if (entry is null)
            {
                return [];
            }

            try
            {
                return [.. entry.GetModels()];
            }
            catch
            {
                return [];
            }
        }

        var models = new List<ImagesModel>();
        foreach (var entry in GetProviders())
        {
            try
            {
                models.AddRange(entry.GetModels());
            }
            catch
            {
                // Match upstream best-effort semantics: a bad provider yields no models.
            }
        }

        return models;
    }

    public ImagesModel? GetModel(string provider, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return GetModels(provider).FirstOrDefault(model => model.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RefreshAsync(string? provider = null, CancellationToken cancellationToken = default)
    {
        if (provider is not null)
        {
            var entry = GetProvider(provider);
            if (entry is null)
            {
                return;
            }

            try
            {
                await entry.RefreshModelsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ImagesModelsException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ImagesModelsException(
                    "model_source",
                    $"Image model refresh failed for provider '{provider}'.",
                    ex);
            }

            return;
        }

        var refreshes = GetProviders().Select(async entry =>
        {
            try
            {
                await entry.RefreshModelsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Global refresh is best-effort, matching upstream allSettled behavior.
            }
        });
        await Task.WhenAll(refreshes).ConfigureAwait(false);
    }

    public ProviderAuthStatus? GetAuth(ImagesModel model, ImagesOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (GetProvider(model.Provider) is null)
        {
            return null;
        }

        return _authResolver.GetStatus(model, options?.ApiKey, options?.Env);
    }

    public async Task<AssistantImages> GenerateImagesAsync(
        ImagesModel model,
        ImagesContext context,
        ImagesOptions? options = null)
    {
        options ??= new ImagesOptions();
        try
        {
            var provider = GetProvider(model.Provider);
            if (provider is null)
            {
                throw new ImagesModelsException("provider", $"Unknown image provider: {model.Provider}");
            }

            var resolvedOptions = ResolveOptions(model, options);
            return await provider.GenerateImagesAsync(model, context, resolvedOptions).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (options.Signal.IsCancellationRequested)
        {
            return CreateErrorResult(model, ImagesStopReason.Aborted, ex.Message);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(model, ImagesStopReason.Error, ex.Message);
        }
    }

    private ImagesOptions ResolveOptions(ImagesModel model, ImagesOptions options)
    {
        var requestConfig = _configurationStore.ResolveRequestConfiguration(model, options.Env);
        var env = ProviderEnvironment.Merge(requestConfig.Options.Env, options.Env);
        if (env is not null)
        {
            requestConfig = _configurationStore.ResolveRequestConfiguration(model, env);
            env = ProviderEnvironment.Merge(requestConfig.Options.Env, options.Env);
        }

        var apiKey = _authResolver.ResolveApiKey(model.Provider, options.ApiKey, env) ?? requestConfig.ApiKey;
        var configuredHeaders = MergeHeaders(requestConfig.Headers, requestConfig.Options.Headers);
        var headers = MergeHeaders(configuredHeaders, options.Headers);
        var metadata = MergeMetadata(requestConfig.Options.Metadata, options.Metadata);
        if (requestConfig.AuthHeader &&
            !string.IsNullOrWhiteSpace(apiKey) &&
            !EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers.TryAdd("Authorization", $"Bearer {apiKey}");
        }

        return options with
        {
            ApiKey = apiKey,
            Headers = headers,
            Timeout = options.Timeout ?? requestConfig.Options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay ?? requestConfig.Options.MaxRetryDelay,
            MaxRetries = options.MaxRetries ?? requestConfig.Options.MaxRetries,
            Metadata = metadata,
            Env = env
        };
    }

    private static AssistantImages CreateErrorResult(
        ImagesModel model,
        ImagesStopReason stopReason,
        string errorMessage) =>
        new()
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Output = [],
            StopReason = stopReason,
            ErrorMessage = errorMessage,
            Timestamp = DateTimeOffset.UtcNow
        };

    private static IDictionary<string, string>? MergeHeaders(
        IDictionary<string, string>? configuredHeaders,
        IDictionary<string, string>? explicitHeaders)
    {
        if (configuredHeaders is null || configuredHeaders.Count == 0)
        {
            return explicitHeaders;
        }

        var result = new Dictionary<string, string>(configuredHeaders, StringComparer.OrdinalIgnoreCase);
        if (explicitHeaders is not null)
        {
            foreach (var (key, value) in explicitHeaders)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static IDictionary<string, object>? MergeMetadata(
        IDictionary<string, object>? configuredMetadata,
        IDictionary<string, object>? explicitMetadata)
    {
        if (configuredMetadata is null || configuredMetadata.Count == 0)
        {
            return explicitMetadata;
        }

        var result = new Dictionary<string, object>(configuredMetadata, StringComparer.OrdinalIgnoreCase);
        if (explicitMetadata is not null)
        {
            foreach (var (key, value) in explicitMetadata)
            {
                result[key] = value;
            }
        }

        return result;
    }
}
