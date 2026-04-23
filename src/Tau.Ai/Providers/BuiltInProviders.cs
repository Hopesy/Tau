using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Providers.OpenAi;

namespace Tau.Ai.Providers;

/// <summary>
/// Registers built-in providers with lazy initialization.
/// </summary>
public static class BuiltInProviders
{
    public static void RegisterAll(ProviderRegistry registry)
    {
        registry.Register("openai-chat-completions", () => new OpenAiProvider(), sourceId: "builtin");
        registry.Register("openai-responses", () => new OpenAiCompatibleProvider("openai-responses", "https://api.openai.com/v1"), sourceId: "builtin");
        registry.Register("openai-codex-responses", () => new OpenAiCompatibleProvider("openai-codex-responses", "https://api.openai.com/v1"), sourceId: "builtin");
        registry.Register("azure-openai-responses", () => new OpenAiCompatibleProvider("azure-openai-responses", ResolveAzureBaseUrl(), authHeaderName: "api-key", authHeaderPrefix: null), sourceId: "builtin");
        registry.Register("mistral-conversations", () => new OpenAiCompatibleProvider("mistral-conversations", "https://api.mistral.ai/v1"), sourceId: "builtin");
        registry.Register("anthropic-messages", () => new AnthropicProvider(), sourceId: "builtin");
        registry.Register("google-generative-language", () => new GoogleProvider(), sourceId: "builtin");
        registry.Register("google-vertex", () => new GoogleVertexProvider(), sourceId: "builtin");
        registry.Register("google-gemini-cli", () => new GoogleGeminiCliProvider(), sourceId: "builtin");
        registry.Register("bedrock-converse-stream", () => new BedrockProvider(), sourceId: "builtin");
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

    private static string ResolveAzureBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("AZURE_OPENAI_BASE_URL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        var resourceName = Environment.GetEnvironmentVariable("AZURE_OPENAI_RESOURCE_NAME");
        return !string.IsNullOrWhiteSpace(resourceName)
            ? $"https://{resourceName}.openai.azure.com/openai/v1"
            : string.Empty;
    }
}
