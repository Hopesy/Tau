using Tau.Ai.Streaming;
using Tau.Ai.Auth;

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
        var resolvedOptions = options with
        {
            ApiKey = AuthResolver.ResolveApiKey(resolvedModel.Provider, options.ApiKey)
        };

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
        var resolvedOptions = options with
        {
            ApiKey = AuthResolver.ResolveApiKey(resolvedModel.Provider, options.ApiKey)
        };

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
}
