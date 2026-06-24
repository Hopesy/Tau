namespace Tau.Ai.Auth;

public static class EnvironmentApiKeyResolver
{
    public const string AuthenticatedMarker = "<authenticated>";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ApiKeyEnvironmentVariables =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ant-ling"] = ["ANT_LING_API_KEY"],
            ["openai"] = ["OPENAI_API_KEY"],
            ["azure-openai-responses"] = ["AZURE_OPENAI_API_KEY"],
            ["nvidia"] = ["NVIDIA_API_KEY"],
            ["deepseek"] = ["DEEPSEEK_API_KEY"],
            ["google"] = ["GEMINI_API_KEY"],
            ["google-vertex"] = ["GOOGLE_CLOUD_API_KEY"],
            ["groq"] = ["GROQ_API_KEY"],
            ["cerebras"] = ["CEREBRAS_API_KEY"],
            ["xai"] = ["XAI_API_KEY"],
            ["openrouter"] = ["OPENROUTER_API_KEY"],
            ["vercel-ai-gateway"] = ["AI_GATEWAY_API_KEY"],
            ["zai"] = ["ZAI_API_KEY"],
            ["zai-coding-cn"] = ["ZAI_CODING_CN_API_KEY"],
            ["mistral"] = ["MISTRAL_API_KEY"],
            ["minimax"] = ["MINIMAX_API_KEY"],
            ["minimax-cn"] = ["MINIMAX_CN_API_KEY"],
            ["moonshotai"] = ["MOONSHOT_API_KEY"],
            ["moonshotai-cn"] = ["MOONSHOT_API_KEY"],
            ["huggingface"] = ["HF_TOKEN"],
            ["fireworks"] = ["FIREWORKS_API_KEY"],
            ["together"] = ["TOGETHER_API_KEY"],
            ["opencode"] = ["OPENCODE_API_KEY"],
            ["opencode-go"] = ["OPENCODE_API_KEY"],
            ["kimi-coding"] = ["KIMI_API_KEY"],
            ["cloudflare-workers-ai"] = ["CLOUDFLARE_API_KEY"],
            ["cloudflare-ai-gateway"] = ["CLOUDFLARE_API_KEY"],
            ["xiaomi"] = ["XIAOMI_API_KEY"],
            ["xiaomi-token-plan-cn"] = ["XIAOMI_TOKEN_PLAN_CN_API_KEY"],
            ["xiaomi-token-plan-ams"] = ["XIAOMI_TOKEN_PLAN_AMS_API_KEY"],
            ["xiaomi-token-plan-sgp"] = ["XIAOMI_TOKEN_PLAN_SGP_API_KEY"],
            ["github-copilot"] = ["COPILOT_GITHUB_TOKEN"],
            ["anthropic"] = ["ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY"]
        };

    public static IReadOnlyList<string> FindEnvKeys(
        string provider,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return [];
        }

        provider = provider.Trim();
        if (!ApiKeyEnvironmentVariables.TryGetValue(provider, out var envVars))
        {
            return [];
        }

        var found = new List<string>();
        foreach (var envVar in envVars)
        {
            if (!string.IsNullOrWhiteSpace(ProviderEnvironment.GetValue(envVar, env)))
            {
                found.Add(envVar);
            }
        }

        return found;
    }

    public static string? GetApiKey(
        string provider,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        provider = provider.Trim();

        var envKeys = FindEnvKeys(provider, env);
        if (envKeys.Count > 0)
        {
            return ProviderEnvironment.GetValue(envKeys[0], env);
        }

        if (provider.Equals("google-vertex", StringComparison.OrdinalIgnoreCase))
        {
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

        return null;
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
