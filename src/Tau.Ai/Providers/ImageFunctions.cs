using Tau.Ai.Auth;

namespace Tau.Ai.Providers;

public static class ImageFunctions
{
    private static readonly ProviderAuthResolver AuthResolver = new();

    public static async Task<AssistantImages> GenerateImagesAsync(
        ImagesProviderRegistry registry,
        ImagesModel model,
        ImagesContext context,
        ImagesOptions? options = null,
        ProviderAuthResolver? authResolver = null)
    {
        options ??= new ImagesOptions();
        var resolver = authResolver ?? AuthResolver;
        var apiKey = resolver.ResolveApiKey(model.Provider, options.ApiKey, options.Env);
        var resolvedOptions = options with
        {
            ApiKey = apiKey
        };

        var provider = registry.Get(model.Api);
        return await provider.GenerateImagesAsync(model, context, resolvedOptions).ConfigureAwait(false);
    }
}
