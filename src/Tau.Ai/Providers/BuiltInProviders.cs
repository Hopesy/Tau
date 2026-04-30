using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Providers;

/// <summary>
/// Registers built-in providers with lazy initialization.
/// </summary>
public static class BuiltInProviders
{
    public static void RegisterAll(ProviderRegistry registry)
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

}
