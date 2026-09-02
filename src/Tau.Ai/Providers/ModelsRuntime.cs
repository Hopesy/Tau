using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers;

/// <summary>
/// provider 的运行时定义，负责维护模型快照并委托具体流式实现。
/// </summary>
public class ProviderDefinition
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<Model>>>? _refreshModels;
    private readonly Func<IReadOnlyList<Model>, IReadOnlyList<Model>>? _filterModels;
    private readonly Func<IReadOnlyList<Model>, ProviderCredential?, IReadOnlyList<Model>>? _filterModelsWithCredential;
    private IReadOnlyList<Model> _models;

    /// <summary>
    /// 创建 provider 定义。
    /// </summary>
    /// <param name="id">provider 唯一标识。</param>
    /// <param name="provider">流式协议实现。</param>
    /// <param name="models">初始模型列表。</param>
    /// <param name="name">显示名称，未提供时使用 id。</param>
    /// <param name="refreshModels">可选动态模型刷新委托。</param>
    /// <param name="filterModels">可选按凭据过滤模型委托。</param>
    /// <param name="auth">可选 provider 自有认证定义。</param>
    /// <param name="baseUrl">provider 默认请求地址；模型未提供地址时使用。</param>
    /// <param name="headers">provider 默认请求头；模型和请求级请求头可以覆盖。</param>
    /// <param name="filterModelsWithCredential">可选按具体凭据过滤模型委托。</param>
    public ProviderDefinition(
        string id,
        IStreamProvider provider,
        IEnumerable<Model>? models = null,
        string? name = null,
        Func<CancellationToken, Task<IReadOnlyList<Model>>>? refreshModels = null,
        Func<IReadOnlyList<Model>, IReadOnlyList<Model>>? filterModels = null,
        ProviderAuthDefinition? auth = null,
        string? baseUrl = null,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<IReadOnlyList<Model>, ProviderCredential?, IReadOnlyList<Model>>? filterModelsWithCredential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _models = models?.ToArray() ?? [];
        _refreshModels = refreshModels;
        _filterModels = filterModels;
        _filterModelsWithCredential = filterModelsWithCredential;
        Auth = auth;
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
        Headers = headers;
    }

    /// <summary>provider 唯一标识。</summary>
    public string Id { get; }

    /// <summary>provider 显示名称。</summary>
    public string Name { get; }

    /// <summary>底层流式 provider 实现。</summary>
    public IStreamProvider Provider { get; }

    /// <summary>provider 自有认证定义；为空时由统一兼容解析器处理。</summary>
    public ProviderAuthDefinition? Auth { get; }

    /// <summary>provider 默认请求地址。</summary>
    public string? BaseUrl { get; }

    /// <summary>provider 默认请求头。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>读取当前模型快照。</summary>
    public virtual IReadOnlyList<Model> GetModels() => _models;

    /// <summary>读取当前 provider 的动态刷新委托是否存在。</summary>
    public bool IsDynamic => _refreshModels is not null;

    /// <summary>
    /// 刷新 provider 模型快照。
    /// </summary>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>刷新完成任务。</returns>
    public async Task RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshModels is null)
        {
            return;
        }

        // 1. 执行 provider 自有网络/缓存逻辑
        var refreshed = await _refreshModels(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // 2. 只在完整刷新成功后替换快照，失败时保留旧列表
        _models = refreshed?.ToArray() ?? [];
    }

    /// <summary>根据凭据过滤当前模型列表。</summary>
    /// <param name="credentialConfigured">是否已配置凭据。</param>
    /// <returns>可用模型列表。</returns>
    public IReadOnlyList<Model> FilterModels(bool credentialConfigured, ProviderCredential? credential = null)
    {
        var models = GetModels();
        if (!credentialConfigured)
        {
            return models;
        }

        if (_filterModelsWithCredential is not null)
        {
            return _filterModelsWithCredential(models, credential);
        }

        return _filterModels is null ? models : _filterModels(models);
    }
}

/// <summary>provider 运行时集合刷新结果。</summary>
public sealed record ModelsRefreshResult(
    bool Aborted,
    IReadOnlyDictionary<string, Exception> Errors);

/// <summary>
/// 统一管理 provider、模型目录、认证和流式请求的运行时集合。
/// </summary>
public sealed class Models
{
    private readonly Dictionary<string, ProviderDefinition> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProviderAuthResolver _authResolver;
    private readonly IProviderCredentialStore _credentialStore;
    private readonly object _gate = new();

    /// <summary>创建模型集合。</summary>
    /// <param name="providers">初始 provider 定义。</param>
    /// <param name="authResolver">认证解析器。</param>
    public Models(
        IEnumerable<ProviderDefinition>? providers = null,
        ProviderAuthResolver? authResolver = null,
        IProviderCredentialStore? credentialStore = null)
    {
        _authResolver = authResolver ?? new ProviderAuthResolver();
        _credentialStore = credentialStore ?? new InMemoryProviderCredentialStore();
        if (providers is not null)
        {
            foreach (var provider in providers)
            {
                SetProvider(provider);
            }
        }
    }

    /// <summary>新增或替换 provider。</summary>
    /// <param name="provider">provider 定义。</param>
    public void SetProvider(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate) _providers[provider.Id] = provider;
    }

    /// <summary>按 id 删除 provider。</summary>
    /// <param name="id">provider id。</param>
    public void DeleteProvider(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate) _providers.Remove(id);
    }

    /// <summary>清空全部 provider。</summary>
    public void ClearProviders()
    {
        lock (_gate) _providers.Clear();
    }

    /// <summary>读取 provider 定义快照。</summary>
    /// <returns>按 id 排序的 provider 列表。</returns>
    public IReadOnlyList<ProviderDefinition> GetProviders()
    {
        lock (_gate) return _providers.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>按 id 查找 provider。</summary>
    /// <param name="id">provider id。</param>
    /// <returns>找到的定义，找不到时为 null。</returns>
    public ProviderDefinition? GetProvider(string id)
    {
        lock (_gate) return _providers.TryGetValue(id, out var provider) ? provider : null;
    }

    /// <summary>读取一个 provider 或全部 provider 的模型快照。</summary>
    /// <param name="providerId">可选 provider id。</param>
    /// <returns>模型快照；单个 provider 读取失败时返回空列表。</returns>
    public IReadOnlyList<Model> GetModels(string? providerId = null)
    {
        var providers = providerId is null
            ? GetProviders()
            : GetProvider(providerId) is { } singleProvider ? [singleProvider] : [];
        var result = new List<Model>();
        foreach (var provider in providers)
        {
            try
            {
                result.AddRange(provider.GetModels());
            }
            catch
            {
                // provider catalog 是 best-effort，单个实现异常不应阻断其它 provider
            }
        }

        return result;
    }

    /// <summary>按 provider 与模型 id 查找模型。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="modelId">模型 id。</param>
    /// <returns>找到的模型，找不到时为 null。</returns>
    public Model? GetModel(string providerId, string modelId) =>
        GetProvider(providerId)?.GetModels().FirstOrDefault(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>检查 provider 认证状态。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="apiKey">可选显式 key。</param>
    /// <param name="env">可选环境覆盖。</param>
    /// <returns>认证状态；未知 provider 返回 null。</returns>
    public ProviderAuthStatus? CheckAuth(string providerId, string? apiKey = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var provider = GetProvider(providerId);
        if (provider is null)
        {
            return null;
        }

        if (provider.Auth is null)
        {
            return _authResolver.GetStatus(providerId, explicitApiKey: apiKey, env: env);
        }

        return CheckAuthAsync(providerId, apiKey, env).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 异步检查 provider 认证配置。
    /// </summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="apiKey">可选的本次请求显式 key。</param>
    /// <param name="env">可选环境覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>认证状态；未知 provider 返回 null。</returns>
    public async Task<ProviderAuthStatus?> CheckAuthAsync(
        string providerId,
        string? apiKey = null,
        IReadOnlyDictionary<string, string>? env = null,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        if (provider is null) return null;
        if (provider.Auth is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _authResolver.GetStatus(providerId, explicitApiKey: apiKey, env: env);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return new ProviderAuthStatus(providerId, true, "explicit", false, false, "API key provided explicitly for this request.");
            }

            var credential = await _credentialStore.ReadAsync(providerId, cancellationToken).ConfigureAwait(false);
            if (credential is ProviderCredential.OAuth && provider.Auth.OAuth is not null)
            {
                return new ProviderAuthStatus(providerId, true, "OAuth", true, true, "OAuth credentials found in the provider credential store.");
            }

            if (provider.Auth.ApiKey is null) return null;
            var apiCredential = credential is ProviderCredential.ApiKey api ? api.Value : null;
            if (provider.Auth.ApiKey.CheckAsync is not null)
            {
                return await provider.Auth.ApiKey.CheckAsync(
                    new ProviderAuthCheckContext(providerId, apiCredential, env, cancellationToken)).ConfigureAwait(false);
            }

            var resolved = await provider.Auth.ApiKey.ResolveAsync(
                new ProviderAuthResolveContext(providerId, apiCredential, env, cancellationToken)).ConfigureAwait(false);
            return resolved is null
                ? new ProviderAuthStatus(providerId, false, "none", false, provider.Auth.ApiKey.LoginAsync is not null, "No credentials found.")
                : new ProviderAuthStatus(providerId, true, resolved.Source ?? provider.Auth.ApiKey.Name, false, provider.Auth.ApiKey.LoginAsync is not null, "Credentials are available.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModelsError("auth", $"Authentication check failed for provider '{providerId}'.", ex);
        }
    }

    /// <summary>
    /// 按 provider 自有认证定义解析请求认证，并合并模型级 headers/baseUrl。
    /// </summary>
    /// <param name="model">目标模型。</param>
    /// <param name="apiKey">可选本次请求显式 key。</param>
    /// <param name="env">可选环境覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析后的认证；provider 未配置或未知时返回 null。</returns>
    public async Task<ProviderAuthResult?> ResolveAuthAsync(
        Model model,
        string? apiKey = null,
        IReadOnlyDictionary<string, string>? env = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var provider = GetProvider(model.Provider) ?? GetProvider(model.Api);
        if (provider is null) return null;
        try
        {
            ProviderAuthResult? result;
            if (provider.Auth is null)
            {
                var resolvedKey = _authResolver.ResolveApiKey(model.Provider, apiKey, env) ?? apiKey;
                result = string.IsNullOrWhiteSpace(resolvedKey)
                    ? null
                    : new ProviderAuthResult(resolvedKey, Source: "legacy");
            }
            else
            {
                result = await ResolveProviderAuthAsync(provider, apiKey, env, cancellationToken).ConfigureAwait(false);
            }

            if (result is null) return null;
            return result with
            {
                Headers = MergeHeaders(model.Headers, result.Headers),
                BaseUrl = result.BaseUrl ?? model.BaseUrl,
                Env = MergeEnvironment(result.Env, env)
            };
        }
        catch (ModelsError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModelsError("auth", $"Authentication resolution failed for provider '{model.Provider}'.", ex);
        }
    }

    /// <summary>
    /// 运行 provider 自有登录流程并以串行方式保存凭据。
    /// </summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="type">登录类型，api_key 或 oauth。</param>
    /// <param name="interaction">登录交互回调。</param>
    /// <returns>登录后保存的凭据。</returns>
    public async Task<ProviderCredential> LoginAsync(
        string providerId,
        string type,
        AuthInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var provider = GetProvider(providerId) ?? throw new ModelsError("provider", $"Unknown provider: {providerId}");
        try
        {
            ProviderCredential credential = type.Trim().ToLowerInvariant() switch
            {
                "api_key" or "apikey" when provider.Auth?.ApiKey?.LoginAsync is not null =>
                    new ProviderCredential.ApiKey(await provider.Auth.ApiKey.LoginAsync(interaction).ConfigureAwait(false)),
                "oauth" when provider.Auth?.OAuth is not null =>
                    new ProviderCredential.OAuth(await provider.Auth.OAuth.LoginAsync(interaction).ConfigureAwait(false)),
                _ => throw new ModelsError("auth", $"Provider '{providerId}' does not support {type} login.")
            };
            var saved = await _credentialStore.ModifyAsync(providerId, _ => Task.FromResult<ProviderCredential?>(credential), interaction.CancellationToken).ConfigureAwait(false);
            return saved ?? credential;
        }
        catch (ModelsError)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errorCode = type.Trim().Equals("oauth", StringComparison.OrdinalIgnoreCase) ? "oauth" : "auth";
            throw new ModelsError(errorCode, $"{type} login failed for provider '{providerId}'.", ex);
        }
    }

    /// <summary>返回已配置认证的模型。</summary>
    /// <param name="providerId">可选 provider id。</param>
    /// <returns>可用模型列表。</returns>
    public IReadOnlyList<Model> GetAvailable(string? providerId = null)
    {
        var providers = providerId is null
            ? GetProviders()
            : GetProvider(providerId) is { } singleProvider ? [singleProvider] : [];
        var result = new List<Model>();
        foreach (var provider in providers)
        {
            ProviderCredential? credential = null;
            ProviderAuthStatus status;
            try
            {
                credential = _credentialStore.ReadAsync(provider.Id).GetAwaiter().GetResult();
                status = provider.Auth is null
                    ? _authResolver.GetStatus(provider.Id)
                    : CheckAuthAsync(provider.Id).GetAwaiter().GetResult() ?? new ProviderAuthStatus(provider.Id, false, "none", false, false, "No credentials found.");
            }
            catch
            {
                continue;
            }

            result.AddRange(provider.FilterModels(status.IsConfigured, credential));
        }

        return result;
    }

    /// <summary>
    /// 异步读取已配置认证的模型，并应用 provider 自有的凭据过滤策略。
    /// </summary>
    /// <param name="providerId">可选 provider id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>认证可用模型列表。</returns>
    public async Task<IReadOnlyList<Model>> GetAvailableAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var providers = providerId is null
            ? GetProviders()
            : GetProvider(providerId) is { } singleProvider ? [singleProvider] : [];
        var result = new List<Model>();
        foreach (var provider in providers)
        {
            var status = await CheckAuthAsync(provider.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (status?.IsConfigured != true) continue;
            var credential = await _credentialStore.ReadAsync(provider.Id, cancellationToken).ConfigureAwait(false);
            result.AddRange(provider.FilterModels(true, credential));
        }

        return result;
    }

    /// <summary>并行刷新所有或指定动态 provider。</summary>
    /// <param name="providerIds">可选 provider id 集合。</param>
    /// <param name="allowNetwork">是否允许动态 provider 访问网络。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>包含取消状态和各 provider 错误的结果。</returns>
    public async Task<ModelsRefreshResult> RefreshAsync(
        IEnumerable<string>? providerIds = null,
        bool allowNetwork = true,
        CancellationToken cancellationToken = default)
    {
        var selected = providerIds is null
            ? null
            : providerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
        var tasks = GetProviders()
            .Where(provider => provider.IsDynamic && (selected is null || selected.Contains(provider.Id)))
            .Select(async provider =>
            {
                if (!allowNetwork) return;
                try
                {
                    await provider.RefreshModelsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    lock (errors) errors[provider.Id] = ex;
                }
            })
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return new ModelsRefreshResult(cancellationToken.IsCancellationRequested, errors);
    }

    /// <summary>解析认证并启动 provider 流。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">对话上下文。</param>
    /// <param name="options">流选项。</param>
    /// <returns>异步消息流。</returns>
    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var provider = GetProvider(model.Provider) ?? GetProvider(model.Api);
        if (provider is null)
        {
            return ErrorStream(model, $"Provider '{model.Provider}' is not registered.");
        }

        return StartAuthenticatedStream(provider, ApplyProviderDefaults(provider, model), context, options, simple: false);
    }

    /// <summary>解析认证并启动简单流。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">对话上下文。</param>
    /// <param name="options">简单流选项。</param>
    /// <returns>异步消息流。</returns>
    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        var provider = GetProvider(model.Provider) ?? GetProvider(model.Api);
        if (provider is null)
        {
            return ErrorStream(model, $"Provider '{model.Provider}' is not registered.");
        }

        return StartAuthenticatedStream(provider, ApplyProviderDefaults(provider, model), context, options, simple: true);
    }

    /// <summary>等待普通流完成并返回最终 assistant message。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">对话上下文。</param>
    /// <param name="options">流选项。</param>
    /// <returns>最终 assistant message。</returns>
    public Task<AssistantMessage> CompleteAsync(Model model, LlmContext context, StreamOptions options) =>
        Stream(model, context, options).ResultAsync;

    /// <summary>等待简单流完成并返回最终 assistant message。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">对话上下文。</param>
    /// <param name="options">简单流选项。</param>
    /// <returns>最终 assistant message。</returns>
    public Task<AssistantMessage> CompleteSimpleAsync(Model model, LlmContext context, SimpleStreamOptions options) =>
        StreamSimple(model, context, options).ResultAsync;

    /// <summary>
    /// 读取指定 provider 的认证状态，作为参考项目 getAuth/checkAuth 的同步兼容入口。
    /// </summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="apiKey">可选显式 API key。</param>
    /// <param name="env">可选环境覆盖。</param>
    /// <returns>认证状态；未知 provider 返回 null。</returns>
    public ProviderAuthStatus? GetAuth(string providerId, string? apiKey = null, IReadOnlyDictionary<string, string>? env = null) =>
        CheckAuth(providerId, apiKey, env);

    /// <summary>
    /// 读取指定模型的认证状态。
    /// </summary>
    /// <param name="model">目标模型。</param>
    /// <param name="apiKey">可选显式 API key。</param>
    /// <param name="env">可选环境覆盖。</param>
    /// <returns>认证状态；未知 provider 返回 null。</returns>
    public ProviderAuthStatus? GetAuth(Model model, string? apiKey = null, IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var provider = GetProvider(model.Provider) ?? GetProvider(model.Api);
        if (provider is null)
        {
            return null;
        }

        return provider.Auth is null
            ? _authResolver.GetStatus(model, apiKey, env)
            : CheckAuth(provider.Id, apiKey, env);
    }

    /// <summary>
    /// 启动 provider OAuth 登录并保存凭据。
    /// </summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="callbacks">登录交互回调。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>保存后的 OAuth 凭据。</returns>
    public async Task<OAuthCredentials> LoginAsync(
        string providerId,
        IOAuthLoginCallbacks callbacks,
        CancellationToken cancellationToken = default)
    {
        if (GetProvider(providerId) is null)
        {
            throw new ModelsError("provider", $"Unknown provider: {providerId}");
        }

        var oauthProvider = _authResolver.GetOAuthProvider(providerId)
            ?? throw new ModelsError("auth", $"Provider '{providerId}' does not support OAuth login.");
        try
        {
            var credentials = await oauthProvider.LoginAsync(callbacks, cancellationToken).ConfigureAwait(false);
            _authResolver.SaveOAuthCredentials(providerId, credentials);
            return credentials;
        }
        catch (Exception ex) when (ex is not ModelsError)
        {
            throw new ModelsError("oauth", $"OAuth login failed for {providerId}.", ex);
        }
    }

    /// <summary>
    /// 删除 provider 的持久化认证凭据。
    /// </summary>
    /// <param name="providerId">provider id。</param>
    /// <returns>是否删除了现有凭据。</returns>
    public bool Logout(string providerId)
    {
        var removedLegacy = _authResolver.Logout(providerId);
        try
        {
            var hadProviderCredential = _credentialStore.ReadAsync(providerId).GetAwaiter().GetResult() is not null;
            _credentialStore.DeleteAsync(providerId).GetAwaiter().GetResult();
            return removedLegacy || hadProviderCredential;
        }
        catch
        {
            return removedLegacy;
        }
    }

    /// <summary>
    /// 拉取 deferred 响应并返回最终消息。
    /// </summary>
    /// <param name="model">原始请求模型。</param>
    /// <param name="handle">provider 返回的句柄。</param>
    /// <param name="options">拉取选项。</param>
    /// <returns>最终 assistant message。</returns>
    public async Task<AssistantMessage> FetchDeferredAsync(Model model, DeferredHandle handle, DeferredFetchOptions? options = null)
    {
        var provider = GetProvider(model.Provider) ?? GetProvider(model.Api);
        if (provider is null)
        {
            return await ErrorStream(model, new ModelsError("provider", $"Provider '{model.Provider}' is not registered.")).ResultAsync.ConfigureAwait(false);
        }

        try
        {
            var requestModel = ApplyProviderDefaults(provider, model);
            var requestOptions = options ?? new DeferredFetchOptions();
            var auth = await ResolveAuthAsync(requestModel, requestOptions.ApiKey, requestOptions.Env, requestOptions.Signal).ConfigureAwait(false);
            requestModel = auth?.BaseUrl is { Length: > 0 } baseUrl ? requestModel with { BaseUrl = baseUrl } : requestModel;
            requestOptions = requestOptions with
            {
                ApiKey = auth?.ApiKey ?? requestOptions.ApiKey,
                Headers = MergeHeaders(requestOptions.Headers, auth?.Headers)?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                Env = MergeEnvironment(requestOptions.Env, auth?.Env)
            };
            return await provider.Provider.FetchDeferred(requestModel, handle, requestOptions).ResultAsync.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await ErrorStream(model, ex).ResultAsync.ConfigureAwait(false);
        }
    }

    /// <summary>FetchDeferredAsync 的同步命名兼容入口。</summary>
    /// <param name="model">原始请求模型。</param>
    /// <param name="handle">provider 返回的句柄。</param>
    /// <param name="options">拉取选项。</param>
    /// <returns>最终 assistant message。</returns>
    public Task<AssistantMessage> FetchDeferred(Model model, DeferredHandle handle, DeferredFetchOptions? options = null) =>
        FetchDeferredAsync(model, handle, options);

    /// <summary>
    /// 取消 deferred 响应；provider 不支持时返回 ModelsError。
    /// </summary>
    /// <param name="model">原始请求模型。</param>
    /// <param name="handle">provider 返回的句柄。</param>
    /// <param name="options">取消选项。</param>
    /// <returns>取消完成任务。</returns>
    public async Task CancelDeferredAsync(Model model, DeferredHandle handle, DeferredCancelOptions? options = null)
    {
            var provider = GetProvider(model.Provider) ?? GetProvider(model.Api)
                ?? throw new ModelsError("provider", $"Provider '{model.Provider}' is not registered.");
        try
        {
            var resolvedOptions = options ?? new DeferredCancelOptions();
            var requestModel = ApplyProviderDefaults(provider, model);
            var auth = await ResolveAuthAsync(requestModel, resolvedOptions.ApiKey, resolvedOptions.Env, resolvedOptions.Signal).ConfigureAwait(false);
            requestModel = auth?.BaseUrl is { Length: > 0 } baseUrl ? requestModel with { BaseUrl = baseUrl } : requestModel;
            resolvedOptions = resolvedOptions with
            {
                ApiKey = auth?.ApiKey ?? resolvedOptions.ApiKey,
                Headers = MergeHeaders(auth?.Headers, resolvedOptions.Headers)?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                Env = MergeEnvironment(resolvedOptions.Env, auth?.Env)
            };
            await provider.Provider.CancelDeferred(requestModel, handle, resolvedOptions).ConfigureAwait(false);
        }
        catch (ModelsError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModelsError("provider", $"Deferred cancellation failed for {model.Provider}.", ex);
        }
    }

    /// <summary>CancelDeferredAsync 的同步命名兼容入口。</summary>
    /// <param name="model">原始请求模型。</param>
    /// <param name="handle">provider 返回的句柄。</param>
    /// <param name="options">取消选项。</param>
    /// <returns>取消完成任务。</returns>
    public Task CancelDeferred(Model model, DeferredHandle handle, DeferredCancelOptions? options = null) =>
        CancelDeferredAsync(model, handle, options);

    /// <summary>应用 provider 级默认地址和请求头。</summary>
    /// <param name="provider">provider 定义。</param>
    /// <param name="model">原始模型。</param>
    /// <returns>应用默认配置后的模型。</returns>
    private static Model ApplyProviderDefaults(ProviderDefinition provider, Model model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? provider.BaseUrl : model.BaseUrl;
        var headers = MergeHeaders(provider.Headers, model.Headers);
        if (string.Equals(baseUrl, model.BaseUrl, StringComparison.Ordinal) &&
            HeadersEqual(headers, model.Headers))
        {
            return model;
        }

        return model with
        {
            BaseUrl = baseUrl,
            Headers = headers?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>比较请求头是否已经包含 provider 默认值。</summary>
    /// <param name="left">合并后的请求头。</param>
    /// <param name="right">模型原始请求头。</param>
    /// <returns>内容相同返回 true。</returns>
    private static bool HeadersEqual(
        IReadOnlyDictionary<string, string>? left,
        IDictionary<string, string>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in right)
        {
            if (!left.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 在异步认证解析完成后转发底层 provider 流。
    /// </summary>
    /// <param name="provider">provider 定义。</param>
    /// <param name="model">目标模型。</param>
    /// <param name="context">对话上下文。</param>
    /// <param name="options">请求选项。</param>
    /// <param name="simple">是否使用 streamSimple。</param>
    /// <returns>外层事件流；认证异常编码为错误事件。</returns>
    private AssistantMessageStream StartAuthenticatedStream(
        ProviderDefinition provider,
        Model model,
        LlmContext context,
        StreamOptions options,
        bool simple)
    {
        var outer = new AssistantMessageStream();
        _ = Task.Run(async () =>
        {
            try
            {
                var auth = await ResolveAuthAsync(model, options.ApiKey, options.Env, options.Signal).ConfigureAwait(false);
                if (provider.Auth is not null && auth is null)
                {
                    throw new ModelsError("auth", $"Provider '{provider.Id}' is not configured.");
                }
                var requestModel = auth?.BaseUrl is { Length: > 0 } baseUrl ? model with { BaseUrl = baseUrl } : model;
                var requestOptions = options with
                {
                    ApiKey = auth?.ApiKey ?? options.ApiKey,
                    Headers = MergeHeaders(auth?.Headers, options.Headers)?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                    Env = MergeEnvironment(options.Env, auth?.Env)
                };
                var inner = simple && requestOptions is SimpleStreamOptions simpleOptions
                    ? provider.Provider.StreamSimple(requestModel, context, simpleOptions)
                    : provider.Provider.Stream(requestModel, context, requestOptions);
                await foreach (var item in inner.ConfigureAwait(false)) outer.Push(item);
            }
            catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
            {
                var aborted = StreamOptionHelpers.CreateAbortedMessage(model, model.Api);
                outer.Push(new ErrorEvent(aborted.ErrorMessage ?? "Request was aborted", Message: aborted));
            }
            catch (Exception ex)
            {
                outer.Push(new ErrorEvent(ex.Message, Message: new AssistantMessage
                {
                    Api = model.Api,
                    Provider = model.Provider,
                    Model = model.Id,
                    Content = [],
                    StopReason = StopReason.Error,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTimeOffset.UtcNow
                }));
            }
        });
        return outer;
    }

    /// <summary>解析 provider 自有认证定义。</summary>
    /// <param name="provider">provider 定义。</param>
    /// <param name="explicitApiKey">显式 API key。</param>
    /// <param name="environment">环境覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>请求认证结果。</returns>
    private async Task<ProviderAuthResult?> ResolveProviderAuthAsync(
        ProviderDefinition provider,
        string? explicitApiKey,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var definition = provider.Auth!;
        if (explicitApiKey is not null && definition.ApiKey is not null)
        {
            return await definition.ApiKey.ResolveAsync(
                new ProviderAuthResolveContext(provider.Id, new ApiKeyCredential(explicitApiKey, environment), environment, cancellationToken)).ConfigureAwait(false);
        }

        var stored = await _credentialStore.ReadAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        if (stored is ProviderCredential.OAuth oauthCredential && definition.OAuth is not null)
        {
            var current = oauthCredential.Value;
            if (DateTimeOffset.UtcNow >= current.ExpiresAt - TimeSpan.FromMinutes(5))
            {
                var refreshed = await _credentialStore.ModifyAsync(provider.Id, async existing =>
                {
                    if (existing is not ProviderCredential.OAuth existingOAuth) return existing;
                    var candidate = existingOAuth.Value;
                    if (DateTimeOffset.UtcNow < candidate.ExpiresAt - TimeSpan.FromMinutes(5)) return existing;
                    var next = await definition.OAuth.RefreshAsync(candidate, cancellationToken).ConfigureAwait(false);
                    return new ProviderCredential.OAuth(next);
                }, cancellationToken).ConfigureAwait(false);
                if (refreshed is ProviderCredential.OAuth refreshedOAuth)
                {
                    current = refreshedOAuth.Value;
                }
            }

            var auth = await definition.OAuth.ToAuthAsync(current).ConfigureAwait(false);
            return auth with { Source = auth.Source ?? "OAuth" };
        }

        if (definition.ApiKey is null) return null;
        var apiCredential = stored is ProviderCredential.ApiKey api ? api.Value : null;
        return await definition.ApiKey.ResolveAsync(
            new ProviderAuthResolveContext(provider.Id, apiCredential, environment, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>合并两个请求头字典，后者覆盖前者。</summary>
    /// <param name="baseHeaders">基础请求头。</param>
    /// <param name="overrideHeaders">覆盖请求头。</param>
    /// <returns>合并后的请求头。</returns>
    private static IReadOnlyDictionary<string, string>? MergeHeaders(
        IReadOnlyDictionary<string, string>? baseHeaders,
        IReadOnlyDictionary<string, string>? overrideHeaders)
    {
        if (baseHeaders is null && overrideHeaders is null) return null;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseHeaders is not null)
        {
            foreach (var pair in baseHeaders) merged[pair.Key] = pair.Value;
        }
        if (overrideHeaders is not null)
        {
            foreach (var pair in overrideHeaders) merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    /// <summary>合并模型的可变请求头与认证请求头。</summary>
    /// <param name="baseHeaders">基础请求头。</param>
    /// <param name="overrideHeaders">覆盖请求头。</param>
    /// <returns>合并后的请求头。</returns>
    private static IReadOnlyDictionary<string, string>? MergeHeaders(
        IDictionary<string, string>? baseHeaders,
        IReadOnlyDictionary<string, string>? overrideHeaders) =>
        MergeHeaders((IReadOnlyDictionary<string, string>?)(baseHeaders is null ? null : new Dictionary<string, string>(baseHeaders, StringComparer.OrdinalIgnoreCase)), overrideHeaders);

    /// <summary>合并只读基础请求头与可变覆盖请求头。</summary>
    /// <param name="baseHeaders">基础请求头。</param>
    /// <param name="overrideHeaders">覆盖请求头。</param>
    /// <returns>合并后的请求头。</returns>
    private static IReadOnlyDictionary<string, string>? MergeHeaders(
        IReadOnlyDictionary<string, string>? baseHeaders,
        IDictionary<string, string>? overrideHeaders) =>
        MergeHeaders(
            baseHeaders,
            (IReadOnlyDictionary<string, string>?)(overrideHeaders is null
                ? null
                : new Dictionary<string, string>(overrideHeaders, StringComparer.OrdinalIgnoreCase)));

    /// <summary>合并 provider 环境配置，后者覆盖前者。</summary>
    /// <param name="baseEnvironment">基础环境配置。</param>
    /// <param name="overrideEnvironment">覆盖环境配置。</param>
    /// <returns>合并后的环境配置。</returns>
    private static IReadOnlyDictionary<string, string>? MergeEnvironment(
        IReadOnlyDictionary<string, string>? baseEnvironment,
        IReadOnlyDictionary<string, string>? overrideEnvironment)
    {
        if (baseEnvironment is null && overrideEnvironment is null) return null;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseEnvironment is not null)
        {
            foreach (var pair in baseEnvironment) merged[pair.Key] = pair.Value;
        }
        if (overrideEnvironment is not null)
        {
            foreach (var pair in overrideEnvironment) merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    /// <summary>创建文本错误流。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="error">错误消息。</param>
    /// <returns>已结束的错误流。</returns>
    private static AssistantMessageStream ErrorStream(Model model, string error) =>
        ErrorStream(model, new InvalidOperationException(error));

    private static AssistantMessageStream ErrorStream(Model model, Exception error)
    {
        var stream = new AssistantMessageStream();
        var message = new AssistantMessage
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Content = [],
            StopReason = StopReason.Error,
            ErrorMessage = error.Message,
            Timestamp = DateTimeOffset.UtcNow
        };
        stream.Push(new ErrorEvent(error.Message, Message: message));
        return stream;
    }
}

/// <summary>
/// Models 运行时的稳定错误类型。
/// </summary>
public sealed class ModelsError : Exception
{
    /// <summary>错误分类，例如 auth、oauth、provider 或 model_source。</summary>
    public string Code { get; }

    /// <summary>
    /// 创建 Models 错误。
    /// </summary>
    /// <param name="code">错误分类。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="innerException">底层异常。</param>
    public ModelsError(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }
}
