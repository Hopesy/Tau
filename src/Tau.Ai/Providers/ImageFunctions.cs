using Tau.Ai.Auth;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

public static class ImageFunctions
{
    private static readonly ProviderAuthResolver AuthResolver = new();
    private static readonly Lazy<ImagesProviderRegistry> BuiltInRegistry = new(
        () => BuiltInProviders.CreateBuiltInImagesRegistry());

    public static Task<AssistantImages> GenerateImagesAsync(
        ImagesModel model,
        ImagesContext context,
        ImagesOptions? options = null,
        ProviderAuthResolver? authResolver = null) =>
        GenerateImagesAsync(BuiltInRegistry.Value, model, context, options, authResolver);

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
        EnsureProviderMatchesModelApi(provider, model);
        return await provider.GenerateImagesAsync(model, context, resolvedOptions).ConfigureAwait(false);
    }

    private static void EnsureProviderMatchesModelApi(IImagesProvider provider, ImagesModel model)
    {
        var expected = ModelApiNames.Normalize(model.Api) ?? model.Api;
        var actual = ModelApiNames.Normalize(provider.Api) ?? provider.Api;
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException($"Mismatched image provider API: {actual} expected {expected}.");
    }
}
