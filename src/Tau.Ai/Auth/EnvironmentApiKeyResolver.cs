using System.Text.Json;

namespace Tau.Ai.Auth;

public static class EnvironmentApiKeyResolver
{
    public const string AuthenticatedMarker = "<authenticated>";

    public static string? GetApiKey(
        string provider,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        provider = provider.Trim();

        if (provider.Equals("github-copilot", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(
                ProviderEnvironment.GetValue("COPILOT_GITHUB_TOKEN", env),
                ProviderEnvironment.GetValue("GH_TOKEN", env),
                ProviderEnvironment.GetValue("GITHUB_TOKEN", env));
        }

        if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(
                ProviderEnvironment.GetValue("ANTHROPIC_OAUTH_TOKEN", env),
                ProviderEnvironment.GetValue("ANTHROPIC_API_KEY", env));
        }

        if (provider.Equals("google-vertex", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = ProviderEnvironment.GetValue("GOOGLE_CLOUD_API_KEY", env);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return apiKey;
            }

            if (HasVertexAdcCredentials(env) &&
                !string.IsNullOrWhiteSpace(FirstNonEmpty(
                    ProviderEnvironment.GetValue("GOOGLE_CLOUD_PROJECT", env),
                    ProviderEnvironment.GetValue("GCLOUD_PROJECT", env))) &&
                !string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue("GOOGLE_CLOUD_LOCATION", env)))
            {
                return AuthenticatedMarker;
            }

            return null;
        }

        if (provider.Equals("amazon-bedrock", StringComparison.OrdinalIgnoreCase))
        {
            var hasAwsCreds =
                !string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue("AWS_PROFILE", env)) ||
                (!string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue("AWS_ACCESS_KEY_ID", env)) &&
                 !string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue("AWS_SECRET_ACCESS_KEY", env))) ||
                !string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue("AWS_BEARER_TOKEN_BEDROCK", env)) ||
                !string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", env)) ||
                !string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue("AWS_CONTAINER_CREDENTIALS_FULL_URI", env)) ||
                !string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue("AWS_WEB_IDENTITY_TOKEN_FILE", env));

            return hasAwsCreds ? AuthenticatedMarker : null;
        }

        return provider.ToLowerInvariant() switch
        {
            "openai" => ProviderEnvironment.GetValue("OPENAI_API_KEY", env),
            "azure-openai-responses" => ProviderEnvironment.GetValue("AZURE_OPENAI_API_KEY", env),
            "google" => FirstNonEmpty(
                ProviderEnvironment.GetValue("GEMINI_API_KEY", env),
                ProviderEnvironment.GetValue("GOOGLE_API_KEY", env)),
            "mistral" => ProviderEnvironment.GetValue("MISTRAL_API_KEY", env),
            "openrouter" => ProviderEnvironment.GetValue("OPENROUTER_API_KEY", env),
            "groq" => ProviderEnvironment.GetValue("GROQ_API_KEY", env),
            "cerebras" => ProviderEnvironment.GetValue("CEREBRAS_API_KEY", env),
            "xai" => ProviderEnvironment.GetValue("XAI_API_KEY", env),
            "zai" => ProviderEnvironment.GetValue("ZAI_API_KEY", env),
            "vercel-ai-gateway" => ProviderEnvironment.GetValue("AI_GATEWAY_API_KEY", env),
            "minimax" => ProviderEnvironment.GetValue("MINIMAX_API_KEY", env),
            "minimax-cn" => ProviderEnvironment.GetValue("MINIMAX_CN_API_KEY", env),
            "huggingface" => ProviderEnvironment.GetValue("HF_TOKEN", env),
            "opencode" => ProviderEnvironment.GetValue("OPENCODE_API_KEY", env),
            "opencode-go" => ProviderEnvironment.GetValue("OPENCODE_API_KEY", env),
            "kimi-coding" => ProviderEnvironment.GetValue("KIMI_API_KEY", env),
            _ => null
        };
    }

    public static bool IsAuthenticatedMarker(string? value) =>
        string.Equals(value, AuthenticatedMarker, StringComparison.Ordinal);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool HasVertexAdcCredentials(IReadOnlyDictionary<string, string>? env)
    {
        var envPath = ProviderEnvironment.GetValue("GOOGLE_APPLICATION_CREDENTIALS", env);
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return true;
        }

        var applicationData = FirstNonEmpty(
            ProviderEnvironment.GetValue("APPDATA", env),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            var adcPath = Path.Combine(applicationData, "gcloud", "application_default_credentials.json");
            if (File.Exists(adcPath))
            {
                return true;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var adcPath = Path.Combine(home, ".config", "gcloud", "application_default_credentials.json");
            if (File.Exists(adcPath))
            {
                return true;
            }
        }

        return false;
    }
}
