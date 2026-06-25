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
            var authResolvedModel = (authResolver ?? AuthResolver).ResolveModel(model);
            resolvedOptions = ResolveOptions(
                authResolvedModel,
                options,
                configurationStore,
                authResolver,
                applyProviderSpecificOptions: true,
                out _,
                out resolvedModel);
        }
        catch (ProviderAuthException ex)
        {
            return CreateAuthErrorStream(model, ex);
        }

        var provider = registry.Get(resolvedModel.Api);
        return provider.Stream(resolvedModel, context, resolvedOptions);
    }

    public static AssistantMessageStream StreamSimple(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        SimpleStreamOptions options,
        ModelConfigurationStore? configurationStore = null,
        ProviderAuthResolver? authResolver = null)
    {
        Model resolvedModel;
        SimpleStreamOptions resolvedOptions;
        ModelProviderSpecificOptionsConfiguration? providerSpecific;
        try
        {
            var authResolvedModel = (authResolver ?? AuthResolver).ResolveModel(model);
            resolvedOptions = (SimpleStreamOptions)ResolveOptions(
                authResolvedModel,
                options,
                configurationStore,
                authResolver,
                applyProviderSpecificOptions: false,
                out providerSpecific,
                out resolvedModel);
        }
        catch (ProviderAuthException ex)
        {
            return CreateAuthErrorStream(model, ex);
        }

        var provider = registry.Get(resolvedModel.Api);
        if (providerSpecific is not null &&
            TryCreateProviderSpecificSimpleOptions(resolvedModel, resolvedOptions, options, providerSpecific, out var providerOptions))
        {
            return provider.Stream(resolvedModel, context, providerOptions);
        }

        return provider.StreamSimple(resolvedModel, context, resolvedOptions);
    }

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

    private static StreamOptions ResolveOptions(
        Model model,
        StreamOptions options,
        ModelConfigurationStore? configurationStore,
        ProviderAuthResolver? authResolver,
        bool applyProviderSpecificOptions,
        out ModelProviderSpecificOptionsConfiguration? providerSpecific,
        out Model requestModel)
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
        var apiKey = resolver.ResolveApiKey(model.Provider, options.ApiKey, env) ?? requestConfig.ApiKey;
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
            Env = env
        };

        if (!options.HasExplicitTransport && requestConfig.Options.Transport is { } transport)
        {
            resolved = resolved with { Transport = transport };
        }

        if (!options.HasExplicitCacheRetention && requestConfig.Options.CacheRetention is { } cacheRetention)
        {
            resolved = resolved with { CacheRetention = cacheRetention };
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
            model,
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

    private static bool TryCreateProviderSpecificSimpleOptions(
        Model model,
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
            "anthropic-messages" => CreateAnthropicOptions(model, resolvedOptions, explicitOptions, configured),
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
            ToolChoice = ToOpenAiToolChoice(configured.ToolChoice),
            ReasoningEffort = ResolveOpenAiReasoningEffort(model, resolvedOptions, explicitOptions, configured)
        };

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
            ReasoningEffort = ResolveReasoningEffort(model, resolvedOptions, explicitOptions, configured),
            ReasoningSummary = configured.ReasoningSummary,
            ServiceTier = configured.ServiceTier
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
            ReasoningEffort = ResolveReasoningEffort(model, resolvedOptions, explicitOptions, configured),
            ReasoningSummary = configured.ReasoningSummary,
            ServiceTier = configured.ServiceTier,
            TextVerbosity = configured.TextVerbosity
        };

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
            ReasoningEffort = ResolveReasoningEffort(model, resolvedOptions, explicitOptions, configured),
            ReasoningSummary = configured.ReasoningSummary,
            AzureApiVersion = configured.AzureApiVersion,
            AzureResourceName = configured.AzureResourceName,
            AzureBaseUrl = configured.AzureBaseUrl,
            AzureDeploymentName = configured.AzureDeploymentName
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
            ToolChoice = ToMistralToolChoice(configured.ToolChoice),
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
            return UsesMistralPromptModeReasoning(model) ? "reasoning" : null;
        }

        return configured.PromptMode ??
               (resolvedOptions.Reasoning is not null && UsesMistralPromptModeReasoning(model) ? "reasoning" : null);
    }

    private static string? ResolveMistralReasoningEffort(
        Model model,
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured)
    {
        if (explicitOptions.Reasoning is not null)
        {
            return model.Reasoning && UsesMistralReasoningEffort(model) ? "high" : null;
        }

        return configured.ReasoningEffort ??
               (resolvedOptions.Reasoning is not null && model.Reasoning && UsesMistralReasoningEffort(model) ? "high" : null);
    }

    private static bool UsesMistralPromptModeReasoning(Model model) =>
        model.Reasoning && !UsesMistralReasoningEffort(model);

    private static bool UsesMistralReasoningEffort(Model model) =>
        model.Id.Equals("mistral-small-2603", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Equals("mistral-small-latest", StringComparison.OrdinalIgnoreCase);

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

    private static string MapOpenAiReasoningEffort(Model model, ThinkingLevel reasoning)
    {
        if (reasoning == ThinkingLevel.ExtraHigh && !ModelCatalog.SupportsXhigh(model))
        {
            reasoning = ThinkingLevel.High;
        }

        return reasoning switch
        {
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.ExtraHigh => "xhigh",
            _ => "high"
        };
    }

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
            ToolChoice = typed?.ToolChoice ?? ToOpenAiToolChoice(configured.ToolChoice),
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort
        };
    }

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
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort,
            ReasoningSummary = typed?.ReasoningSummary ?? configured.ReasoningSummary,
            ServiceTier = typed?.ServiceTier ?? configured.ServiceTier
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
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort,
            ReasoningSummary = typed?.ReasoningSummary ?? configured.ReasoningSummary,
            ServiceTier = typed?.ServiceTier ?? configured.ServiceTier,
            TextVerbosity = typed?.TextVerbosity ?? configured.TextVerbosity
        };
    }

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
            ReasoningEffort = typed?.ReasoningEffort ?? configured.ReasoningEffort,
            ReasoningSummary = typed?.ReasoningSummary ?? configured.ReasoningSummary,
            AzureApiVersion = typed?.AzureApiVersion ?? configured.AzureApiVersion,
            AzureResourceName = typed?.AzureResourceName ?? configured.AzureResourceName,
            AzureBaseUrl = typed?.AzureBaseUrl ?? configured.AzureBaseUrl,
            AzureDeploymentName = typed?.AzureDeploymentName ?? configured.AzureDeploymentName
        };
    }

    private static AnthropicOptions CreateAnthropicOptions(
        Model model,
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
            ThinkingEnabled = ResolveAnthropicThinkingEnabled(resolvedOptions, explicitOptions, configured),
            ThinkingBudgetTokens = ResolveAnthropicThinkingBudget(model, resolvedOptions, explicitOptions, configured),
            Effort = ResolveAnthropicEffort(model, resolvedOptions, explicitOptions, configured),
            ThinkingDisplay = configured.ThinkingDisplay,
            InterleavedThinking = configured.InterleavedThinking,
            ToolChoice = ToAnthropicToolChoice(configured.ToolChoice)
        };

        return typed;
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
            ThinkingEnabled = typed?.ThinkingEnabled ?? configured.ThinkingEnabled,
            ThinkingBudgetTokens = typed?.ThinkingBudgetTokens ?? configured.ThinkingBudgetTokens,
            Effort = typed?.Effort ?? configured.Effort,
            ThinkingDisplay = typed?.ThinkingDisplay ?? configured.ThinkingDisplay,
            InterleavedThinking = typed?.InterleavedThinking ?? configured.InterleavedThinking,
            ToolChoice = typed?.ToolChoice ?? ToAnthropicToolChoice(configured.ToolChoice)
        };
    }

    private static bool? ResolveAnthropicThinkingEnabled(
        SimpleStreamOptions resolvedOptions,
        SimpleStreamOptions explicitOptions,
        ModelProviderSpecificOptionsConfiguration configured) =>
        explicitOptions.Reasoning is not null
            ? true
            : configured.ThinkingEnabled ?? (resolvedOptions.Reasoning is not null ? true : null);

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
            ToolChoice = configured.ToolChoice?.Kind,
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
            Project = configured.Project,
            Location = configured.Location,
            ToolChoice = configured.ToolChoice?.Kind,
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
            ProjectId = configured.ProjectId,
            ToolChoice = configured.ToolChoice?.Kind,
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
        var (toolChoice, toolName) = ToBedrockToolChoice(configured.ToolChoice);
        return new BedrockOptions
        {
            Temperature = resolvedOptions.Temperature,
            MaxTokens = resolvedOptions.MaxTokens ?? model.MaxOutputTokens,
            TopP = resolvedOptions.TopP,
            ApiKey = resolvedOptions.ApiKey,
            Signal = resolvedOptions.Signal,
            OnResponse = resolvedOptions.OnResponse,
            OnPayload = resolvedOptions.OnPayload,
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
            return CreateGoogleReasoningThinking(model, explicitReasoning, resolvedOptions?.ThinkingBudgets);
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
            return CreateGoogleReasoningThinking(model, resolvedReasoning, resolvedOptions.ThinkingBudgets);
        }

        return null;
    }

    private static GoogleThinkingOptions CreateGoogleReasoningThinking(
        Model model,
        ThinkingLevel reasoning,
        ThinkingBudgets? budgets)
    {
        if (GoogleProvider.IsGemini3ProModel(model.Id) ||
            GoogleProvider.IsGemini3FlashModel(model.Id) ||
            GoogleProvider.IsGemma4Model(model.Id))
        {
            return new GoogleThinkingOptions
            {
                Enabled = true,
                Level = GetGoogleThinkingLevel(model, reasoning)
            };
        }

        return new GoogleThinkingOptions
        {
            Enabled = true,
            BudgetTokens = GetGoogleThinkingBudget(model, reasoning, budgets)
        };
    }

    private static string GetGoogleThinkingLevel(Model model, ThinkingLevel reasoning)
    {
        if (GoogleProvider.IsGemini3ProModel(model.Id))
        {
            return reasoning is ThinkingLevel.Minimal or ThinkingLevel.Low ? "LOW" : "HIGH";
        }

        if (reasoning == ThinkingLevel.ExtraHigh)
        {
            reasoning = ThinkingLevel.High;
        }

        return reasoning switch
        {
            ThinkingLevel.Minimal => "MINIMAL",
            ThinkingLevel.Low => "LOW",
            ThinkingLevel.Medium => "MEDIUM",
            _ => "HIGH"
        };
    }

    private static int GetGoogleThinkingBudget(Model model, ThinkingLevel reasoning, ThinkingBudgets? budgets)
    {
        if (model.Api.Equals("google-gemini-cli", StringComparison.OrdinalIgnoreCase))
        {
            return StreamOptionHelpers.GetThinkingBudget(
                budgets,
                reasoning,
                defaultMinimal: 1_024,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 16_384);
        }

        if (model.Id.Contains("2.5-pro", StringComparison.OrdinalIgnoreCase))
        {
            return StreamOptionHelpers.GetThinkingBudget(
                budgets,
                reasoning,
                defaultMinimal: 128,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 32_768);
        }

        if (model.Id.Contains("2.5-flash-lite", StringComparison.OrdinalIgnoreCase))
        {
            return StreamOptionHelpers.GetThinkingBudget(
                budgets,
                reasoning,
                defaultMinimal: 512,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 24_576);
        }

        if (model.Id.Contains("2.5-flash", StringComparison.OrdinalIgnoreCase))
        {
            return StreamOptionHelpers.GetThinkingBudget(
                budgets,
                reasoning,
                defaultMinimal: 128,
                defaultLow: 2_048,
                defaultMedium: 8_192,
                defaultHigh: 24_576);
        }

        return StreamOptionHelpers.GetCustomThinkingBudget(budgets, reasoning) ?? -1;
    }

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

    private static bool UsesAnthropicAdaptiveThinking(Model model) =>
        model.Id.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Contains("opus-4-7", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Contains("opus-4.7", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Contains("sonnet-4.6", StringComparison.OrdinalIgnoreCase);

    private static string MapAnthropicEffort(ThinkingLevel level, Model model) =>
        level switch
        {
            ThinkingLevel.Minimal => "low",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.ExtraHigh when model.Id.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase) ||
                                        model.Id.Contains("opus-4.6", StringComparison.OrdinalIgnoreCase) => "max",
            ThinkingLevel.ExtraHigh when model.Id.Contains("opus-4-7", StringComparison.OrdinalIgnoreCase) ||
                                        model.Id.Contains("opus-4.7", StringComparison.OrdinalIgnoreCase) => "xhigh",
            ThinkingLevel.ExtraHigh => "high",
            _ => "high"
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
