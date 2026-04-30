namespace Tau.Ai.Registry;

public static class BuiltInModels
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, Model>> Catalog =
        new Dictionary<string, IReadOnlyDictionary<string, Model>>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4.1"] = Create("gpt-4.1", "GPT-4.1", "openai-chat-completions", "openai", "https://api.openai.com/v1", true, 1_047_576, 32_768, 2.0m, 8.0m),
                ["gpt-4o-mini"] = Create("gpt-4o-mini", "GPT-4o mini", "openai-chat-completions", "openai", "https://api.openai.com/v1", false, 128_000, 16_384, 0.15m, 0.60m),
                ["gpt-5"] = Create("gpt-5", "GPT-5", "openai-chat-completions", "openai", "https://api.openai.com/v1", true, 400_000, 128_000, 1.25m, 10.0m),
                ["gpt-5-mini"] = Create("gpt-5-mini", "GPT-5 mini", "openai-chat-completions", "openai", "https://api.openai.com/v1", true, 400_000, 128_000, 0.25m, 2.0m),
                ["gpt-5.4"] = Create("gpt-5.4", "GPT-5.4", "openai-chat-completions", "openai", "https://api.openai.com/v1", true, 400_000, 128_000, 1.25m, 10.0m),
                ["gpt-5.4-mini"] = Create("gpt-5.4-mini", "GPT-5.4 Mini", "openai-chat-completions", "openai", "https://api.openai.com/v1", true, 400_000, 128_000, 0.25m, 2.0m)
            },
            ["anthropic"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-sonnet-4-20250514"] = Create("claude-sonnet-4-20250514", "Claude Sonnet 4", "anthropic-messages", "anthropic", "https://api.anthropic.com", true, 200_000, 8_192, 3.0m, 15.0m),
                ["claude-opus-4-6"] = Create("claude-opus-4-6", "Claude Opus 4.6", "anthropic-messages", "anthropic", "https://api.anthropic.com", true, 200_000, 8_192, 15.0m, 75.0m)
            },
            ["google"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-flash"] = Create("gemini-2.5-flash", "Gemini 2.5 Flash", "google-generative-language", "google", "https://generativelanguage.googleapis.com", true, 1_048_576, 65_536, 0.30m, 2.50m),
                ["gemini-2.5-pro"] = Create("gemini-2.5-pro", "Gemini 2.5 Pro", "google-generative-language", "google", "https://generativelanguage.googleapis.com", true, 1_048_576, 65_536, 1.25m, 10.0m)
            },
            ["azure-openai-responses"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4.1"] = Create("gpt-4.1", "GPT-4.1 (Azure)", "azure-openai-responses", "azure-openai-responses", string.Empty, true, 1_047_576, 32_768, 2.0m, 8.0m),
                ["gpt-4o-mini"] = Create("gpt-4o-mini", "GPT-4o mini (Azure)", "azure-openai-responses", "azure-openai-responses", string.Empty, false, 128_000, 16_384, 0.15m, 0.60m),
                ["gpt-5.2"] = Create("gpt-5.2", "GPT-5.2 (Azure)", "azure-openai-responses", "azure-openai-responses", string.Empty, true, 400_000, 128_000, 1.25m, 10.0m)
            },
            ["openai-codex"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-5-codex"] = Create("gpt-5-codex", "GPT-5 Codex", "openai-codex-responses", "openai-codex", "https://chatgpt.com/backend-api", true, 400_000, 32_768, 1.25m, 10.0m),
                ["gpt-5.2-codex"] = Create("gpt-5.2-codex", "GPT-5.2 Codex", "openai-codex-responses", "openai-codex", "https://chatgpt.com/backend-api", true, 400_000, 32_768, 1.25m, 10.0m),
                ["gpt-5.4"] = Create("gpt-5.4", "GPT-5.4 (Codex)", "openai-codex-responses", "openai-codex", "https://chatgpt.com/backend-api", true, 400_000, 128_000, 1.25m, 10.0m)
            },
            ["github-copilot"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4o"] = Create("gpt-4o", "GPT-4o (GitHub Copilot)", "openai-responses", "github-copilot", "https://api.individual.githubcopilot.com", false, 128_000, 16_384, 0m, 0m) with
                {
                    Headers = Tau.Ai.Providers.GitHubCopilotHeaders.CreateStaticHeaders(),
                    InputModalities = ["text", "image"]
                },
                ["claude-sonnet-4"] = Create("claude-sonnet-4", "Claude Sonnet 4 (GitHub Copilot)", "openai-responses", "github-copilot", "https://api.individual.githubcopilot.com", true, 200_000, 8_192, 0m, 0m) with
                {
                    Headers = Tau.Ai.Providers.GitHubCopilotHeaders.CreateStaticHeaders(),
                    InputModalities = ["text", "image"]
                },
                ["gpt-5.3-codex"] = Create("gpt-5.3-codex", "GPT-5.3 Codex (GitHub Copilot)", "openai-responses", "github-copilot", "https://api.individual.githubcopilot.com", true, 400_000, 32_768, 0m, 0m) with
                {
                    Headers = Tau.Ai.Providers.GitHubCopilotHeaders.CreateStaticHeaders(),
                    InputModalities = ["text", "image"]
                }
            },
            ["mistral"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["mistral-small-latest"] = Create("mistral-small-latest", "Mistral Small", "mistral-conversations", "mistral", "https://api.mistral.ai/v1", true, 128_000, 32_768, 0.20m, 0.60m),
                ["devstral-medium-latest"] = Create("devstral-medium-latest", "Devstral Medium", "mistral-conversations", "mistral", "https://api.mistral.ai/v1", true, 128_000, 32_768, 2.0m, 6.0m)
            },
            ["google-vertex"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-flash"] = Create("gemini-2.5-flash", "Gemini 2.5 Flash (Vertex)", "google-vertex", "google-vertex", string.Empty, true, 1_048_576, 65_536, 0.30m, 2.50m),
                ["gemini-2.5-pro"] = Create("gemini-2.5-pro", "Gemini 2.5 Pro (Vertex)", "google-vertex", "google-vertex", string.Empty, true, 1_048_576, 65_536, 1.25m, 10.0m),
                ["gemini-3-pro-preview"] = Create("gemini-3-pro-preview", "Gemini 3 Pro Preview (Vertex)", "google-vertex", "google-vertex", string.Empty, true, 1_048_576, 65_536, 1.25m, 10.0m)
            },
            ["google-gemini-cli"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-flash"] = Create("gemini-2.5-flash", "Gemini 2.5 Flash (Gemini CLI)", "google-gemini-cli", "google-gemini-cli", "https://cloudcode-pa.googleapis.com", true, 1_048_576, 65_536, 0m, 0m),
                ["gemini-2.5-pro"] = Create("gemini-2.5-pro", "Gemini 2.5 Pro (Gemini CLI)", "google-gemini-cli", "google-gemini-cli", "https://cloudcode-pa.googleapis.com", true, 1_048_576, 65_536, 0m, 0m)
            },
            ["google-antigravity"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["gemini-2.5-pro"] = Create("gemini-2.5-pro", "Gemini 2.5 Pro (Antigravity)", "google-gemini-cli", "google-antigravity", string.Empty, true, 1_048_576, 65_536, 0m, 0m),
                ["gemini-3.1-pro-high"] = Create("gemini-3.1-pro-high", "Gemini 3.1 Pro High (Antigravity)", "google-gemini-cli", "google-antigravity", string.Empty, true, 1_048_576, 65_536, 0m, 0m)
            },
            ["amazon-bedrock"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic.claude-3-7-sonnet-20250219-v1:0"] = Create("anthropic.claude-3-7-sonnet-20250219-v1:0", "Claude 3.7 Sonnet (Bedrock)", "bedrock-converse-stream", "amazon-bedrock", string.Empty, true, 200_000, 8_192, 3.0m, 15.0m),
                ["us.anthropic.claude-opus-4-6-v1"] = Create("us.anthropic.claude-opus-4-6-v1", "Claude Opus 4.6 (Bedrock)", "bedrock-converse-stream", "amazon-bedrock", string.Empty, true, 200_000, 8_192, 15.0m, 75.0m),
                ["global.anthropic.claude-sonnet-4-5-20250929-v1:0"] = Create("global.anthropic.claude-sonnet-4-5-20250929-v1:0", "Claude Sonnet 4.5 (Bedrock)", "bedrock-converse-stream", "amazon-bedrock", string.Empty, true, 200_000, 8_192, 3.0m, 15.0m)
            }
        };

    private static Model Create(
        string id,
        string name,
        string api,
        string provider,
        string baseUrl,
        bool reasoning,
        int contextWindow,
        int maxTokens,
        decimal inputCost,
        decimal outputCost) =>
        new()
        {
            Id = id,
            Name = name,
            Api = api,
            Provider = provider,
            BaseUrl = baseUrl,
            Reasoning = reasoning,
            ContextWindow = contextWindow,
            MaxOutputTokens = maxTokens,
            Cost = new ModelCost(inputCost, outputCost, inputCost / 10m, inputCost)
        };
}
