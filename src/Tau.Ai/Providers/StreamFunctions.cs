using Tau.Ai.Streaming;
using Tau.Ai.Auth;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

/// <summary>
/// Top-level convenience functions for streaming LLM responses.
/// Mirrors pi-mono's stream.ts exports.
/// </summary>
public static class StreamFunctions
{
    private static readonly ProviderAuthResolver AuthResolver = new();

    public static AssistantMessageStream Stream(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        StreamOptions options)
    {
        var resolvedModel = AuthResolver.ResolveModel(model);
        var resolvedOptions = ResolveOptions(resolvedModel, options);

        var provider = registry.Get(resolvedModel.Api);
        return provider.Stream(resolvedModel, context, resolvedOptions);
    }

    public static AssistantMessageStream StreamSimple(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        SimpleStreamOptions options)
    {
        var resolvedModel = AuthResolver.ResolveModel(model);
        var resolvedOptions = (SimpleStreamOptions)ResolveOptions(resolvedModel, options);

        var provider = registry.Get(resolvedModel.Api);
        return provider.StreamSimple(resolvedModel, context, resolvedOptions);
    }

    public static async Task<AssistantMessage> CompleteAsync(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        StreamOptions options)
    {
        var stream = Stream(registry, model, context, options);
        return await stream.ResultAsync.ConfigureAwait(false);
    }

    public static async Task<AssistantMessage> CompleteSimpleAsync(
        ProviderRegistry registry,
        Model model,
        LlmContext context,
        SimpleStreamOptions options)
    {
        var stream = StreamSimple(registry, model, context, options);
        return await stream.ResultAsync.ConfigureAwait(false);
    }

    private static StreamOptions ResolveOptions(Model model, StreamOptions options)
    {
        var requestConfig = new ModelConfigurationStore().ResolveRequestConfiguration(model);
        var apiKey = AuthResolver.ResolveApiKey(model.Provider, options.ApiKey) ?? requestConfig.ApiKey;
        var headers = MergeHeaders(requestConfig.Headers, options.Headers);
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
            Headers = headers
        };
    }

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
}
