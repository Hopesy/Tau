using System.Text.Json;
using Tau.Ai.Streaming;
using Tau.Ai.Auth;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Cloudflare;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

/// <summary>
/// Top-level convenience functions for streaming LLM responses.
/// Mirrors pi-main's stream.ts exports.
/// </summary>
public static class StreamFunctions
{
    private static readonly ProviderAuthResolver AuthResolver = new();

    /// <summary>【AI】【流式请求】重放系统消息并解析配置后启动普通请求。</summary>
    /// <param name="registry">协议注册表。</param>
    /// <param name="model">请求模型。</param>
    /// <param name="context">包含旧字段或系统消息的上下文。</param>
    /// <param name="options">请求选项。</param>
    /// <param name="configurationStore">可选配置存储。</param>
    /// <param name="authResolver">可选认证解析器。</param>
    /// <returns>助手事件流。</returns>
    public static AssistantMessageStream Stream(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        StreamOptions options,
        ModelConfigurationStore? configurationStore = null,
        ProviderAuthResolver? authResolver = null)
    {
        Model resolvedModel;
        StreamOptions resolvedOptions;
        try
        {
            options.Signal.ThrowIfCancellationRequested();
            var authResolvedModel = (authResolver ?? AuthResolver).ResolveModel(model);
            resolvedOptions = ResolveOptions(
                authResolvedModel,
                options,
                configurationStore,
                authResolver,
                applyProviderSpecificOptions: true,
                out _,
                out resolvedModel, out _);
        }
        catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
        {
            return CreateAbortedStream(model);
        }
        catch (ProviderAuthException ex)
        {
            return CreateAuthErrorStream(model, ex);
        }

        var provider = registry.Get(resolvedModel);
        return provider.Stream(resolvedModel, provider.SupportsTranscriptContext
            ? Transcript.NormalizeContext(context) : Transcript.ResolveContext(context), resolvedOptions);
    }

    /// <summary>【AI】【流式请求】重放系统消息并解析配置后启动简化请求。</summary>
    /// <param name="registry">协议注册表。</param>
    /// <param name="model">请求模型。</param>
    /// <param name="context">包含旧字段或系统消息的上下文。</param>
    /// <param name="options">请求选项。</param>
    /// <param name="configurationStore">可选配置存储。</param>
    /// <param name="authResolver">可选认证解析器。</param>
    /// <param name="onRequestPrepared">提供方启动前观察实际模型、上下文、已合并的简化选项及最终协议选项。</param>
    /// <returns>助手事件流。</returns>
    public static AssistantMessageStream StreamSimple(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        SimpleStreamOptions options,
        ModelConfigurationStore? configurationStore = null,
        ProviderAuthResolver? authResolver = null,
        Action<Model, LlmContext, SimpleStreamOptions, StreamOptions>? onRequestPrepared = null)
    {
        Model resolvedModel;
        SimpleStreamOptions resolvedOptions;
        ModelProviderSpecificOptionsConfiguration? providerSpecific;
        try
        {
            options.Signal.ThrowIfCancellationRequested();
            var authResolvedModel = (authResolver ?? AuthResolver).ResolveModel(model);
            resolvedOptions = (SimpleStreamOptions)ResolveOptions(
                authResolvedModel,
                options,
                configurationStore,
                authResolver,
                applyProviderSpecificOptions: false,
                out providerSpecific,
                out resolvedModel, out _);
        }
        catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
        {
            return CreateAbortedStream(model);
        }
        catch (ProviderAuthException ex)
        {
            return CreateAuthErrorStream(model, ex);
        }

        var provider = registry.Get(resolvedModel);
        if (SimpleTokenOptions.UsesContextLimit(resolvedModel.Api))
            resolvedOptions = SimpleTokenOptions.WithContextLimit(resolvedModel, context, resolvedOptions);
        context = provider.SupportsTranscriptContext ? Transcript.NormalizeContext(context) : Transcript.ResolveContext(context);
        if (providerSpecific is not null &&
            TryCreateProviderSpecificSimpleOptions(resolvedModel, context, resolvedOptions, options, providerSpecific, out var providerOptions))
        {
            onRequestPrepared?.Invoke(resolvedModel, context, resolvedOptions, providerOptions);
            return provider.Stream(resolvedModel, context, providerOptions);
        }

        onRequestPrepared?.Invoke(resolvedModel, context, resolvedOptions, resolvedOptions);
        return provider.StreamSimple(resolvedModel, context, resolvedOptions);
    }

    /// <summary>【AI】【流式取消】认证准备阶段取消时，返回与提供方流一致的终止助手消息。</summary>
    /// <param name="model">请求模型。</param><returns>包含取消错误事件的完成流。</returns>
    private static AssistantMessageStream CreateAbortedStream(Model model)
    {
        var stream = new AssistantMessageStream();
        var message = StreamOptionHelpers.CreateAbortedMessage(model, model.Api);
        stream.Push(new ErrorEvent(message.ErrorMessage ?? "Request was aborted", Message: message));
        return stream;
    }

    /// <summary>【AI】【流式认证错误】把认证失败转为有模型元数据的错误流。</summary>
    /// <param name="model">请求模型。</param><param name="exception">认证错误。</param><returns>包含错误助手消息的完成流。</returns>
    private static AssistantMessageStream CreateAuthErrorStream(Model model, ProviderAuthException exception)
    {
        var stream = new AssistantMessageStream();
        var message = new AssistantMessage
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Content = [],
            StopReason = StopReason.Error,
            ErrorMessage = exception.Message,
            Timestamp = DateTimeOffset.UtcNow
        };
        stream.Push(new ErrorEvent(exception.Message, Message: message));
        return stream;
    }

    public static async Task<AssistantMessage> CompleteAsync(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        StreamOptions options,
        ModelConfigurationStore? configurationStore = null,
        ProviderAuthResolver? authResolver = null)
    {
        var stream = Stream(registry, model, context, options, configurationStore, authResolver);
        return await stream.ResultAsync.ConfigureAwait(false);
    }

    public static async Task<AssistantMessage> CompleteSimpleAsync(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        SimpleStreamOptions options,
        ModelConfigurationStore? configurationStore = null,
        ProviderAuthResolver? authResolver = null)
    {
        var stream = StreamSimple(registry, model, context, options, configurationStore, authResolver);
        return await stream.ResultAsync.ConfigureAwait(false);
    }

    /// <summary>【AI】【请求认证】复用实际请求配置合并，返回有效密钥、请求头、环境及动态地址。</summary>
    /// <param name="model">目标模型。</param><param name="options">显式请求选项。</param><param name="configurationStore">模型配置。</param>
    /// <param name="authResolver">认证上下文。</param><returns>实际请求的认证配置。</returns>
    public static ProviderAuthResult ResolveRequestAuthentication(Model model, StreamOptions options, ModelConfigurationStore configurationStore, ProviderAuthResolver authResolver)
    {
        var resolvedModel = authResolver.ResolveModel(model);
        var resolved = ResolveOptions(resolvedModel, options, configurationStore, authResolver, false, out _, out var requestModel, out var auth);
        if (configurationStore.ResolveRequestConfiguration(model, options.Env).AuthHeader && string.IsNullOrWhiteSpace(resolved.ApiKey)
            && auth.Headers is not { Count: > 0 })
            throw new ProviderAuthException("api_key", $"No API key found for \"{model.Provider}\"");
        return new(resolved.ApiKey, resolved.Headers is null ? null : new Dictionary<string, string>(resolved.Headers, StringComparer.OrdinalIgnoreCase),
            requestModel.BaseUrl != model.BaseUrl ? requestModel.BaseUrl : null, resolved.Env, auth.Source ?? authResolver.GetStatus(model).Source);
    }

    private static StreamOptions ResolveOptions(
        Model model,
        StreamOptions options,
        ModelConfigurationStore? configurationStore,
        ProviderAuthResolver? authResolver,
        bool applyProviderSpecificOptions,
        out ModelProviderSpecificOptionsConfiguration? providerSpecific,
        out Model requestModel, out ProviderAuthResult requestAuth)
    {
        var resolver = authResolver ?? AuthResolver;
        var store = configurationStore ?? new ModelConfigurationStore();
        requestModel = model;
        var requestConfig = store.ResolveRequestConfiguration(model, options.Env);
        var env = ProviderEnvironment.Merge(requestConfig.Options.Env, options.Env);
        if (env is not null)
        {
            requestConfig = store.ResolveRequestConfiguration(model, env);
            env = ProviderEnvironment.Merge(requestConfig.Options.Env, options.Env);
        }

        providerSpecific = requestConfig.Options.ProviderSpecific;
        var auth = resolver.ResolveRequestAuth(model.Provider, options.ApiKey, env, options.Signal);
        requestAuth = auth;
        env = ProviderEnvironment.Merge(auth.Env, env);
        // 1. 【AI】【认证归属】提供方已拒绝或已选择其他认证时，禁止在最终请求中重新注入配置密钥
        var apiKey = auth.ApiKey ?? (auth.SuppressConfiguredApiKey ? null : requestConfig.ApiKey);
        if (!string.IsNullOrWhiteSpace(auth.BaseUrl)) requestModel = model with { BaseUrl = auth.BaseUrl };
        var configuredHeaders = MergeHeaders(requestConfig.Headers, requestConfig.Options.Headers);
        var headers = MergeHeaders(MergeHeaders(auth.Headers is null ? null : new Dictionary<string, string>(auth.Headers, StringComparer.OrdinalIgnoreCase), configuredHeaders), options.Headers);
        var metadata = MergeMetadata(requestConfig.Options.Metadata, options.Metadata);
        if (requestConfig.AuthHeader &&
            !string.IsNullOrWhiteSpace(apiKey) &&
            !EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers.TryAdd("Authorization", $"Bearer {apiKey}");
        }

        var resolved = options with
        {
            Temperature = options.Temperature ?? requestConfig.Options.Temperature,
            MaxTokens = options.MaxTokens ?? requestConfig.Options.MaxTokens,
            TopP = options.TopP ?? requestConfig.Options.TopP,
            ApiKey = apiKey,
            SessionId = options.SessionId ?? requestConfig.Options.SessionId,
            Headers = headers,
            Timeout = options.Timeout ?? requestConfig.Options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay ?? requestConfig.Options.MaxRetryDelay,
            MaxRetries = options.MaxRetries ?? requestConfig.Options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout ?? requestConfig.Options.WebSocketConnectTimeout,
            Metadata = metadata,
            Env = env,
            SamplingParams = MergeSamplingParams(requestConfig.Options.SamplingParams, options.SamplingParams)
        };

        if (!options.HasExplicitTransport && requestConfig.Options.Transport is { } transport)
        {
            resolved = resolved with { Transport = transport };
        }

        if (!options.HasExplicitCacheRetention && requestConfig.Options.CacheRetention is { } cacheRetention)
        {
            resolved = resolved with { CacheRetention = cacheRetention };
        }
        else if (!options.HasExplicitCacheRetention)
        {
            resolved = resolved with { CacheRetention = ProviderEnvironment.GetValue("PI_CACHE_RETENTION", env) == "long"
                ? CacheRetention.Long : CacheRetention.Short };
        }

        if (resolved is SimpleStreamOptions simple && requestConfig.Options is { } configuredOptions)
        {
            resolved = simple with
            {
                Reasoning = simple.Reasoning ?? configuredOptions.Reasoning,
                ThinkingBudgets = MergeThinkingBudgets(configuredOptions.ThinkingBudgets, simple.ThinkingBudgets)
            };
        }

        resolved = CloudflareAuthResolver.Resolve(
            requestModel,
            resolved,
            resolver,
            env,
            options.ApiKey,
            apiKey,
            out requestModel);

        return applyProviderSpecificOptions
            ? ApplyProviderSpecificOptions(requestModel, resolved, requestConfig.Options.ProviderSpecific)
            : resolved;
    }

    /// <summary>【AI】【生成选项】将合并配置后的通用选项转换为供应商专用选项。</summary>
    /// <param name="model">实际请求模型。</param>
    /// <param name="context">规范化后的上下文，用于生成预算估算。</param>
    /// <param name="resolvedOptions">合并默认值后的通用选项。</param>
    /// <param name="explicitOptions">调用方显式选项。</param>
    /// <param name="configured">供应商配置。</param>
    /// <param name="providerOptions">输出转换后的选项。</param>
    /// <returns>支持专用选项转换时为 true。</returns>
    private static bool TryCreateProviderSpecificSimpleOptions(
        Model model,
        LlmContext context,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured,
        out StreamOptions providerOptions)
    {
        providerOptions = model.Api switch
        {
            "openai-chat-completions" => CreateOpenAiOptions(model, resolvedOptions, explicitOptions, configured),
            "openai-responses" => CreateOpenAiResponsesOptions(model, resolvedOptions, explicitOptions, configured),
            "openai-codex-responses" => CreateOpenAiCodexResponsesOptions(model, resolvedOptions, explicitOptions, configured),
            "azure-openai-responses" => CreateAzureOpenAiResponsesOptions(model, resolvedOptions, explicitOptions, configured),
            "anthropic-messages" => CreateAnthropicOptions(model, context, resolvedOptions, explicitOptions, configured),
            "mistral-conversations" => CreateMistralOptions(model, resolvedOptions, explicitOptions, configured),
            "google-generative-language" => CreateGoogleOptions(model, resolvedOptions, explicitOptions, configured),
            "google-vertex" => CreateGoogleVertexOptions(model, resolvedOptions, explicitOptions, configured),
            "google-gemini-cli" => CreateGoogleGeminiCliOptions(model, resolvedOptions, explicitOptions, configured),
            "bedrock-converse-stream" => CreateBedrockOptions(model, resolvedOptions, explicitOptions, configured),
            _ => resolvedOptions
        };

        return !ReferenceEquals(providerOptions, resolvedOptions);
    }

    private static OpenAiOptions CreateOpenAiOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        new()
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            Deferred = resolvedOptions.Deferred,
            ToolChoice = ToOpenAiToolChoice(explicitOptions.ToolChoice) ?? ToOpenAiToolChoice(configured.ToolChoice),
            ThinkingBudgets = resolvedOptions.ThinkingBudgets,
            ReasoningEffort = ResolveOpenAiReasoningEffort(model, resolvedOptions, explicitOptions, configured)
        };

    /// <summary>【AI】【Responses 配置】合并简化入口选项并保留显式工具选择。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="resolvedOptions">已合并的通用选项。</param>
    /// <param name="explicitOptions">调用方显式选项。</param>
    /// <param name="configured">供应商专用配置。</param>
    /// <returns>Responses 专用请求选项。</returns>
    private static OpenAiResponsesOptions CreateOpenAiResponsesOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        new()
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            Deferred = resolvedOptions.Deferred,
            ReasoningEffort = ResolveReasoningEffort(model, resolvedOptions, explicitOptions, configured),
            ReasoningSummary = configured.ReasoningSummary,
            ServiceTier = configured.ServiceTier,
            ToolChoice = explicitOptions.ToolChoice ?? ToResponsesToolChoice(configured.ToolChoice)
        };

    private static OpenAiCodexResponsesOptions CreateOpenAiCodexResponsesOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        new()
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            Deferred = resolvedOptions.Deferred,
            ReasoningEffort = ResolveReasoningEffort(model, resolvedOptions, explicitOptions, configured),
            ReasoningSummary = configured.ReasoningSummary,
            ServiceTier = configured.ServiceTier,
            TextVerbosity = configured.TextVerbosity
        };

    /// <summary>【AI】【Azure Responses 配置】保留简化入口的工具选择和 Azure 部署参数。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="resolvedOptions">已合并的通用选项。</param>
    /// <param name="explicitOptions">调用方显式选项。</param>
    /// <param name="configured">供应商专用配置。</param>
    /// <returns>Azure 专用请求选项。</returns>
    private static AzureOpenAiResponsesOptions CreateAzureOpenAiResponsesOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        new()
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            Deferred = resolvedOptions.Deferred,
            ReasoningEffort = ResolveReasoningEffort(model, resolvedOptions, explicitOptions, configured),
            ReasoningSummary = configured.ReasoningSummary,
            AzureApiVersion = configured.AzureApiVersion,
            AzureResourceName = configured.AzureResourceName,
            AzureBaseUrl = configured.AzureBaseUrl,
            AzureDeploymentName = configured.AzureDeploymentName,
            ToolChoice = explicitOptions.ToolChoice ?? ToResponsesToolChoice(configured.ToolChoice)
        };

    private static MistralOptions CreateMistralOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        new()
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            Deferred = resolvedOptions.Deferred,
            ToolChoice = ToMistralToolChoice(explicitOptions.ToolChoice) ?? ToMistralToolChoice(configured.ToolChoice),
            PromptMode = ResolveMistralPromptMode(model, resolvedOptions, explicitOptions, configured),
            ReasoningEffort = ResolveMistralReasoningEffort(model, resolvedOptions, explicitOptions, configured)
        };

    private static string? ResolveReasoningEffort(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        if (explicitOptions.Reasoning is { } explicitReasoning)
        {
            return OpenAiResponsesShared.MapReasoningEffort(explicitReasoning, model);
        }

        return configured.ReasoningEffort ?? OpenAiResponsesShared.MapReasoningEffort(resolvedOptions.Reasoning, model);
    }

    private static string? ResolveMistralPromptMode(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        if (explicitOptions.Reasoning is not null)
        {
            return MistralProvider.ResolveThinkingOptions(model, explicitOptions.Reasoning).PromptMode;
        }

        return configured.PromptMode ??
               MistralProvider.ResolveThinkingOptions(model, resolvedOptions.Reasoning).PromptMode;
    }

    private static string? ResolveMistralReasoningEffort(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        if (explicitOptions.Reasoning is not null)
        {
            return MistralProvider.ResolveThinkingOptions(model, explicitOptions.Reasoning).ReasoningEffort;
        }

        return configured.ReasoningEffort ??
               MistralProvider.ResolveThinkingOptions(model, resolvedOptions.Reasoning).ReasoningEffort;
    }

    private static string? ResolveOpenAiReasoningEffort(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        if (explicitOptions.Reasoning is { } explicitReasoning)
        {
            return MapOpenAiReasoningEffort(model, explicitReasoning);
        }

        return configured.ReasoningEffort ??
               (resolvedOptions.Reasoning is { } resolvedReasoning
                   ? MapOpenAiReasoningEffort(model, resolvedReasoning)
                   : null);
    }

    /// <summary>【AI】【配置思考】复用直接入口的等级收敛和关闭规则，避免 off 变为 high。</summary>
    /// <param name="model">模型能力。</param><param name="reasoning">请求等级。</param><returns>可发送等级或关闭时空值。</returns>
    private static string? MapOpenAiReasoningEffort(Model model, ThinkingLevel reasoning) =>
        OpenAiThinkingTemplates.RequestedEffort(model, new SimpleStreamOptions { Reasoning = reasoning });

    private static StreamOptions ApplyProviderSpecificOptions(
        Model model,
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration? configured)
    {
        if (configured is null)
        {
            return options;
        }

        return model.Api switch
        {
            "openai-chat-completions" => ApplyOpenAiOptions(options, configured),
            "openai-responses" => ApplyOpenAiResponsesOptions(options, configured),
            "openai-codex-responses" => ApplyOpenAiCodexResponsesOptions(options, configured),
            "azure-openai-responses" => ApplyAzureOpenAiResponsesOptions(options, configured),
            "anthropic-messages" => ApplyAnthropicOptions(model, options, configured),
            "mistral-conversations" => ApplyMistralOptions(options, configured),
            "google-generative-language" => ApplyGoogleOptions(model, options, configured),
            "google-vertex" => ApplyGoogleVertexOptions(model, options, configured),
            "google-gemini-cli" => ApplyGoogleGeminiCliOptions(model, options, configured),
            "bedrock-converse-stream" => ApplyBedrockOptions(model, options, configured),
            _ => options
        };
    }

    private static OpenAiOptions ApplyOpenAiOptions(
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as OpenAiOptions;
        return new OpenAiOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ToolChoice = typed?.ToolChoice ?? ToOpenAiToolChoice(configured.ToolChoice),
            ThinkingBudgets = typed?.ThinkingBudgets ?? (options as SimpleStreamOptions)?.ThinkingBudgets,
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort
        };
    }

    /// <summary>【AI】【Responses 配置】向普通流式入口应用配置，保留调用方专用选项。</summary>
    /// <param name="options">已解析的请求选项。</param>
    /// <param name="configured">供应商配置。</param>
    /// <returns>完成合并的 Responses 选项。</returns>
    private static OpenAiResponsesOptions ApplyOpenAiResponsesOptions(
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as OpenAiResponsesOptions;
        return new OpenAiResponsesOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort,
            ReasoningSummary = typed?.ReasoningSummary ?? configured.ReasoningSummary,
            ServiceTier = typed?.ServiceTier ?? configured.ServiceTier,
            ToolChoice = typed?.ToolChoice ?? ToResponsesToolChoice(configured.ToolChoice)
        };
    }

    private static OpenAiCodexResponsesOptions ApplyOpenAiCodexResponsesOptions(
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as OpenAiCodexResponsesOptions;
        return new OpenAiCodexResponsesOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort,
            ReasoningSummary = typed?.ReasoningSummary ?? configured.ReasoningSummary,
            ServiceTier = typed?.ServiceTier ?? configured.ServiceTier,
            TextVerbosity = typed?.TextVerbosity ?? configured.TextVerbosity
        };
    }

    /// <summary>【AI】【Azure Responses 配置】合并普通流式入口配置，显式工具选择和部署参数优先。</summary>
    /// <param name="options">已解析的请求选项。</param>
    /// <param name="configured">供应商配置。</param>
    /// <returns>完成合并的 Azure 选项。</returns>
    private static AzureOpenAiResponsesOptions ApplyAzureOpenAiResponsesOptions(
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as AzureOpenAiResponsesOptions;
        return new AzureOpenAiResponsesOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort,
            ReasoningSummary = typed?.ReasoningSummary ?? configured.ReasoningSummary,
            AzureApiVersion = typed?.AzureApiVersion ?? configured.AzureApiVersion,
            AzureResourceName = typed?.AzureResourceName ?? configured.AzureResourceName,
            AzureBaseUrl = typed?.AzureBaseUrl ?? configured.AzureBaseUrl,
            AzureDeploymentName = typed?.AzureDeploymentName ?? configured.AzureDeploymentName,
            ToolChoice = typed?.ToolChoice ?? ToResponsesToolChoice(configured.ToolChoice)
        };
    }

    /// <summary>【AI】【Anthropic 选项】合并简化入口配置，并应用与直接入口相同的 token 预算。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">折叠前的规范化上下文。</param>
    /// <param name="resolvedOptions">合并后的通用选项。</param>
    /// <param name="explicitOptions">调用方显式选项。</param>
    /// <param name="configured">供应商专用配置。</param>
    /// <returns>可直接交给高级入口的完整选项。</returns>
    private static AnthropicOptions CreateAnthropicOptions(
        Model model,
        LlmContext context,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = new AnthropicOptions
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            ThinkingEnabled = ResolveAnthropicThinkingEnabled(resolvedOptions, explicitOptions, configured),
            ThinkingBudgetTokens = ResolveAnthropicThinkingBudget(model, resolvedOptions, explicitOptions, configured),
            Effort = ResolveAnthropicEffort(model, resolvedOptions, explicitOptions, configured),
            ThinkingDisplay = configured.ThinkingDisplay,
            InterleavedThinking = configured.InterleavedThinking,
            ToolChoice = ToAnthropicToolChoice(explicitOptions.ToolChoice) ?? ToAnthropicToolChoice(configured.ToolChoice)
        };

        var budget = SimpleTokenOptions.ResolveAnthropic(model, context, resolvedOptions.MaxTokens,
            typed.ThinkingEnabled == true && !UsesAnthropicAdaptiveThinking(model) ? typed.ThinkingBudgetTokens ?? 1_024 : null);
        return typed with { MaxTokens = budget.MaxTokens, ThinkingBudgetTokens = budget.ThinkingBudget ?? typed.ThinkingBudgetTokens };
    }

    private static AnthropicOptions ApplyAnthropicOptions(
        Model model,
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as AnthropicOptions;
        return new AnthropicOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ThinkingEnabled = typed?.ThinkingEnabled ?? configured.ThinkingEnabled,
            ThinkingBudgetTokens = typed?.ThinkingBudgetTokens ?? configured.ThinkingBudgetTokens,
            Effort = typed?.Effort ?? configured.Effort,
            ThinkingDisplay = typed?.ThinkingDisplay ?? configured.ThinkingDisplay,
            InterleavedThinking = typed?.InterleavedThinking ?? configured.InterleavedThinking,
            ToolChoice = typed?.ToolChoice ?? ToAnthropicToolChoice(configured.ToolChoice)
        };
    }

    /// <summary>【AI】【Anthropic 选项】合并简化入口思考开关，显式 Off 优先于配置文件。</summary>
    /// <param name="resolvedOptions">完成配置合并的选项。</param>
    /// <param name="explicitOptions">调用方显式选项。</param>
    /// <param name="configured">供应商专用配置。</param>
    /// <returns>是否启用思考。</returns>
    private static bool? ResolveAnthropicThinkingEnabled(
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        explicitOptions.Reasoning is not null
            ? explicitOptions.Reasoning != ThinkingLevel.Off
            : configured.ThinkingEnabled ?? (resolvedOptions.Reasoning is not null and not ThinkingLevel.Off);

    /// <summary>从显式等级、自定义预算和供应商默认值选择固定思考预算。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="resolvedOptions">合并后的通用选项。</param>
    /// <param name="explicitOptions">调用方显式选项。</param>
    /// <param name="configured">供应商专用配置。</param>
    /// <returns>固定思考预算，未指定时为空。</returns>
    private static int? ResolveAnthropicThinkingBudget(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        if (explicitOptions.Reasoning is { } explicitReasoning && !UsesAnthropicAdaptiveThinking(model))
        {
            return StreamOptionHelpers.GetThinkingBudget(
                resolvedOptions.ThinkingBudgets,
                explicitReasoning,
                defaultMinimal: 1_024,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 16_384);
        }

        if (configured.ThinkingBudgetTokens is { } configuredBudget)
        {
            return configuredBudget;
        }

        var reasoning = explicitOptions.Reasoning ?? resolvedOptions.Reasoning;
        if (reasoning is null || UsesAnthropicAdaptiveThinking(model))
        {
            return null;
        }

        return StreamOptionHelpers.GetThinkingBudget(
            resolvedOptions.ThinkingBudgets,
            reasoning.Value,
            defaultMinimal: 1_024,
            defaultLow: 2_048,
            defaultMedium: 8_192,
            defaultHigh: 16_384);
    }

    private static string? ResolveAnthropicEffort(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        if (!UsesAnthropicAdaptiveThinking(model))
        {
            return configured.Effort;
        }

        var reasoning = explicitOptions.Reasoning ?? resolvedOptions.Reasoning;
        return reasoning is null
            ? configured.Effort
            : MapAnthropicEffort(reasoning.Value, model);
    }

    private static MistralOptions ApplyMistralOptions(
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as MistralOptions;
        return new MistralOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ToolChoice = typed?.ToolChoice ?? ToMistralToolChoice(configured.ToolChoice),
            PromptMode = typed?.PromptMode ?? configured.PromptMode,
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort
        };
    }

    private static GoogleOptions CreateGoogleOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        new()
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            ToolChoice = explicitOptions.ToolChoice as string ?? configured.ToolChoice?.Kind,
            Thinking = CreateGoogleThinking(model, resolvedOptions, explicitOptions, configured)
        };

    private static GoogleVertexOptions CreateGoogleVertexOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        new()
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            Project = configured.Project,
            Location = configured.Location,
            ToolChoice = explicitOptions.ToolChoice as string ?? configured.ToolChoice?.Kind,
            Thinking = CreateGoogleThinking(model, resolvedOptions, explicitOptions, configured)
        };

    private static GoogleGeminiCliOptions CreateGoogleGeminiCliOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        new()
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            ProjectId = configured.ProjectId,
            ToolChoice = explicitOptions.ToolChoice as string ?? configured.ToolChoice?.Kind,
            Thinking = CreateGoogleThinking(model, resolvedOptions, explicitOptions, configured)
        };

    private static GoogleOptions ApplyGoogleOptions(
        Model model,
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as GoogleOptions;
        return new GoogleOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ToolChoice = typed?.ToolChoice ?? configured.ToolChoice?.Kind,
            Thinking = typed?.Thinking ?? CreateGoogleThinking(model, null, null, configured)
        };
    }

    private static GoogleVertexOptions ApplyGoogleVertexOptions(
        Model model,
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as GoogleVertexOptions;
        return new GoogleVertexOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            AccessToken = typed?.AccessToken,
            CredentialsFile = typed?.CredentialsFile,
            Project = typed?.Project ?? configured.Project,
            Location = typed?.Location ?? configured.Location,
            ToolChoice = typed?.ToolChoice ?? configured.ToolChoice?.Kind,
            Thinking = typed?.Thinking ?? CreateGoogleThinking(model, null, null, configured)
        };
    }

    private static GoogleGeminiCliOptions ApplyGoogleGeminiCliOptions(
        Model model,
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as GoogleGeminiCliOptions;
        return new GoogleGeminiCliOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            ProjectId = typed?.ProjectId ?? configured.ProjectId,
            ToolChoice = typed?.ToolChoice ?? configured.ToolChoice?.Kind,
            Thinking = typed?.Thinking ?? CreateGoogleThinking(model, null, null, configured)
        };
    }

    private static BedrockOptions CreateBedrockOptions(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var (toolChoice, toolName) = ToBedrockToolChoice(explicitOptions.ToolChoice) ?? ToBedrockToolChoice(configured.ToolChoice);
        return new BedrockOptions
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
            TransformHeaders = resolvedOptions.TransformHeaders,
            OnProviderStreamEvent = resolvedOptions.OnProviderStreamEvent,
            Transport = resolvedOptions.Transport,
            CacheRetention = resolvedOptions.CacheRetention,
            SessionId = resolvedOptions.SessionId,
            Headers = resolvedOptions.Headers,
            Timeout = resolvedOptions.Timeout,
            MaxRetryDelay = resolvedOptions.MaxRetryDelay,
            MaxRetries = resolvedOptions.MaxRetries,
            WebSocketConnectTimeout = resolvedOptions.WebSocketConnectTimeout,
            Metadata = resolvedOptions.Metadata,
            Env = resolvedOptions.Env,
            SamplingParams = resolvedOptions.SamplingParams,
            Deferred = resolvedOptions.Deferred,
            Region = configured.Region,
            Profile = configured.Profile,
            BearerToken = configured.BearerToken,
            ToolChoice = toolChoice,
            ToolName = toolName,
            Reasoning = model.Reasoning ? resolvedOptions.Reasoning : null,
            ThinkingBudgetTokens = explicitOptions.Reasoning is null ? configured.ThinkingBudgetTokens : null,
            ThinkingBudgets = resolvedOptions.ThinkingBudgets,
            ThinkingDisplay = configured.ThinkingDisplay,
            InterleavedThinking = configured.InterleavedThinking,
            RequestMetadata = configured.RequestMetadata
        };
    }

    private static BedrockOptions ApplyBedrockOptions(
        Model model,
        StreamOptions options,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        var typed = options as BedrockOptions;
        var (toolChoice, toolName) = ToBedrockToolChoice(configured.ToolChoice);
        return new BedrockOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            TopP = options.TopP,
            ApiKey = options.ApiKey,
            Signal = options.Signal,
            OnResponse = options.OnResponse,
            OnPayload = options.OnPayload,
            TransformHeaders = options.TransformHeaders,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
            Transport = options.Transport,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Timeout = options.Timeout,
            MaxRetryDelay = options.MaxRetryDelay,
            MaxRetries = options.MaxRetries,
            WebSocketConnectTimeout = options.WebSocketConnectTimeout,
            Metadata = options.Metadata,
            Env = options.Env,
            SamplingParams = options.SamplingParams,
            Deferred = options.Deferred,
            Region = typed?.Region ?? configured.Region,
            BearerToken = typed?.BearerToken ?? configured.BearerToken,
            AccessKeyId = typed?.AccessKeyId,
            SecretAccessKey = typed?.SecretAccessKey,
            SessionToken = typed?.SessionToken,
            Profile = typed?.Profile ?? configured.Profile,
            CredentialsFile = typed?.CredentialsFile,
            ConfigFile = typed?.ConfigFile,
            CredentialProcess = typed?.CredentialProcess,
            WebIdentityTokenFile = typed?.WebIdentityTokenFile,
            WebIdentityRoleArn = typed?.WebIdentityRoleArn,
            WebIdentityRoleSessionName = typed?.WebIdentityRoleSessionName,
            StsEndpoint = typed?.StsEndpoint,
            SsoTokenCacheFile = typed?.SsoTokenCacheFile,
            SsoTokenCacheDirectory = typed?.SsoTokenCacheDirectory,
            SsoPortalEndpoint = typed?.SsoPortalEndpoint,
            SsoOidcEndpoint = typed?.SsoOidcEndpoint,
            ContainerCredentialsRelativeUri = typed?.ContainerCredentialsRelativeUri,
            ContainerCredentialsFullUri = typed?.ContainerCredentialsFullUri,
            ContainerAuthorizationToken = typed?.ContainerAuthorizationToken,
            ContainerAuthorizationTokenFile = typed?.ContainerAuthorizationTokenFile,
            Ec2MetadataDisabled = typed?.Ec2MetadataDisabled,
            Ec2MetadataV1Disabled = typed?.Ec2MetadataV1Disabled,
            Ec2MetadataServiceEndpoint = typed?.Ec2MetadataServiceEndpoint,
            Ec2MetadataServiceTimeout = typed?.Ec2MetadataServiceTimeout,
            ToolChoice = typed?.ToolChoice ?? toolChoice,
            ToolName = typed?.ToolName ?? toolName,
            Reasoning = typed?.Reasoning ?? (model.Reasoning ? options is SimpleStreamOptions simple ? simple.Reasoning : null : null),
            ThinkingBudgetTokens = typed?.ThinkingBudgetTokens ?? configured.ThinkingBudgetTokens,
            ThinkingBudgets = typed?.ThinkingBudgets ?? (options as SimpleStreamOptions)?.ThinkingBudgets,
            ThinkingDisplay = typed?.ThinkingDisplay ?? configured.ThinkingDisplay,
            InterleavedThinking = typed?.InterleavedThinking ?? configured.InterleavedThinking,
            RequestMetadata = typed?.RequestMetadata ?? configured.RequestMetadata
        };
    }

    private static GoogleThinkingOptions? CreateGoogleThinking(
        Model model,
        SimpleStreamOptions? resolvedOptions,
        SimpleStreamOptions? explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        if (explicitOptions?.Reasoning is { } explicitReasoning)
        {
            return GoogleThinking.ResolveSimple(model, explicitReasoning, resolvedOptions?.ThinkingBudgets);
        }

        if (configured.ThinkingEnabled.HasValue ||
            configured.ThinkingBudgetTokens is not null ||
            !string.IsNullOrWhiteSpace(configured.ThinkingLevel))
        {
            return new GoogleThinkingOptions
            {
                Enabled = configured.ThinkingEnabled ?? true,
                BudgetTokens = configured.ThinkingBudgetTokens,
                Level = configured.ThinkingLevel
            };
        }

        if (resolvedOptions?.Reasoning is { } resolvedReasoning)
        {
            return GoogleThinking.ResolveSimple(model, resolvedReasoning, resolvedOptions.ThinkingBudgets);
        }

        return resolvedOptions is null ? null : GoogleThinking.ResolveSimple(model, null, resolvedOptions.ThinkingBudgets);
    }

    /// <summary>把 provider 无关工具选择值转换为 Mistral 形式。</summary>
    /// <param name="choice">字符串、已有 provider 对象或 JSON 值。</param>
    /// <returns>转换后的选择策略；无法转换时返回 null。</returns>
    private static MistralToolChoice? ToMistralToolChoice(object? choice) => MistralProvider.ConvertToolChoice(choice);

    /// <summary>把 provider 无关工具选择值转换为 OpenAI 形式。</summary>
    /// <param name="choice">字符串、已有 provider 对象或 JSON 值。</param>
    /// <returns>转换后的选择策略；无法转换时返回 null。</returns>
    private static OpenAiToolChoice? ToOpenAiToolChoice(object? choice) => choice switch
    {
        null => null,
        OpenAiToolChoice typed => typed,
        string value when !string.IsNullOrWhiteSpace(value) => OpenAiToolChoice.FromString(value),
        JsonElement element when element.ValueKind == JsonValueKind.String => OpenAiToolChoice.FromString(element.GetString()!),
        _ => null
    };

    /// <summary>把 provider 无关工具选择值转换为 Anthropic 形式。</summary>
    /// <param name="choice">字符串、已有 provider 对象或 JSON 值。</param>
    /// <returns>转换后的选择策略；无法转换时返回 null。</returns>
    private static AnthropicToolChoice? ToAnthropicToolChoice(object? choice) => choice switch
    {
        null => null,
        AnthropicToolChoice typed => typed,
        string value when !string.IsNullOrWhiteSpace(value) => AnthropicToolChoice.FromString(value),
        JsonElement element when element.ValueKind == JsonValueKind.String => AnthropicToolChoice.FromString(element.GetString()!),
        _ => null
    };

    /// <summary>把 provider 无关工具选择值转换为 Bedrock 形式。</summary>
    /// <param name="choice">字符串或 JSON 字符串。</param>
    /// <returns>Bedrock 的选择类型和工具名称。</returns>
    private static (string? ToolChoice, string? ToolName)? ToBedrockToolChoice(object? choice) => choice switch
    {
        string value when !string.IsNullOrWhiteSpace(value) => (value, null),
        JsonElement element when element.ValueKind == JsonValueKind.String => (element.GetString(), null),
        _ => null
    };

    private static MistralToolChoice? ToMistralToolChoice(ModelToolChoiceConfiguration? configured)
    {
        if (configured is null)
        {
            return null;
        }

        return configured.IsFunction
            ? MistralToolChoice.Function(configured.FunctionName!)
            : MistralToolChoice.FromString(configured.Kind);
    }

    /// <summary>【AI】【Responses 配置】把模型配置中的工具选择转换成 Responses 的扁平对象。</summary>
    /// <param name="configured">已解析的工具选择；兼容旧式嵌套 function 配置。</param>
    /// <returns>选择模式字符串、命名函数对象或 null。</returns>
    private static object? ToResponsesToolChoice(ModelToolChoiceConfiguration? configured)
    {
        if (configured is null || configured.IsTool) return null;
        return configured.IsFunction
            ? new Dictionary<string, object> { ["type"] = "function", ["name"] = configured.FunctionName! }
            : configured.Kind;
    }

    private static OpenAiToolChoice? ToOpenAiToolChoice(ModelToolChoiceConfiguration? configured)
    {
        if (configured is null || configured.IsTool)
        {
            return null;
        }

        return configured.IsFunction
            ? OpenAiToolChoice.Function(configured.FunctionName!)
            : OpenAiToolChoice.FromString(configured.Kind);
    }

    private static AnthropicToolChoice? ToAnthropicToolChoice(ModelToolChoiceConfiguration? configured)
    {
        if (configured is null)
        {
            return null;
        }

        return configured.IsTool
            ? AnthropicToolChoice.Tool(configured.ToolName!)
            : AnthropicToolChoice.FromString(configured.Kind);
    }

    private static (string? ToolChoice, string? ToolName) ToBedrockToolChoice(ModelToolChoiceConfiguration? configured)
    {
        if (configured is null)
        {
            return (null, null);
        }

        if (configured.IsTool)
        {
            return ("tool", configured.ToolName);
        }

        return configured.IsFunction ? (null, null) : (configured.Kind, null);
    }

    /// <summary>按显式能力判断自适应思考，与直接 Provider 入口保持一致。</summary>
    /// <param name="model">目标模型。</param>
    /// <returns>启用自适应思考时为 true。</returns>
    private static bool UsesAnthropicAdaptiveThinking(Model model) => model.Compat?.ForceAdaptiveThinking == true;

    /// <summary>复用 Anthropic 等级映射，避免配置入口与直接入口产生不同 effort。</summary>
    /// <param name="level">通用思考等级。</param>
    /// <param name="model">目标模型。</param>
    /// <returns>供应商原生 effort。</returns>
    private static string MapAnthropicEffort(ThinkingLevel level, Model model) => AnthropicProtocol.MapEffort(model, level);

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

    /// <summary>
    /// 合并模型配置与请求级采样参数，请求级键覆盖配置级键。
    /// </summary>
    /// <param name="configured">配置文件中的默认采样参数。</param>
    /// <param name="explicitValues">当前请求显式采样参数。</param>
    /// <returns>合并后的只读字典；两者均为空时返回 null。</returns>
    private static IReadOnlyDictionary<string, object>? MergeSamplingParams(
        IDictionary<string, object>? configured,
        IReadOnlyDictionary<string, object>? explicitValues)
    {
        if (configured is null && explicitValues is null)
        {
            return null;
        }

        var merged = new Dictionary<string, object>(StringComparer.Ordinal);
        if (configured is not null)
        {
            foreach (var pair in configured)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (explicitValues is not null)
        {
            foreach (var pair in explicitValues)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
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

    private static ThinkingBudgets? MergeThinkingBudgets(
        ThinkingBudgets? configuredBudgets,
        ThinkingBudgets? explicitBudgets)
    {
        if (configuredBudgets is null)
        {
            return explicitBudgets;
        }

        if (explicitBudgets is null)
        {
            return configuredBudgets;
        }

        return new ThinkingBudgets
        {
            Minimal = explicitBudgets.Minimal ?? configuredBudgets.Minimal,
            Low = explicitBudgets.Low ?? configuredBudgets.Low,
            Medium = explicitBudgets.Medium ?? configuredBudgets.Medium,
            High = explicitBudgets.High ?? configuredBudgets.High
        };
    }
}
