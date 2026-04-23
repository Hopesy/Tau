using System.Text.Json;

namespace Tau.Ai.Auth;

public static class EnvironmentApiKeyResolver
{
    public const string AuthenticatedMarker = "<authenticated>";

    public static string? GetApiKey(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        provider = provider.Trim();

        if (provider.Equals("github-copilot", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(
                Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN"),
                Environment.GetEnvironmentVariable("GH_TOKEN"),
                Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        }

        if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(
                Environment.GetEnvironmentVariable("ANTHROPIC_OAUTH_TOKEN"),
                Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        }

        if (provider.Equals("google-vertex", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return apiKey;
            }

            if (HasVertexAdcCredentials() &&
                !string.IsNullOrWhiteSpace(FirstNonEmpty(
                    Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT"),
                    Environment.GetEnvironmentVariable("GCLOUD_PROJECT"))) &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION")))
            {
                return AuthenticatedMarker;
            }

            return null;
        }

        if (provider.Equals("amazon-bedrock", StringComparison.OrdinalIgnoreCase))
        {
            var hasAwsCreds =
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_PROFILE")) ||
                (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")) &&
                 !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"))) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_WEB_IDENTITY_TOKEN_FILE"));

            return hasAwsCreds ? AuthenticatedMarker : null;
        }

        return provider.ToLowerInvariant() switch
        {
            "openai" => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            "azure-openai-responses" => Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            "google" => FirstNonEmpty(
                Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
                Environment.GetEnvironmentVariable("GOOGLE_API_KEY")),
            "mistral" => Environment.GetEnvironmentVariable("MISTRAL_API_KEY"),
            "openrouter" => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
            "groq" => Environment.GetEnvironmentVariable("GROQ_API_KEY"),
            "cerebras" => Environment.GetEnvironmentVariable("CEREBRAS_API_KEY"),
            "xai" => Environment.GetEnvironmentVariable("XAI_API_KEY"),
            "zai" => Environment.GetEnvironmentVariable("ZAI_API_KEY"),
            "vercel-ai-gateway" => Environment.GetEnvironmentVariable("AI_GATEWAY_API_KEY"),
            "minimax" => Environment.GetEnvironmentVariable("MINIMAX_API_KEY"),
            "minimax-cn" => Environment.GetEnvironmentVariable("MINIMAX_CN_API_KEY"),
            "huggingface" => Environment.GetEnvironmentVariable("HF_TOKEN"),
            "opencode" => Environment.GetEnvironmentVariable("OPENCODE_API_KEY"),
            "opencode-go" => Environment.GetEnvironmentVariable("OPENCODE_API_KEY"),
            "kimi-coding" => Environment.GetEnvironmentVariable("KIMI_API_KEY"),
            _ => null
        };
    }

    public static bool IsAuthenticatedMarker(string? value) =>
        string.Equals(value, AuthenticatedMarker, StringComparison.Ordinal);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool HasVertexAdcCredentials()
    {
        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return true;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return false;
        }

        var adcPath = Path.Combine(home, ".config", "gcloud", "application_default_credentials.json");
        return File.Exists(adcPath);
    }
}
