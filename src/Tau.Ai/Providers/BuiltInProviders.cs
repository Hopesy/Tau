using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Providers.OpenRouter;
using Tau.Ai.Providers.PiMessages;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

namespace Tau.Ai.Providers;

/// <summary>
/// Registers built-in providers with lazy initialization.
/// </summary>
public static class BuiltInProviders
{
    private static readonly HashSet<string> BuiltInApiNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai-chat-completions",
        "openai-responses",
        "openai-codex-responses",
        "azure-openai-responses",
        "mistral-conversations",
        "anthropic-messages",
        "google-generative-language",
        "google-vertex",
        "google-gemini-cli",
        "bedrock-converse-stream"
        ,"xai"
        ,"pi-messages"
    };

    public static void RegisterAll(
        ProviderRegistry registry,
        ModelConfigurationStore? configurationStore = null,
        HttpClient? configuredProviderHttpClient = null)
    {
        registry.Register("openai-chat-completions", () => new OpenAiProvider(), sourceId: "builtin");
        registry.Register("openai-responses", () => new OpenAiResponsesProvider(), sourceId: "builtin");
        registry.Register("openai-codex-responses", () => new OpenAiCodexResponsesProvider(), sourceId: "builtin");
        registry.Register("azure-openai-responses", () => new AzureOpenAiResponsesProvider(), sourceId: "builtin");
        registry.Register("mistral-conversations", () => new MistralProvider(), sourceId: "builtin");
        registry.Register("anthropic-messages", () => new AnthropicProvider(), sourceId: "builtin");
        registry.Register("google-generative-language", () => new GoogleProvider(), sourceId: "builtin");
        registry.Register("google-vertex", () => new GoogleVertexProvider(), sourceId: "builtin");
        registry.Register("google-gemini-cli", () => new GoogleGeminiCliProvider(), sourceId: "builtin");
        registry.Register("bedrock-converse-stream", () => new BedrockProvider(), sourceId: "builtin");
        registry.Register("pi-messages", () => new PiMessagesProvider(), sourceId: "builtin");
        // 为上游 provider id 保留可直接选择的别名；模型目录仍可使用底层 api 名称
        registry.Register("baseten", () => new ProviderAlias("baseten", new OpenAiCompatibleProvider("baseten", "https://inference.baseten.co/v1")), sourceId: "builtin");
        registry.Register("kimi-coding", () => new ProviderAlias("kimi-coding", new AnthropicProvider()), sourceId: "builtin");
        registry.Register("minimax", () => new ProviderAlias("minimax", new AnthropicProvider()), sourceId: "builtin");
        registry.Register("minimax-cn", () => new ProviderAlias("minimax-cn", new AnthropicProvider()), sourceId: "builtin");
        registry.Register("opencode-go", () => new ProviderAlias("opencode-go", new OpenAiCompatibleProvider("opencode-go", "https://opencode.ai/zen/v1")), sourceId: "builtin");
        registry.Register("qwen-token-plan", () => new ProviderAlias("qwen-token-plan", new OpenAiCompatibleProvider("qwen-token-plan", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1")), sourceId: "builtin");
        registry.Register("qwen-token-plan-cn", () => new ProviderAlias("qwen-token-plan-cn", new OpenAiCompatibleProvider("qwen-token-plan-cn", "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1")), sourceId: "builtin");
        registry.Register("qwen-token-plan-individual", () => new ProviderAlias("qwen-token-plan-individual", new OpenAiCompatibleProvider("qwen-token-plan-individual", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1")), sourceId: "builtin");
        registry.Register("xai", () => new RoutingProvider("xai", [
            ("openai-responses", new OpenAiResponsesProvider(configuredProviderHttpClient))
        ]), sourceId: "builtin");
        registry.Register("vercel-ai-gateway", () => new ProviderAlias("vercel-ai-gateway", new AnthropicProvider()), sourceId: "builtin");
        registry.Register("radius", () => new RadiusProvider(httpClient: configuredProviderHttpClient), sourceId: "builtin");
        // 与 pi 的内置 provider id 保持一一对应。模型的 api 字段仍决定实际协议，
        // 这里为 OpenAI-compatible/Anthropic-compatible provider 提供可直接查找的别名。
        RegisterOpenAiCompatibleAliases(registry, configuredProviderHttpClient);
        RegisterAnthropicAliases(registry, configuredProviderHttpClient);
        RegisterCompositeAliases(registry, configuredProviderHttpClient);
        RegisterConfiguredProviders(registry, configurationStore, configuredProviderHttpClient);
    }

    public static void RegisterOpenAi(ProviderRegistry registry, HttpClient? httpClient = null)
    {
        registry.Register("openai-chat-completions", () => new OpenAiProvider(httpClient), sourceId: "builtin");
    }

    public static void RegisterAnthropic(ProviderRegistry registry, HttpClient? httpClient = null)
    {
        registry.Register("anthropic-messages", () => new AnthropicProvider(httpClient), sourceId: "builtin");
    }

    public static void RegisterGoogle(ProviderRegistry registry, HttpClient? httpClient = null)
    {
        registry.Register("google-generative-language", () => new GoogleProvider(httpClient), sourceId: "builtin");
    }

    public static void RegisterAllImages(ImagesProviderRegistry registry)
    {
        registry.Register("openrouter-images", () => new OpenRouterImagesProvider(), sourceId: "builtin");
    }

    /// <summary>
    /// 创建包含当前内置模型目录和协议实现的统一模型集合。
    /// </summary>
    /// <param name="configurationStore">可选模型配置覆盖。</param>
    /// <param name="authResolver">可选认证解析器。</param>
    /// <param name="httpClient">可选共享 HTTP 客户端。</param>
    /// <returns>可直接用于模型查找、认证和流式请求的集合。</returns>
    public static Models CreateBuiltInModels(
        ModelConfigurationStore? configurationStore = null,
        ProviderAuthResolver? authResolver = null,
        HttpClient? httpClient = null)
    {
        var effectiveAuthResolver = authResolver ?? new ProviderAuthResolver(configurationStore: configurationStore);
        var registry = new ProviderRegistry();
        RegisterAll(registry, configurationStore, httpClient);
        var catalog = new ModelCatalog(effectiveAuthResolver, configurationStore);
        var providers = new List<ProviderDefinition>();
        foreach (var providerId in catalog.GetProviders())
        {
            var models = catalog.GetModels(providerId);
            if (models.Count == 0)
            {
                continue;
            }

            // provider id 优先于模型 api：同一协议下的不同 provider 需要各自的默认地址、认证和请求变换
            var implementation = registry.TryGet(providerId) ?? registry.TryGet(models[0].Api);
            if (implementation is null)
            {
                continue;
            }

            Func<CancellationToken, Task<IReadOnlyList<Model>>>? refreshModels = null;
            if (implementation is RadiusProvider radius)
            {
                refreshModels = cancellationToken => radius.RefreshModelsAsync(
                    providerId,
                    effectiveAuthResolver.ResolveApiKey(providerId),
                    allowNetwork: true,
                    cancellationToken);
            }

            var providerAuth = CreateProviderAuth(providerId, effectiveAuthResolver);

            providers.Add(new ProviderDefinition(
                providerId,
                implementation,
                models,
                providerId,
                refreshModels: refreshModels,
                auth: providerAuth));
        }

        return new Models(providers, effectiveAuthResolver);
    }

    /// <summary>
    /// 为没有独立 OAuth 流程的内置 provider 创建环境/API-key 认证定义。
    /// </summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="authResolver">兼容 auth.json、models.json 和环境变量的旧解析器。</param>
    /// <returns>provider 自有认证定义；需要保留专用 OAuth 行为时返回 null。</returns>
    private static ProviderAuthDefinition? CreateProviderAuth(string providerId, ProviderAuthResolver authResolver)
    {
        var displayName = providerId switch
        {
            "amazon-bedrock" => "Amazon Bedrock credentials",
            "google-vertex" => "Google Cloud credentials",
            "radius" => "Radius API key",
            _ => $"{providerId} API key"
        };

        var apiKey = new ApiKeyAuthDefinition(
            displayName,
            resolve: context =>
            {
                var key = context.Credential?.Key;
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = authResolver.ResolveApiKey(providerId, env: context.Environment);
                }

                if (string.IsNullOrWhiteSpace(key))
                {
                    return Task.FromResult<ProviderAuthResult?>(null);
                }

                var legacyOAuth = authResolver.GetStoredAuthEntry(providerId)?.OAuth;
                if (legacyOAuth is not null && providerId is ("anthropic" or "github-copilot"))
                {
                    return Task.FromResult<ProviderAuthResult?>(new ProviderAuthResult(
                        EnvironmentApiKeyResolver.AuthenticatedMarker,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Authorization"] = $"Bearer {key}"
                        },
                        Env: context.Credential?.Env,
                        Source: "OAuth"));
                }

                var source = EnvironmentApiKeyResolver.IsAuthenticatedMarker(key)
                    ? "ambient"
                    : context.Credential is not null ? "stored credential" : "environment or auth.json";
                return Task.FromResult<ProviderAuthResult?>(new ProviderAuthResult(
                    key,
                    Env: context.Credential?.Env,
                    Source: source));
            },
            check: context =>
            {
                var key = context.Credential?.Key;
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = authResolver.ResolveApiKey(providerId, env: context.Environment);
                }

                return Task.FromResult<ProviderAuthStatus?>(new ProviderAuthStatus(
                    providerId,
                    !string.IsNullOrWhiteSpace(key),
                    EnvironmentApiKeyResolver.IsAuthenticatedMarker(key) ? "ambient" : "api_key",
                    UsesOAuth: false,
                    CanLogin: true,
                    string.IsNullOrWhiteSpace(key) ? "No credentials found." : "Credentials are available."));
            },
            login: async interaction => new ApiKeyCredential(await interaction.PromptAsync($"Enter {displayName}").ConfigureAwait(false)));

        return new ProviderAuthDefinition(apiKey: apiKey, oauth: CreateOAuthAuth(providerId, authResolver));
    }

    /// <summary>将旧 OAuth provider 适配到 Models provider-owned auth 契约。</summary>
    /// <param name="providerId">provider id。</param>
    /// <param name="authResolver">包含 OAuth provider 注册表的解析器。</param>
    /// <returns>OAuth 定义；没有注册 OAuth 实现时返回 null。</returns>
    private static OAuthAuthDefinition? CreateOAuthAuth(string providerId, ProviderAuthResolver authResolver)
    {
        var oauthProvider = authResolver.GetOAuthProvider(providerId);
        if (oauthProvider is null)
        {
            return null;
        }

        return new OAuthAuthDefinition(
            oauthProvider.Name,
            login: async interaction =>
            {
                var callbacks = new AuthInteractionCallbacks(interaction);
                var credentials = await oauthProvider.LoginAsync(callbacks, interaction.CancellationToken).ConfigureAwait(false);
                return new ProviderOAuthCredential(credentials.Refresh, credentials.Access, credentials.ExpiresAt, credentials.Metadata);
            },
            refresh: async (credential, cancellationToken) =>
            {
                var credentials = new OAuthCredentials
                {
                    Refresh = credential.Refresh,
                    Access = credential.Access,
                    ExpiresAt = credential.ExpiresAt,
                    Metadata = credential.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };
                var refreshed = await oauthProvider.RefreshTokenAsync(credentials, cancellationToken).ConfigureAwait(false);
                return new ProviderOAuthCredential(refreshed.Refresh, refreshed.Access, refreshed.ExpiresAt, refreshed.Metadata);
            },
            toAuth: credential =>
            {
                var credentials = new OAuthCredentials
                {
                    Refresh = credential.Refresh,
                    Access = credential.Access,
                    ExpiresAt = credential.ExpiresAt,
                    Metadata = credential.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };
                var token = oauthProvider.GetApiKey(credentials);
                if (providerId is "anthropic" or "github-copilot")
                {
                    return Task.FromResult(new ProviderAuthResult(
                        EnvironmentApiKeyResolver.AuthenticatedMarker,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Authorization"] = $"Bearer {token}"
                        },
                        Source: "OAuth"));
                }

                return Task.FromResult(new ProviderAuthResult(token, Source: "OAuth"));
            });
    }

    /// <summary>把 Models 交互回调桥接到旧 OAuth 登录回调接口。</summary>
    private sealed class AuthInteractionCallbacks(AuthInteraction interaction) : IOAuthLoginCallbacks
    {
        public void OnAuth(string url, string? instructions = null) => interaction.Notify(
            string.IsNullOrWhiteSpace(instructions) ? url : $"{url}\n{instructions}");

        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) =>
            interaction.PromptAsync(string.IsNullOrWhiteSpace(placeholder) ? message : $"{message} ({placeholder})");

        public void OnProgress(string message) => interaction.Notify(message);

        /// <summary>通过 Models 交互回调读取手工粘贴的 OAuth 授权码。</summary>
        /// <returns>授权码或完整重定向 URL。</returns>
        public Task<string>? OnManualCodeInputAsync() =>
            interaction.PromptAsync("Paste the authorization code or full redirect URL:");
    }

    public static void RegisterOpenRouterImages(ImagesProviderRegistry registry, HttpClient? httpClient = null)
    {
        registry.Register("openrouter-images", () => new OpenRouterImagesProvider(httpClient), sourceId: "builtin");
    }

    public static ImagesProviderRegistry CreateBuiltInImagesRegistry(HttpClient? openRouterHttpClient = null)
    {
        var registry = new ImagesProviderRegistry();
        RegisterOpenRouterImages(registry, openRouterHttpClient);
        return registry;
    }

    public static IReadOnlyList<ImagesProviderDefinition> GetBuiltInImagesProviders(
        ImageModelCatalog? catalog = null,
        HttpClient? openRouterHttpClient = null) =>
        [CreateOpenRouterImagesProvider(catalog, openRouterHttpClient)];

    public static ImagesModels CreateBuiltInImagesModels(
        ImageModelCatalog? catalog = null,
        HttpClient? openRouterHttpClient = null,
        ProviderAuthResolver? authResolver = null,
        ModelConfigurationStore? configurationStore = null)
    {
        var models = new ImagesModels(authResolver: authResolver, configurationStore: configurationStore);
        foreach (var provider in GetBuiltInImagesProviders(catalog, openRouterHttpClient))
        {
            models.SetProvider(provider);
        }

        return models;
    }

    public static ImagesProviderDefinition CreateOpenRouterImagesProvider(
        ImageModelCatalog? catalog = null,
        HttpClient? httpClient = null) =>
        new(
            "openrouter",
            new OpenRouterImagesProvider(httpClient),
            (catalog ?? new ImageModelCatalog()).GetModels("openrouter"),
            "OpenRouter");

    public static void RegisterConfiguredProviders(
        ProviderRegistry registry,
        ModelConfigurationStore? configurationStore = null,
        HttpClient? httpClient = null)
    {
        var registeredApis = registry.RegisteredApis.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in (configurationStore ?? new ModelConfigurationStore()).GetDynamicProviderRegistrations())
        {
            if (BuiltInApiNames.Contains(provider.Api) || registeredApis.Contains(provider.Api))
            {
                continue;
            }

            registry.Register(
                provider.Api,
                () => new OpenAiCompatibleProvider(
                    provider.Api,
                    provider.BaseUrl,
                    provider.RequestPath,
                    httpClient: httpClient),
                sourceId: "models.json");
            registeredApis.Add(provider.Api);
        }
    }

    private sealed class ProviderAlias(string api, IStreamProvider inner) : IStreamProvider
    {
        public string Api => api;
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) => inner.Stream(model with { Api = inner.Api }, context, options);
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => inner.StreamSimple(model with { Api = inner.Api }, context, options);
        public AssistantMessageStream FetchDeferred(Model model, DeferredHandle handle, DeferredFetchOptions options) => inner.FetchDeferred(model with { Api = inner.Api }, handle with { Api = inner.Api }, options);
        public Task CancelDeferred(Model model, DeferredHandle handle, DeferredCancelOptions options) => inner.CancelDeferred(model with { Api = inner.Api }, handle with { Api = inner.Api }, options);
    }

    /// <summary>
    /// 注册使用 OpenAI Chat Completions 协议的内置 provider 别名。
    /// </summary>
    /// <param name="registry">目标 provider 注册表。</param>
    /// <param name="httpClient">可选共享 HTTP 客户端。</param>
    private static void RegisterOpenAiCompatibleAliases(ProviderRegistry registry, HttpClient? httpClient)
    {
        var definitions = new (string Id, string BaseUrl)[]
        {
            ("ant-ling", "https://api.antgroup.com/v1"),
            ("baseten", "https://inference.baseten.co/v1"),
            ("cerebras", "https://api.cerebras.ai/v1"),
            ("cloudflare-workers-ai", "https://api.cloudflare.com/client/v4"),
            ("deepseek", "https://api.deepseek.com/v1"),
            ("groq", "https://api.groq.com/openai/v1"),
            ("huggingface", "https://router.huggingface.co/v1"),
            ("moonshotai", "https://api.moonshot.ai/v1"),
            ("moonshotai-cn", "https://api.moonshot.cn/v1"),
            ("nvidia", "https://integrate.api.nvidia.com/v1"),
            ("openrouter", "https://openrouter.ai/api/v1"),
            ("opencode-go", "https://opencode.ai/zen/v1"),
            ("qwen-token-plan", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1"),
            ("qwen-token-plan-cn", "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1"),
            ("qwen-token-plan-individual", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1"),
            ("together", "https://api.together.xyz/v1"),
            ("xiaomi", "https://api.xiaomimimo.com/v1"),
            ("xiaomi-token-plan-cn", "https://api.xiaomimimo.com/v1"),
            ("xiaomi-token-plan-ams", "https://api.xiaomimimo.com/v1"),
            ("xiaomi-token-plan-sgp", "https://api.xiaomimimo.com/v1"),
            ("zai", "https://api.z.ai/api/paas/v4"),
            ("zai-coding-cn", "https://open.bigmodel.cn/api/paas/v4")
        };

        foreach (var (id, baseUrl) in definitions)
        {
            registry.Register(id, () => new ProviderAlias(id, new OpenAiCompatibleProvider(id, baseUrl, httpClient: httpClient)), sourceId: "builtin");
        }
    }

    /// <summary>
    /// 注册使用 Anthropic Messages 协议的内置 provider 别名。
    /// </summary>
    /// <param name="registry">目标 provider 注册表。</param>
    /// <param name="httpClient">可选共享 HTTP 客户端。</param>
    private static void RegisterAnthropicAliases(ProviderRegistry registry, HttpClient? httpClient)
    {
        var definitions = new (string Id, string BaseUrl)[]
        {
            ("kimi-coding", "https://api.kimi.com/coding"),
            ("minimax", "https://api.minimax.io/anthropic"),
            ("minimax-cn", "https://api.minimaxi.com/anthropic"),
            ("vercel-ai-gateway", "https://ai-gateway.vercel.sh")
        };

        foreach (var (id, baseUrl) in definitions)
        {
            registry.Register(id, () => new ProviderAlias(id, new AnthropicProvider(httpClient)), sourceId: "builtin");
        }
    }

    /// <summary>
    /// 注册一个 provider 下同时暴露多种 API 协议的路由实现。
    /// </summary>
    /// <param name="registry">目标 provider 注册表。</param>
    /// <param name="httpClient">可选共享 HTTP 客户端。</param>
    private static void RegisterCompositeAliases(ProviderRegistry registry, HttpClient? httpClient)
    {
        var openAi = new OpenAiCompatibleProvider("openai-chat-completions", "https://opencode.ai/zen/v1", httpClient: httpClient);
        var openAiResponses = new OpenAiResponsesProvider(httpClient);
        var anthropic = new AnthropicProvider(httpClient);
        var google = new GoogleProvider(httpClient);
        var fireworksOpenAi = new OpenAiCompatibleProvider("openai-chat-completions", "https://api.fireworks.ai/inference", httpClient: httpClient);
        var cloudflareOpenAi = new OpenAiCompatibleProvider("openai-chat-completions", "https://gateway.ai.cloudflare.com", httpClient: httpClient);
        var cloudflareResponses = new OpenAiResponsesProvider(httpClient);
        var cloudflareAnthropic = new AnthropicProvider(httpClient);
        var cloudflareWorkersOpenAi = new OpenAiCompatibleProvider("openai-chat-completions", "https://api.cloudflare.com/client/v4", httpClient: httpClient);
        var openAiChat = new OpenAiProvider(httpClient);
        var githubAnthropic = new AnthropicProvider(httpClient);
        var githubResponses = new OpenAiResponsesProvider(httpClient);

        registry.Register("openai", () => new RoutingProvider("openai", [
            ("openai-chat-completions", openAiChat),
            ("openai-responses", openAiResponses)
        ]), sourceId: "builtin");
        registry.Register("github-copilot", () => new RoutingProvider("github-copilot", [
            ("anthropic-messages", githubAnthropic),
            ("openai-chat-completions", openAiChat),
            ("openai-responses", githubResponses)
        ]), sourceId: "builtin");
        registry.Register("opencode", () => new RoutingProvider("opencode", [
            ("openai-chat-completions", openAi),
            ("openai-responses", openAiResponses),
            ("anthropic-messages", anthropic),
            ("google-generative-language", google)
        ]), sourceId: "builtin");
        registry.Register("opencode-go", () => new RoutingProvider("opencode-go", [
            ("openai-chat-completions", openAi),
            ("openai-responses", openAiResponses),
            ("anthropic-messages", anthropic)
        ]), sourceId: "builtin");
        registry.Register("fireworks", () => new RoutingProvider("fireworks", [
            ("openai-chat-completions", fireworksOpenAi),
            ("anthropic-messages", anthropic)
        ]), sourceId: "builtin");
        registry.Register("cloudflare-ai-gateway", () => new RoutingProvider("cloudflare-ai-gateway", [
            ("openai-chat-completions", cloudflareOpenAi),
            ("openai-responses", cloudflareResponses),
            ("anthropic-messages", cloudflareAnthropic)
        ], resolveCloudflarePlaceholders: true), sourceId: "builtin");
        registry.Register("cloudflare-workers-ai", () => new RoutingProvider("cloudflare-workers-ai", [
            ("openai-chat-completions", cloudflareWorkersOpenAi)
        ], resolveCloudflarePlaceholders: true), sourceId: "builtin");
    }

    /// <summary>
    /// 按模型声明的 API 将同一 provider 的请求转发到对应协议实现。
    /// </summary>
    private sealed class RoutingProvider : IStreamProvider
    {
        private readonly string _providerId;
        private readonly IReadOnlyDictionary<string, IStreamProvider> _routes;
        private readonly bool _resolveCloudflarePlaceholders;

        public RoutingProvider(
            string providerId,
            IEnumerable<(string Api, IStreamProvider Provider)> routes,
            bool resolveCloudflarePlaceholders = false)
        {
            _providerId = providerId;
            _routes = routes.ToDictionary(item => item.Api, item => item.Provider, StringComparer.OrdinalIgnoreCase);
            _resolveCloudflarePlaceholders = resolveCloudflarePlaceholders;
        }

        public string Api => _providerId;

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Resolve(model, options).Stream(PrepareModel(model, options), context, PrepareOptions(options));

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Resolve(model, options).StreamSimple(PrepareModel(model, options), context, PrepareSimpleOptions(options));

        public AssistantMessageStream FetchDeferred(Model model, DeferredHandle handle, DeferredFetchOptions options) =>
            Resolve(model, options).FetchDeferred(PrepareModel(model, options), PrepareHandle(model, handle), options);

        public Task CancelDeferred(Model model, DeferredHandle handle, DeferredCancelOptions options) =>
            Resolve(model, options).CancelDeferred(PrepareModel(model, options), PrepareHandle(model, handle), options);

        private IStreamProvider Resolve(Model model, StreamOptions options) =>
            _routes.TryGetValue(ModelApiNames.Normalize(model.Api) ?? model.Api, out var provider)
                ? provider
                : throw new KeyNotFoundException($"Provider '{_providerId}' does not support API '{model.Api}'.");

        private Model PrepareModel(Model model, StreamOptions options)
        {
            if (!_resolveCloudflarePlaceholders || string.IsNullOrWhiteSpace(model.BaseUrl)) return model;
            var env = options.Env;
            var account = env is not null && env.TryGetValue("CLOUDFLARE_ACCOUNT_ID", out var accountValue)
                ? accountValue
                : Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID");
            var gateway = env is not null && env.TryGetValue("CLOUDFLARE_GATEWAY_ID", out var gatewayValue)
                ? gatewayValue
                : Environment.GetEnvironmentVariable("CLOUDFLARE_GATEWAY_ID");
            var baseUrl = model.BaseUrl
                .Replace("{CLOUDFLARE_ACCOUNT_ID}", account ?? "{CLOUDFLARE_ACCOUNT_ID}", StringComparison.Ordinal)
                .Replace("{CLOUDFLARE_GATEWAY_ID}", gateway ?? "{CLOUDFLARE_GATEWAY_ID}", StringComparison.Ordinal);
            return baseUrl.Equals(model.BaseUrl, StringComparison.Ordinal) ? model : model with { BaseUrl = baseUrl };
        }

        private DeferredHandle PrepareHandle(Model model, DeferredHandle handle) =>
            handle.Api.Equals(model.Api, StringComparison.OrdinalIgnoreCase)
                ? handle
                : handle with { Api = model.Api };

        private StreamOptions PrepareOptions(StreamOptions options)
        {
            if (!_providerId.Equals("cloudflare-ai-gateway", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(options.ApiKey) ||
                EnvironmentApiKeyResolver.IsAuthenticatedMarker(options.ApiKey))
            {
                return options;
            }

            var headers = options.Headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);
            headers["cf-aig-authorization"] = $"Bearer {options.ApiKey}";
            headers.Remove("Authorization");
            headers.Remove("x-api-key");
            return options with
            {
                ApiKey = EnvironmentApiKeyResolver.AuthenticatedMarker,
                Headers = headers
            };
        }

        private SimpleStreamOptions PrepareSimpleOptions(SimpleStreamOptions options) =>
            (SimpleStreamOptions)PrepareOptions(options);
    }
}
