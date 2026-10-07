// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

/// <summary>【AI】【模型能力】统一模型集合的分类查询及非流式调用。</summary>
public sealed partial class Models
{
    /// <summary>按能力类型读取模型快照。</summary>
    /// <param name="type">chat、image 或 classifier。</param>
    /// <param name="providerId">可选 provider 标识。</param>
    /// <returns>指定类型的模型。</returns>
    public IReadOnlyList<Model> GetModelsOfType(string type, string? providerId = null) =>
        GetAllModels(providerId).Where(model => ModelTypes.GetModelType(model) == type).ToArray();

    /// <summary>按能力类型、提供方及标识查找模型。</summary>
    /// <param name="type">能力类型。</param>
    /// <param name="providerId">provider 标识。</param>
    /// <param name="modelId">模型标识。</param>
    /// <returns>匹配模型；不存在时返回 null。</returns>
    public Model? GetModelOfType(string type, string providerId, string modelId) =>
        GetModelsOfType(type, providerId).FirstOrDefault(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>同步查询指定类型且认证可用的模型。</summary>
    /// <param name="type">能力类型。</param>
    /// <param name="providerId">可选 provider 标识。</param>
    /// <returns>可用模型。</returns>
    public IReadOnlyList<Model> GetAvailableOfType(string type, string? providerId = null) =>
        GetAllAvailable(providerId).Where(model => ModelTypes.GetModelType(model) == type).ToArray();

    /// <summary>异步查询指定类型且认证可用的模型。</summary>
    /// <param name="type">能力类型。</param>
    /// <param name="providerId">可选 provider 标识。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>可用模型。</returns>
    public async Task<IReadOnlyList<Model>> GetAvailableOfTypeAsync(string type, string? providerId = null, CancellationToken cancellationToken = default) =>
        (await GetAllAvailableAsync(providerId, cancellationToken).ConfigureAwait(false))
        .Where(model => ModelTypes.GetModelType(model) == type).ToArray();

    /// <summary>【AI】【图像调用】检查能力、解析认证并委托图像协议执行。</summary>
    /// <param name="model">图像模型；误传其他类型时返回错误结果。</param>
    /// <param name="context">图像生成输入。</param>
    /// <param name="options">可选请求配置。</param>
    /// <returns>图像输出或错误结果。</returns>
    public async Task<AssistantImages> GenerateImagesAsync(Model model, ImagesContext context, ImagesOptions? options = null)
    {
        options ??= new ImagesOptions();
        try
        {
            // 1. 【AI】【图像调用】先检查类型和协议，避免错误输入触发认证或网络请求
            if (ModelTypes.GetModelType(model) != ModelTypes.Image || model is not ImagesModel)
                throw new ModelsError("provider", $"Model '{model.Provider}/{model.Id}' is not an image model.");
            var provider = GetProvider(model.Provider) ?? throw new ModelsError("provider", $"Unknown provider: {model.Provider}");
            if (!provider.Images.TryGetValue(model.Api, out var implementation))
                throw new ModelsError("provider", $"Provider '{provider.Id}' does not support image API '{model.Api}'.");

            // 2. 【AI】【图像调用】共享 provider 默认值及认证解析，请求头由本次选项最终覆盖
            options.Signal.ThrowIfCancellationRequested();
            var config = ResolveCapabilityConfiguration(model, options.Env);
            options = ApplyConfiguration(config, options);
            var (requestModel, auth) = await PrepareCapabilityRequestAsync(provider, model, options.ApiKey, options.Env, options.Signal).ConfigureAwait(false);
            if (config.AuthHeader && string.IsNullOrWhiteSpace(auth?.ApiKey ?? options.ApiKey))
                throw new ModelsError("auth", "authHeader requires a resolved API key");
            var requestOptions = options with
            {
                ApiKey = auth?.ApiKey ?? options.ApiKey,
                Headers = FinalizeCapabilityHeaders(auth, options.Headers, config.AuthHeader, options.ApiKey),
                Env = MergeEnvironment(options.Env, auth?.Env)
            };
            return await implementation.GenerateImagesAsync((ImagesModel)requestModel, context, requestOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AssistantImages
            {
                Api = model.Api, Provider = model.Provider, Model = model.Id,
                StopReason = options.Signal.IsCancellationRequested ? ImagesStopReason.Aborted : ImagesStopReason.Error,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>【AI】【分类调用】检查能力、解析认证并委托分类协议执行。</summary>
    /// <param name="model">分类模型；误传其他类型时返回错误结果。</param>
    /// <param name="context">结构化状态及问题。</param>
    /// <param name="options">可选请求配置。</param>
    /// <returns>分类答案或错误结果。</returns>
    public async Task<ClassifierResult> ClassifyAsync(Model model, ClassifierContext context, ClassifierOptions? options = null)
    {
        options ??= new ClassifierOptions();
        try
        {
            // 1. 【AI】【分类调用】先检查类型和协议
            if (ModelTypes.GetModelType(model) != ModelTypes.Classifier || model is not ClassifierModel)
                throw new ModelsError("provider", $"Model '{model.Provider}/{model.Id}' is not a classifier model.");
            var provider = GetProvider(model.Provider) ?? throw new ModelsError("provider", $"Unknown provider: {model.Provider}");
            if (!provider.Classifiers.TryGetValue(model.Api, out var implementation))
                throw new ModelsError("provider", $"Provider '{provider.Id}' does not support classifier API '{model.Api}'.");

            // 2. 【AI】【分类调用】复用统一认证，并保留回调、重试及取消选项
            options.Signal.ThrowIfCancellationRequested();
            var config = ResolveCapabilityConfiguration(model, options.Env);
            options = ApplyConfiguration(config, options);
            var (requestModel, auth) = await PrepareCapabilityRequestAsync(provider, model, options.ApiKey, options.Env, options.Signal).ConfigureAwait(false);
            if (config.AuthHeader && string.IsNullOrWhiteSpace(auth?.ApiKey ?? options.ApiKey))
                throw new ModelsError("auth", "authHeader requires a resolved API key");
            var requestOptions = options with
            {
                ApiKey = auth?.ApiKey ?? options.ApiKey,
                Headers = FinalizeCapabilityHeaders(auth, options.Headers, config.AuthHeader, options.ApiKey),
                Env = MergeEnvironment(options.Env, auth?.Env)
            };
            return await implementation.ClassifyAsync((ClassifierModel)requestModel, context, requestOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ClassifierResult
            {
                Api = model.Api, Provider = model.Provider, Model = model.Id,
                StopReason = options.Signal.IsCancellationRequested ? ClassifierStopReason.Aborted : ClassifierStopReason.Error,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>【AI】【请求准备】为非流式能力应用默认值并解析认证。</summary>
    /// <param name="provider">所属 provider。</param>
    /// <param name="model">原始模型。</param>
    /// <param name="apiKey">显式凭据。</param>
    /// <param name="environment">环境覆盖。</param>
    /// <param name="signal">取消信号。</param>
    /// <returns>保留实际派生类型的请求模型及认证结果。</returns>
    private async Task<(Model Model, ProviderAuthResult? Auth)> PrepareCapabilityRequestAsync(
        ProviderDefinition provider, Model model, string? apiKey, IReadOnlyDictionary<string, string>? environment, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        var requestModel = ApplyProviderDefaults(provider, model);
        var auth = await ResolveAuthAsync(requestModel, apiKey, environment, signal).ConfigureAwait(false);
        signal.ThrowIfCancellationRequested();
        if (provider.Auth is not null && auth is null)
            throw new ModelsError("auth", $"Provider '{provider.Id}' is not configured.");
        if (auth?.BaseUrl is { Length: > 0 } baseUrl) requestModel = requestModel with { BaseUrl = baseUrl };
        return (requestModel, auth);
    }

    /// <summary>【AI】【图像配置】按配置层级填充缺省选项，保留显式选项与回调。</summary>
    /// <param name="config">按当前模型解析的请求配置。</param>
    /// <param name="options">显式请求选项。</param>
    /// <returns>配置合并后的选项。</returns>
    private static ImagesOptions ApplyConfiguration(ModelRequestConfiguration config, ImagesOptions options)
    {
        return options with
        {
            ApiKey = options.ApiKey ?? config.ApiKey,
            Headers = ConfigurationHeaders(config, options.Headers),
            Env = MergeEnvironment(config.Options.Env, options.Env),
            Timeout = options.Timeout ?? config.Options.Timeout,
            MaxRetries = options.MaxRetries ?? config.Options.MaxRetries,
            MaxRetryDelay = options.MaxRetryDelay ?? config.Options.MaxRetryDelay,
            Metadata = ConfigurationMetadata(config.Options.Metadata, options.Metadata)
        };
    }

    /// <summary>【AI】【分类配置】合并分类调用的超时、温度、环境和请求头。</summary>
    /// <param name="config">按当前模型解析的请求配置。</param>
    /// <param name="options">显式请求选项。</param>
    /// <returns>配置合并后的选项。</returns>
    private static ClassifierOptions ApplyConfiguration(ModelRequestConfiguration config, ClassifierOptions options)
    {
        return options with
        {
            ApiKey = options.ApiKey ?? config.ApiKey,
            Headers = ConfigurationHeaders(config, options.Headers),
            Env = MergeEnvironment(config.Options.Env, options.Env),
            Timeout = options.Timeout ?? config.Options.Timeout,
            MaxRetries = options.MaxRetries ?? config.Options.MaxRetries,
            MaxRetryDelay = options.MaxRetryDelay ?? config.Options.MaxRetryDelay,
            Temperature = options.Temperature ?? config.Options.Temperature,
            Metadata = ConfigurationMetadata(config.Options.Metadata, options.Metadata)
        };
    }

    /// <summary>先收集配置环境，再解析依赖这些环境变量的请求设置。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="environment">调用方环境覆盖。</param>
    /// <returns>解析后的请求配置。</returns>
    private ModelRequestConfiguration ResolveCapabilityConfiguration(Model model, IReadOnlyDictionary<string, string>? environment) =>
        _configurationStore?.ResolveRequestConfiguration(model, environment) ?? ModelRequestConfiguration.Empty;

    /// <summary>合并 provider、模型及请求级配置头。</summary>
    /// <param name="config">已解析配置。</param>
    /// <param name="headers">本次显式请求头。</param>
    /// <returns>新的请求头字典。</returns>
    private static IDictionary<string, string>? ConfigurationHeaders(ModelRequestConfiguration config, IDictionary<string, string>? headers)
    {
        var configured = MergeHeaders(config.Headers, (IReadOnlyDictionary<string, string>?)config.Options.Headers?.ToDictionary(pair => pair.Key, pair => pair.Value));
        return MergeHeaders(configured, headers)?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>【AI】【请求认证】完成认证后补充可选 bearer 头，避免使用认证刷新前的 key。</summary>
    /// <param name="auth">已解析认证。</param>
    /// <param name="headers">配置及显式请求头。</param>
    /// <param name="authHeader">是否配置自动认证头。</param>
    /// <param name="apiKey">解析没有返回 key 时使用的请求 key。</param>
    /// <returns>包含最终认证信息的请求头。</returns>
    private static IDictionary<string, string>? FinalizeCapabilityHeaders(ProviderAuthResult? auth, IDictionary<string, string>? headers, bool authHeader, string? apiKey)
    {
        var result = MergeHeaders(auth?.Headers, headers)?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var key = auth?.ApiKey ?? apiKey;
        if (authHeader && !string.IsNullOrWhiteSpace(key) && !EnvironmentApiKeyResolver.IsAuthenticatedMarker(key))
        {
            result ??= new(StringComparer.OrdinalIgnoreCase);
            result.TryAdd("Authorization", $"Bearer {key}");
        }
        return result;
    }

    /// <summary>合并 metadata 字典，显式字段覆盖配置字段而不修改原字典。</summary>
    /// <param name="configured">配置 metadata。</param>
    /// <param name="explicitMetadata">本次 metadata。</param>
    /// <returns>合并后的 metadata。</returns>
    private static IDictionary<string, object>? ConfigurationMetadata(IDictionary<string, object>? configured, IDictionary<string, object>? explicitMetadata)
    {
        if (configured is null && explicitMetadata is null) return null;
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (configured is not null) foreach (var pair in configured) result[pair.Key] = pair.Value;
        if (explicitMetadata is not null) foreach (var pair in explicitMetadata) result[pair.Key] = pair.Value;
        return result;
    }
}
