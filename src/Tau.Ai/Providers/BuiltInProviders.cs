using Tau.Ai.Auth;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Providers.OpenRouter;
using Tau.Ai.Registry;

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
}
