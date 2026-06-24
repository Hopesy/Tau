using System.Diagnostics;
using System.Text.Json;

namespace Tau.Ai.Registry;

public sealed class ModelConfigurationStore
{
    private readonly string[] _searchPaths;

    public ModelConfigurationStore(IEnumerable<string>? searchPaths = null)
    {
        _searchPaths = searchPaths?.ToArray() ?? GetDefaultSearchPaths().ToArray();
    }

    internal void ApplyTo(Dictionary<string, Dictionary<string, Model>> models)
    {
        var doc = LoadDocument();
        if (doc is null)
        {
            return;
        }

        using (doc)
        {
            if (!TryGetProviders(doc.RootElement, out var providers))
            {
                return;
            }

            foreach (var providerProp in providers.EnumerateObject())
            {
                if (providerProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                ApplyProvider(providerProp.Name, providerProp.Value, models);
            }
        }
    }

    internal ModelRequestConfiguration ResolveRequestConfiguration(Model model)
    {
        var doc = LoadDocument();
        if (doc is null)
        {
            return ModelRequestConfiguration.Empty;
        }

        using (doc)
        {
            if (!TryGetProviders(doc.RootElement, out var providers) ||
                !TryGetObjectProperty(providers, model.Provider, out var providerConfig))
            {
                return ModelRequestConfiguration.Empty;
            }

            var apiKey = ResolveConfigValue(GetString(providerConfig, "apiKey"));
            var authHeader = GetBool(providerConfig, "authHeader") ?? false;
            var headers = ResolveHeaders(ParseStringDictionary(providerConfig, "headers"));
            var options = ParseRequestOptions(providerConfig);

            if (providerConfig.TryGetProperty("modelOverrides", out var overrides) &&
                overrides.ValueKind == JsonValueKind.Object &&
                TryGetObjectProperty(overrides, model.Id, out var overrideConfig))
            {
                headers = MergeHeaders(headers, ResolveHeaders(ParseStringDictionary(overrideConfig, "headers")));
                options = MergeRequestOptions(options, ParseRequestOptions(overrideConfig));
            }

            if (providerConfig.TryGetProperty("models", out var configuredModels) &&
                configuredModels.ValueKind == JsonValueKind.Array)
            {
                foreach (var configuredModel in configuredModels.EnumerateArray())
                {
                    if (configuredModel.ValueKind == JsonValueKind.Object &&
                        TryGetString(configuredModel, "id", out var modelId) &&
                        model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        headers = MergeHeaders(headers, ResolveHeaders(ParseStringDictionary(configuredModel, "headers")));
                        options = MergeRequestOptions(options, ParseRequestOptions(configuredModel));
                        break;
                    }
                }
            }

            return new ModelRequestConfiguration(apiKey, headers, authHeader, options);
        }
    }

    internal ModelRequestConfigurationStatus InspectRequestConfigurationStatus(Model model)
    {
        var doc = LoadDocument();
        if (doc is null)
        {
            return ModelRequestConfigurationStatus.Empty;
        }

        using (doc)
        {
            if (!TryGetProviders(doc.RootElement, out var providers) ||
                !TryGetObjectProperty(providers, model.Provider, out var providerConfig))
            {
                return ModelRequestConfigurationStatus.Empty;
            }

            var providerHeaders = ParseStringDictionary(providerConfig, "headers");
            var providerOptionHeaders = ParseRequestOptionHeaders(providerConfig);
            var hasApiKey = HasConfiguredValue(GetString(providerConfig, "apiKey"));
            var hasCredentialHeader =
                HasCredentialHeader(providerHeaders) ||
                HasCredentialHeader(providerOptionHeaders);
            var hasCommandBackedSecret =
                IsCommandBackedValue(GetString(providerConfig, "apiKey")) ||
                HasCommandBackedCredentialHeader(providerHeaders) ||
                HasCommandBackedCredentialHeader(providerOptionHeaders);

            if (providerConfig.TryGetProperty("modelOverrides", out var overrides) &&
                overrides.ValueKind == JsonValueKind.Object &&
                TryGetObjectProperty(overrides, model.Id, out var overrideConfig))
            {
                var overrideHeaders = ParseStringDictionary(overrideConfig, "headers");
                var overrideOptionHeaders = ParseRequestOptionHeaders(overrideConfig);
                hasCredentialHeader |= HasCredentialHeader(overrideHeaders);
                hasCredentialHeader |= HasCredentialHeader(overrideOptionHeaders);
                hasCommandBackedSecret |=
                    HasCommandBackedCredentialHeader(overrideHeaders) ||
                    HasCommandBackedCredentialHeader(overrideOptionHeaders);
            }

            if (providerConfig.TryGetProperty("models", out var configuredModels) &&
                configuredModels.ValueKind == JsonValueKind.Array)
            {
                foreach (var configuredModel in configuredModels.EnumerateArray())
                {
                    if (configuredModel.ValueKind == JsonValueKind.Object &&
                        TryGetString(configuredModel, "id", out var modelId) &&
                        model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        var modelHeaders = ParseStringDictionary(configuredModel, "headers");
                        var modelOptionHeaders = ParseRequestOptionHeaders(configuredModel);
                        hasCredentialHeader |= HasCredentialHeader(modelHeaders);
                        hasCredentialHeader |= HasCredentialHeader(modelOptionHeaders);
                        hasCommandBackedSecret |=
                            HasCommandBackedCredentialHeader(modelHeaders) ||
                            HasCommandBackedCredentialHeader(modelOptionHeaders);
                        break;
                    }
                }
            }

            return new ModelRequestConfigurationStatus(
                hasApiKey,
                hasCredentialHeader,
                hasCommandBackedSecret);
        }
    }

    internal ModelRequestConfigurationStatus InspectProviderConfigurationStatus(string provider)
    {
        var doc = LoadDocument();
        if (doc is null)
        {
            return ModelRequestConfigurationStatus.Empty;
        }

        using (doc)
        {
            if (!TryGetProviders(doc.RootElement, out var providers) ||
                !TryGetObjectProperty(providers, provider, out var providerConfig))
            {
                return ModelRequestConfigurationStatus.Empty;
            }

            var providerHeaders = ParseStringDictionary(providerConfig, "headers");
            var providerOptionHeaders = ParseRequestOptionHeaders(providerConfig);
            return new ModelRequestConfigurationStatus(
                HasConfiguredValue(GetString(providerConfig, "apiKey")),
                HasCredentialHeader(providerHeaders) ||
                HasCredentialHeader(providerOptionHeaders),
                IsCommandBackedValue(GetString(providerConfig, "apiKey")) ||
                HasCommandBackedCredentialHeader(providerHeaders) ||
                HasCommandBackedCredentialHeader(providerOptionHeaders));
        }
    }

    internal IReadOnlyList<DynamicProviderRegistration> GetDynamicProviderRegistrations()
    {
        var doc = LoadDocument();
        if (doc is null)
        {
            return [];
        }

        using (doc)
        {
            if (!TryGetProviders(doc.RootElement, out var providers))
            {
                return [];
            }

            var registrations = new Dictionary<string, DynamicProviderRegistration>(StringComparer.OrdinalIgnoreCase);
            foreach (var providerProp in providers.EnumerateObject())
            {
                if (providerProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                AddDynamicProviderRegistrations(providerProp.Value, registrations);
            }

            return [.. registrations.Values];
        }
    }

    private static void AddDynamicProviderRegistrations(
        JsonElement providerConfig,
        Dictionary<string, DynamicProviderRegistration> registrations)
    {
        var providerApi = ModelApiNames.Normalize(GetString(providerConfig, "api"));
        var providerBaseUrl = GetString(providerConfig, "baseUrl");
        var providerRequestPath = GetRequestPath(providerConfig);
        var providerIsOpenAiCompatible = IsOpenAiCompatibleRegistration(providerConfig);

        if (providerIsOpenAiCompatible &&
            !string.IsNullOrWhiteSpace(providerApi) &&
            !string.IsNullOrWhiteSpace(providerBaseUrl))
        {
            registrations[providerApi] = new DynamicProviderRegistration(
                providerApi,
                providerBaseUrl!,
                providerRequestPath);
        }

        if (!providerConfig.TryGetProperty("models", out var configuredModels) ||
            configuredModels.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var configuredModel in configuredModels.EnumerateArray())
        {
            if (configuredModel.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var modelApi = ModelApiNames.Normalize(GetString(configuredModel, "api"));
            var api = modelApi ?? providerApi;
            var baseUrl = GetString(configuredModel, "baseUrl") ?? providerBaseUrl;
            var requestPath = GetString(configuredModel, "requestPath") is { } modelRequestPath
                ? NormalizeRequestPath(modelRequestPath)
                : providerRequestPath;
            var isOpenAiCompatible = IsOpenAiCompatibleRegistration(configuredModel) || providerIsOpenAiCompatible;

            if (isOpenAiCompatible &&
                !string.IsNullOrWhiteSpace(api) &&
                !string.IsNullOrWhiteSpace(baseUrl))
            {
                registrations[api] = new DynamicProviderRegistration(api, baseUrl!, requestPath);
            }
        }
    }

    private static void ApplyProvider(
        string providerName,
        JsonElement providerConfig,
        Dictionary<string, Dictionary<string, Model>> models)
    {
        var providerApi = ModelApiNames.Normalize(GetString(providerConfig, "api"));
        var providerBaseUrl = GetString(providerConfig, "baseUrl");
        var providerHeaders = ParseStringDictionary(providerConfig, "headers");
        var providerCompat = ParseCompat(providerConfig, "compat");

        if (!models.TryGetValue(providerName, out var bucket))
        {
            bucket = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);
            models[providerName] = bucket;
        }

        if (HasProviderOverride(providerBaseUrl, providerHeaders, providerCompat))
        {
            foreach (var (modelId, model) in bucket.ToArray())
            {
                bucket[modelId] = model with
                {
                    BaseUrl = providerBaseUrl ?? model.BaseUrl,
                    Headers = MergeHeaders(model.Headers, providerHeaders),
                    Compat = MergeCompat(model.Compat, providerCompat)
                };
            }
        }

        ApplyModelOverrides(providerConfig, bucket);
        ApplyCustomModels(providerName, providerConfig, bucket, providerApi, providerBaseUrl, providerHeaders, providerCompat);
    }

    private static void ApplyModelOverrides(JsonElement providerConfig, Dictionary<string, Model> bucket)
    {
        if (!providerConfig.TryGetProperty("modelOverrides", out var overrides) ||
            overrides.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var overrideProp in overrides.EnumerateObject())
        {
            if (overrideProp.Value.ValueKind != JsonValueKind.Object ||
                !bucket.TryGetValue(overrideProp.Name, out var existing))
            {
                continue;
            }

            bucket[overrideProp.Name] = ApplyModelOverride(existing, overrideProp.Value);
        }
    }

    private static void ApplyCustomModels(
        string providerName,
        JsonElement providerConfig,
        Dictionary<string, Model> bucket,
        string? providerApi,
        string? providerBaseUrl,
        IDictionary<string, string>? providerHeaders,
        ModelCompatibility? providerCompat)
    {
        if (!providerConfig.TryGetProperty("models", out var configuredModels) ||
            configuredModels.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var defaultApi = providerApi ?? bucket.Values.FirstOrDefault()?.Api ?? ModelApiNames.OpenAiChatCompletions;
        foreach (var configuredModel in configuredModels.EnumerateArray())
        {
            if (configuredModel.ValueKind != JsonValueKind.Object ||
                !TryGetString(configuredModel, "id", out var modelId))
            {
                continue;
            }

            var model = new Model
            {
                Id = modelId!,
                Name = GetString(configuredModel, "name") ?? modelId!,
                Api = ModelApiNames.Normalize(GetString(configuredModel, "api")) ?? defaultApi,
                Provider = providerName,
                BaseUrl = GetString(configuredModel, "baseUrl") ?? providerBaseUrl ?? string.Empty,
                Reasoning = GetBool(configuredModel, "reasoning") ?? false,
                InputModalities = ParseInputModalities(configuredModel),
                Cost = ParseCost(configuredModel, fallback: null) ?? new ModelCost(0m, 0m, 0m, 0m),
                ContextWindow = GetInt(configuredModel, "contextWindow") ?? 128_000,
                MaxOutputTokens = GetInt(configuredModel, "maxTokens") ?? GetInt(configuredModel, "maxOutputTokens") ?? 16_384,
                Headers = MergeHeaders(providerHeaders, ParseStringDictionary(configuredModel, "headers")),
                Compat = MergeCompat(providerCompat, ParseCompat(configuredModel, "compat"))
            };

            bucket[model.Id] = model;
        }
    }

    private static Model ApplyModelOverride(Model model, JsonElement overrideConfig)
    {
        return model with
        {
            Name = GetString(overrideConfig, "name") ?? model.Name,
            Reasoning = GetBool(overrideConfig, "reasoning") ?? model.Reasoning,
            InputModalities = TryGetInputModalities(overrideConfig, out var input) ? input : model.InputModalities,
            Cost = ParseCost(overrideConfig, model.Cost) ?? model.Cost,
            ContextWindow = GetInt(overrideConfig, "contextWindow") ?? model.ContextWindow,
            MaxOutputTokens = GetInt(overrideConfig, "maxTokens") ?? GetInt(overrideConfig, "maxOutputTokens") ?? model.MaxOutputTokens,
            Headers = MergeHeaders(model.Headers, ParseStringDictionary(overrideConfig, "headers")),
            Compat = MergeCompat(model.Compat, ParseCompat(overrideConfig, "compat"))
        };
    }

    private static ModelCost? ParseCost(JsonElement element, ModelCost? fallback)
    {
        if (!element.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        return new ModelCost(
            GetDecimal(cost, "input") ?? fallback?.InputPerMillion ?? 0m,
            GetDecimal(cost, "output") ?? fallback?.OutputPerMillion ?? 0m,
            GetDecimal(cost, "cacheRead") ?? fallback?.CacheReadPerMillion ?? 0m,
            GetDecimal(cost, "cacheWrite") ?? fallback?.CacheWritePerMillion ?? 0m);
    }

    private static ModelCompatibility? ParseCompat(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var compat) || compat.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ModelCompatibility
        {
            SupportsStore = GetBool(compat, "supportsStore"),
            SupportsDeveloperRole = GetBool(compat, "supportsDeveloperRole"),
            SupportsReasoningEffort = GetBool(compat, "supportsReasoningEffort"),
            ReasoningEffortMap = ParseStringDictionary(compat, "reasoningEffortMap"),
            SupportsUsageInStreaming = GetBool(compat, "supportsUsageInStreaming"),
            MaxTokensField = GetString(compat, "maxTokensField"),
            RequiresToolResultName = GetBool(compat, "requiresToolResultName"),
            RequiresAssistantAfterToolResult = GetBool(compat, "requiresAssistantAfterToolResult"),
            RequiresThinkingAsText = GetBool(compat, "requiresThinkingAsText"),
            ThinkingFormat = GetString(compat, "thinkingFormat"),
            OpenRouterRouting = ParseObjectDictionary(compat, "openRouterRouting"),
            VercelGatewayRouting = ParseVercelGatewayRouting(compat, "vercelGatewayRouting"),
            ZaiToolStream = GetBool(compat, "zaiToolStream"),
            SupportsStrictMode = GetBool(compat, "supportsStrictMode")
        };
    }

    private static ModelCompatibility? MergeCompat(ModelCompatibility? baseCompat, ModelCompatibility? overrideCompat)
    {
        if (baseCompat is null)
        {
            return overrideCompat;
        }

        if (overrideCompat is null)
        {
            return baseCompat;
        }

        return new ModelCompatibility
        {
            SupportsStore = overrideCompat.SupportsStore ?? baseCompat.SupportsStore,
            SupportsDeveloperRole = overrideCompat.SupportsDeveloperRole ?? baseCompat.SupportsDeveloperRole,
            SupportsReasoningEffort = overrideCompat.SupportsReasoningEffort ?? baseCompat.SupportsReasoningEffort,
            ReasoningEffortMap = MergeStringDictionaries(baseCompat.ReasoningEffortMap, overrideCompat.ReasoningEffortMap),
            SupportsUsageInStreaming = overrideCompat.SupportsUsageInStreaming ?? baseCompat.SupportsUsageInStreaming,
            MaxTokensField = overrideCompat.MaxTokensField ?? baseCompat.MaxTokensField,
            RequiresToolResultName = overrideCompat.RequiresToolResultName ?? baseCompat.RequiresToolResultName,
            RequiresAssistantAfterToolResult = overrideCompat.RequiresAssistantAfterToolResult ?? baseCompat.RequiresAssistantAfterToolResult,
            RequiresThinkingAsText = overrideCompat.RequiresThinkingAsText ?? baseCompat.RequiresThinkingAsText,
            ThinkingFormat = overrideCompat.ThinkingFormat ?? baseCompat.ThinkingFormat,
            OpenRouterRouting = MergeObjectDictionaries(baseCompat.OpenRouterRouting, overrideCompat.OpenRouterRouting),
            VercelGatewayRouting = MergeVercelGatewayRouting(baseCompat.VercelGatewayRouting, overrideCompat.VercelGatewayRouting),
            ZaiToolStream = overrideCompat.ZaiToolStream ?? baseCompat.ZaiToolStream,
            SupportsStrictMode = overrideCompat.SupportsStrictMode ?? baseCompat.SupportsStrictMode
        };
    }

    private static VercelGatewayRouting? ParseVercelGatewayRouting(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var routing) || routing.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new VercelGatewayRouting
        {
            Only = ParseStringArray(routing, "only"),
            Order = ParseStringArray(routing, "order")
        };
    }

    private static VercelGatewayRouting? MergeVercelGatewayRouting(
        VercelGatewayRouting? baseRouting,
        VercelGatewayRouting? overrideRouting)
    {
        if (baseRouting is null)
        {
            return overrideRouting;
        }

        if (overrideRouting is null)
        {
            return baseRouting;
        }

        return new VercelGatewayRouting
        {
            Only = overrideRouting.Only ?? baseRouting.Only,
            Order = overrideRouting.Order ?? baseRouting.Order
        };
    }

    private static Dictionary<string, string>? ParseStringDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in value.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                result[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static IDictionary<string, object>? ParseObjectDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in value.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }

        return result.Count == 0 ? null : result;
    }

    private static ModelRequestOptionsConfiguration ParseRequestOptions(JsonElement element)
    {
        if (!element.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object)
        {
            return ModelRequestOptionsConfiguration.Empty;
        }

        return new ModelRequestOptionsConfiguration(
            Temperature: GetFloat(options, "temperature"),
            MaxTokens: GetInt(options, "maxTokens"),
            TopP: GetFloat(options, "topP"),
            Transport: ParseEnum<StreamTransport>(GetString(options, "transport")),
            CacheRetention: ParseEnum<CacheRetention>(GetString(options, "cacheRetention")),
            SessionId: GetString(options, "sessionId"),
            Timeout: ParseTimeout(options),
            MaxRetryDelay: ParseMaxRetryDelay(options),
            MaxRetries: ParseMaxRetries(options),
            WebSocketConnectTimeout: ParseWebSocketConnectTimeout(options),
            Headers: ResolveHeaders(ParseRequestOptionHeaders(element)),
            Metadata: ParseObjectDictionary(options, "metadata"),
            Reasoning: ParseEnum<ThinkingLevel>(GetString(options, "reasoning")),
            ThinkingBudgets: ParseThinkingBudgets(options),
            ProviderSpecific: ParseProviderSpecificOptions(options));
    }

    private static ModelRequestOptionsConfiguration MergeRequestOptions(
        ModelRequestOptionsConfiguration baseOptions,
        ModelRequestOptionsConfiguration overrideOptions)
    {
        if (ReferenceEquals(baseOptions, ModelRequestOptionsConfiguration.Empty))
        {
            return overrideOptions;
        }

        if (ReferenceEquals(overrideOptions, ModelRequestOptionsConfiguration.Empty))
        {
            return baseOptions;
        }

        return new ModelRequestOptionsConfiguration(
            Temperature: overrideOptions.Temperature ?? baseOptions.Temperature,
            MaxTokens: overrideOptions.MaxTokens ?? baseOptions.MaxTokens,
            TopP: overrideOptions.TopP ?? baseOptions.TopP,
            Transport: overrideOptions.Transport ?? baseOptions.Transport,
            CacheRetention: overrideOptions.CacheRetention ?? baseOptions.CacheRetention,
            SessionId: overrideOptions.SessionId ?? baseOptions.SessionId,
            Timeout: overrideOptions.Timeout ?? baseOptions.Timeout,
            MaxRetryDelay: overrideOptions.MaxRetryDelay ?? baseOptions.MaxRetryDelay,
            MaxRetries: overrideOptions.MaxRetries ?? baseOptions.MaxRetries,
            WebSocketConnectTimeout: overrideOptions.WebSocketConnectTimeout ?? baseOptions.WebSocketConnectTimeout,
            Headers: MergeHeaders(baseOptions.Headers, overrideOptions.Headers),
            Metadata: MergeObjectDictionaries(baseOptions.Metadata, overrideOptions.Metadata),
            Reasoning: overrideOptions.Reasoning ?? baseOptions.Reasoning,
            ThinkingBudgets: MergeThinkingBudgets(baseOptions.ThinkingBudgets, overrideOptions.ThinkingBudgets),
            ProviderSpecific: MergeProviderSpecificOptions(baseOptions.ProviderSpecific, overrideOptions.ProviderSpecific));
    }

    private static ModelProviderSpecificOptionsConfiguration? ParseProviderSpecificOptions(JsonElement options)
    {
        var parsed = new ModelProviderSpecificOptionsConfiguration(
            ReasoningEffort: GetString(options, "reasoningEffort"),
            ReasoningSummary: GetString(options, "reasoningSummary"),
            ServiceTier: GetString(options, "serviceTier"),
            TextVerbosity: GetString(options, "textVerbosity"),
            PromptMode: GetString(options, "promptMode"),
            ToolChoice: ParseToolChoice(options),
            ThinkingEnabled: GetBool(options, "thinkingEnabled"),
            ThinkingBudgetTokens: GetInt(options, "thinkingBudgetTokens"),
            Effort: GetString(options, "effort"),
            ThinkingDisplay: GetString(options, "thinkingDisplay"),
            ThinkingLevel: GetString(options, "thinkingLevel"),
            InterleavedThinking: GetBool(options, "interleavedThinking"),
            Region: GetString(options, "region"),
            Profile: GetString(options, "profile"),
            BearerToken: GetString(options, "bearerToken"),
            RequestMetadata: ParseStringDictionary(options, "requestMetadata"),
            AzureApiVersion: GetString(options, "azureApiVersion"),
            AzureResourceName: GetString(options, "azureResourceName"),
            AzureBaseUrl: GetString(options, "azureBaseUrl"),
            AzureDeploymentName: GetString(options, "azureDeploymentName"),
            Project: GetString(options, "project"),
            Location: GetString(options, "location"),
            ProjectId: GetString(options, "projectId"));

        return parsed.IsEmpty ? null : parsed;
    }

    private static IDictionary<string, string>? ParseRequestOptionHeaders(JsonElement element)
    {
        return element.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object
            ? ParseStringDictionary(options, "headers")
            : null;
    }

    private static ModelProviderSpecificOptionsConfiguration? MergeProviderSpecificOptions(
        ModelProviderSpecificOptionsConfiguration? baseOptions,
        ModelProviderSpecificOptionsConfiguration? overrideOptions)
    {
        if (baseOptions is null)
        {
            return overrideOptions;
        }

        if (overrideOptions is null)
        {
            return baseOptions;
        }

        return new ModelProviderSpecificOptionsConfiguration(
            ReasoningEffort: overrideOptions.ReasoningEffort ?? baseOptions.ReasoningEffort,
            ReasoningSummary: overrideOptions.ReasoningSummary ?? baseOptions.ReasoningSummary,
            ServiceTier: overrideOptions.ServiceTier ?? baseOptions.ServiceTier,
            TextVerbosity: overrideOptions.TextVerbosity ?? baseOptions.TextVerbosity,
            PromptMode: overrideOptions.PromptMode ?? baseOptions.PromptMode,
            ToolChoice: overrideOptions.ToolChoice ?? baseOptions.ToolChoice,
            ThinkingEnabled: overrideOptions.ThinkingEnabled ?? baseOptions.ThinkingEnabled,
            ThinkingBudgetTokens: overrideOptions.ThinkingBudgetTokens ?? baseOptions.ThinkingBudgetTokens,
            Effort: overrideOptions.Effort ?? baseOptions.Effort,
            ThinkingDisplay: overrideOptions.ThinkingDisplay ?? baseOptions.ThinkingDisplay,
            ThinkingLevel: overrideOptions.ThinkingLevel ?? baseOptions.ThinkingLevel,
            InterleavedThinking: overrideOptions.InterleavedThinking ?? baseOptions.InterleavedThinking,
            Region: overrideOptions.Region ?? baseOptions.Region,
            Profile: overrideOptions.Profile ?? baseOptions.Profile,
            BearerToken: overrideOptions.BearerToken ?? baseOptions.BearerToken,
            RequestMetadata: MergeHeaders(baseOptions.RequestMetadata, overrideOptions.RequestMetadata),
            AzureApiVersion: overrideOptions.AzureApiVersion ?? baseOptions.AzureApiVersion,
            AzureResourceName: overrideOptions.AzureResourceName ?? baseOptions.AzureResourceName,
            AzureBaseUrl: overrideOptions.AzureBaseUrl ?? baseOptions.AzureBaseUrl,
            AzureDeploymentName: overrideOptions.AzureDeploymentName ?? baseOptions.AzureDeploymentName,
            Project: overrideOptions.Project ?? baseOptions.Project,
            Location: overrideOptions.Location ?? baseOptions.Location,
            ProjectId: overrideOptions.ProjectId ?? baseOptions.ProjectId);
    }

    private static ModelToolChoiceConfiguration? ParseToolChoice(JsonElement options)
    {
        if (!options.TryGetProperty("toolChoice", out var toolChoice))
        {
            return null;
        }

        if (toolChoice.ValueKind == JsonValueKind.String)
        {
            var value = toolChoice.GetString();
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new ModelToolChoiceConfiguration(value);
        }

        if (toolChoice.ValueKind == JsonValueKind.Object &&
            string.Equals(GetString(toolChoice, "type"), "function", StringComparison.Ordinal) &&
            toolChoice.TryGetProperty("function", out var function) &&
            function.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(function, "name");
            return string.IsNullOrWhiteSpace(name)
                ? null
                : new ModelToolChoiceConfiguration("function", FunctionName: name);
        }

        if (toolChoice.ValueKind == JsonValueKind.Object &&
            string.Equals(GetString(toolChoice, "type"), "tool", StringComparison.Ordinal))
        {
            var name = GetString(toolChoice, "name");
            return string.IsNullOrWhiteSpace(name)
                ? null
                : new ModelToolChoiceConfiguration("tool", ToolName: name);
        }

        return null;
    }

    private static TimeSpan? ParseMaxRetryDelay(JsonElement options)
    {
        var delayMs = GetInt(options, "maxRetryDelayMs");
        if (delayMs is null)
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, delayMs.Value));
    }

    private static TimeSpan? ParseTimeout(JsonElement options)
    {
        var timeoutMs = GetInt(options, "timeoutMs");
        if (timeoutMs is null)
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs.Value));
    }

    private static int? ParseMaxRetries(JsonElement options)
    {
        var retries = GetInt(options, "maxRetries");
        return retries is null ? null : Math.Max(0, retries.Value);
    }

    private static TimeSpan? ParseWebSocketConnectTimeout(JsonElement options)
    {
        var timeoutMs = GetInt(options, "websocketConnectTimeoutMs");
        if (timeoutMs is null)
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs.Value));
    }

    private static ThinkingBudgets? ParseThinkingBudgets(JsonElement options)
    {
        if (!options.TryGetProperty("thinkingBudgets", out var budgets) || budgets.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parsed = new ThinkingBudgets
        {
            Minimal = GetInt(budgets, "minimal"),
            Low = GetInt(budgets, "low"),
            Medium = GetInt(budgets, "medium"),
            High = GetInt(budgets, "high")
        };

        return parsed.Minimal is null &&
               parsed.Low is null &&
               parsed.Medium is null &&
               parsed.High is null
            ? null
            : parsed;
    }

    private static ThinkingBudgets? MergeThinkingBudgets(ThinkingBudgets? baseBudgets, ThinkingBudgets? overrideBudgets)
    {
        if (baseBudgets is null)
        {
            return overrideBudgets;
        }

        if (overrideBudgets is null)
        {
            return baseBudgets;
        }

        return new ThinkingBudgets
        {
            Minimal = overrideBudgets.Minimal ?? baseBudgets.Minimal,
            Low = overrideBudgets.Low ?? baseBudgets.Low,
            Medium = overrideBudgets.Medium ?? baseBudgets.Medium,
            High = overrideBudgets.High ?? baseBudgets.High
        };
    }

    private static IReadOnlyList<string> ParseInputModalities(JsonElement element)
    {
        return TryGetInputModalities(element, out var input) ? input : ["text"];
    }

    private static bool TryGetInputModalities(JsonElement element, out IReadOnlyList<string> input)
    {
        input = [];
        if (!element.TryGetProperty("input", out var inputElement) &&
            !element.TryGetProperty("inputModalities", out inputElement))
        {
            return false;
        }

        var parsed = ParseStringArray(inputElement);
        if (parsed.Count == 0)
        {
            return false;
        }

        input = parsed;
        return true;
    }

    private static IReadOnlyList<string>? ParseStringArray(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var array)
            ? ParseStringArray(array)
            : null;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                result.Add(item.GetString()!);
            }
        }

        return result;
    }

    private static IDictionary<string, string>? MergeHeaders(
        IDictionary<string, string>? baseHeaders,
        IDictionary<string, string>? overrideHeaders)
    {
        if (baseHeaders is null || baseHeaders.Count == 0)
        {
            return overrideHeaders is null || overrideHeaders.Count == 0
                ? null
                : new Dictionary<string, string>(overrideHeaders, StringComparer.OrdinalIgnoreCase);
        }

        if (overrideHeaders is null || overrideHeaders.Count == 0)
        {
            return new Dictionary<string, string>(baseHeaders, StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(baseHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrideHeaders)
        {
            result[key] = value;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string>? MergeStringDictionaries(
        IReadOnlyDictionary<string, string>? baseValues,
        IReadOnlyDictionary<string, string>? overrideValues)
    {
        if (baseValues is null || baseValues.Count == 0)
        {
            return overrideValues;
        }

        if (overrideValues is null || overrideValues.Count == 0)
        {
            return baseValues;
        }

        var result = new Dictionary<string, string>(baseValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrideValues)
        {
            result[key] = value;
        }

        return result;
    }

    private static IDictionary<string, object>? MergeObjectDictionaries(
        IDictionary<string, object>? baseValues,
        IDictionary<string, object>? overrideValues)
    {
        if (baseValues is null || baseValues.Count == 0)
        {
            return overrideValues;
        }

        if (overrideValues is null || overrideValues.Count == 0)
        {
            return baseValues;
        }

        var result = new Dictionary<string, object>(baseValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrideValues)
        {
            result[key] = value;
        }

        return result;
    }

    private static bool HasProviderOverride(
        string? providerBaseUrl,
        IDictionary<string, string>? providerHeaders,
        ModelCompatibility? providerCompat)
    {
        return !string.IsNullOrWhiteSpace(providerBaseUrl) ||
               providerHeaders is { Count: > 0 } ||
               providerCompat is not null;
    }

    private JsonDocument? LoadDocument()
    {
        var path = _searchPaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProviders(JsonElement root, out JsonElement providers)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("providers", out providers) &&
            providers.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        providers = default;
        return false;
    }

    private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                candidate.Value.ValueKind == JsonValueKind.Object)
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static IDictionary<string, string>? ResolveHeaders(IDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            var resolved = ResolveConfigValue(value);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                result[key] = resolved;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static bool HasConfiguredValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool HasCredentialHeader(IDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return false;
        }

        foreach (var (key, value) in headers)
        {
            if (HasConfiguredValue(value) && IsCredentialHeaderName(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCommandBackedCredentialHeader(IDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return false;
        }

        foreach (var (key, value) in headers)
        {
            if (IsCredentialHeaderName(key) && IsCommandBackedValue(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCredentialHeaderName(string headerName)
    {
        return headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase) ||
               headerName.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandBackedValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith('!');

    private static string? ResolveConfigValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.StartsWith('!'))
        {
            return ResolveCommandValue(value[1..].Trim());
        }

        var environmentValue = Environment.GetEnvironmentVariable(value);
        return string.IsNullOrWhiteSpace(environmentValue) ? value : environmentValue;
    }

    private static string? ResolveCommandValue(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        try
        {
            var isWindows = OperatingSystem.IsWindows();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "/bin/sh",
                    Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000) || process.ExitCode != 0)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsOpenAiCompatibleRegistration(JsonElement element)
    {
        var kind = GetString(element, "apiKind") ?? GetString(element, "apiType");
        if (string.IsNullOrWhiteSpace(kind))
        {
            return false;
        }

        return kind.Trim().ToLowerInvariant() switch
        {
            "openai-compatible" => true,
            "openai-completions" => true,
            ModelApiNames.OpenAiChatCompletions => true,
            _ => false
        };
    }

    private static string GetRequestPath(JsonElement element)
    {
        var requestPath = GetString(element, "requestPath");
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return "/chat/completions";
        }

        return NormalizeRequestPath(requestPath);
    }

    private static string NormalizeRequestPath(string requestPath)
    {
        requestPath = requestPath.Trim();
        return requestPath.StartsWith('/') ? requestPath : $"/{requestPath}";
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = GetString(element, propertyName);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static decimal? GetDecimal(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDecimal(out var value)
            ? value
            : null;
    }

    private static float? GetFloat(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetSingle(out var value)
            ? value
            : null;
    }

    private static TEnum? ParseEnum<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (typeof(TEnum) == typeof(ThinkingLevel) &&
            string.Equals(normalized, "xhigh", StringComparison.OrdinalIgnoreCase))
        {
            return (TEnum)(object)ThinkingLevel.ExtraHigh;
        }

        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetDefaultSearchPaths()
    {
        var configured = Environment.GetEnvironmentVariable("TAU_MODELS_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        yield return Path.Combine(Directory.GetCurrentDirectory(), ".tau", "models.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".tau", "models.json");
        }
    }
}

internal sealed record ModelRequestConfiguration(
    string? ApiKey,
    IDictionary<string, string>? Headers,
    bool AuthHeader,
    ModelRequestOptionsConfiguration Options)
{
    public static ModelRequestConfiguration Empty { get; } = new(
        null,
        null,
        false,
        ModelRequestOptionsConfiguration.Empty);
}

internal sealed record ModelRequestOptionsConfiguration(
    float? Temperature,
    int? MaxTokens,
    float? TopP,
    StreamTransport? Transport,
    CacheRetention? CacheRetention,
    string? SessionId,
    TimeSpan? Timeout,
    TimeSpan? MaxRetryDelay,
    int? MaxRetries,
    TimeSpan? WebSocketConnectTimeout,
    IDictionary<string, string>? Headers,
    IDictionary<string, object>? Metadata,
    ThinkingLevel? Reasoning,
    ThinkingBudgets? ThinkingBudgets,
    ModelProviderSpecificOptionsConfiguration? ProviderSpecific)
{
    public static ModelRequestOptionsConfiguration Empty { get; } = new(
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}

internal sealed record ModelProviderSpecificOptionsConfiguration(
    string? ReasoningEffort,
    string? ReasoningSummary,
    string? ServiceTier,
    string? TextVerbosity,
    string? PromptMode,
    ModelToolChoiceConfiguration? ToolChoice,
    bool? ThinkingEnabled,
    int? ThinkingBudgetTokens,
    string? Effort,
    string? ThinkingDisplay,
    string? ThinkingLevel,
    bool? InterleavedThinking,
    string? Region,
    string? Profile,
    string? BearerToken,
    IDictionary<string, string>? RequestMetadata,
    string? AzureApiVersion,
    string? AzureResourceName,
    string? AzureBaseUrl,
    string? AzureDeploymentName,
    string? Project,
    string? Location,
    string? ProjectId)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ReasoningEffort) &&
        string.IsNullOrWhiteSpace(ReasoningSummary) &&
        string.IsNullOrWhiteSpace(ServiceTier) &&
        string.IsNullOrWhiteSpace(TextVerbosity) &&
        string.IsNullOrWhiteSpace(PromptMode) &&
        ToolChoice is null &&
        ThinkingEnabled is null &&
        ThinkingBudgetTokens is null &&
        string.IsNullOrWhiteSpace(Effort) &&
        string.IsNullOrWhiteSpace(ThinkingDisplay) &&
        string.IsNullOrWhiteSpace(ThinkingLevel) &&
        InterleavedThinking is null &&
        string.IsNullOrWhiteSpace(Region) &&
        string.IsNullOrWhiteSpace(Profile) &&
        string.IsNullOrWhiteSpace(BearerToken) &&
        (RequestMetadata is null || RequestMetadata.Count == 0) &&
        string.IsNullOrWhiteSpace(AzureApiVersion) &&
        string.IsNullOrWhiteSpace(AzureResourceName) &&
        string.IsNullOrWhiteSpace(AzureBaseUrl) &&
        string.IsNullOrWhiteSpace(AzureDeploymentName) &&
        string.IsNullOrWhiteSpace(Project) &&
        string.IsNullOrWhiteSpace(Location) &&
        string.IsNullOrWhiteSpace(ProjectId);
}

internal sealed record ModelToolChoiceConfiguration(string Kind, string? FunctionName = null, string? ToolName = null)
{
    public bool IsFunction => FunctionName is not null;
    public bool IsTool => ToolName is not null;
}

internal sealed record ModelRequestConfigurationStatus(
    bool HasApiKey,
    bool HasCredentialHeader,
    bool HasCommandBackedSecret)
{
    public static ModelRequestConfigurationStatus Empty { get; } = new(false, false, false);

    public bool IsConfigured => HasApiKey || HasCredentialHeader;
}

internal sealed record DynamicProviderRegistration(
    string Api,
    string BaseUrl,
    string RequestPath);
