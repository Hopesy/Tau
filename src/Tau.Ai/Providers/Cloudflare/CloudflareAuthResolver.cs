using Tau.Ai.Auth;

namespace Tau.Ai.Providers.Cloudflare;

internal static class CloudflareAuthResolver
{
    internal const string ApiKeyEnvironmentVariable = "CLOUDFLARE_API_KEY";
    internal const string AccountIdEnvironmentVariable = "CLOUDFLARE_ACCOUNT_ID";
    internal const string GatewayIdEnvironmentVariable = "CLOUDFLARE_GATEWAY_ID";

    internal const string WorkersAiBaseUrl =
        "https://api.cloudflare.com/client/v4/accounts/{CLOUDFLARE_ACCOUNT_ID}/ai/v1";

    internal const string AiGatewayCompatBaseUrl =
        "https://gateway.ai.cloudflare.com/v1/{CLOUDFLARE_ACCOUNT_ID}/{CLOUDFLARE_GATEWAY_ID}/compat";

    internal const string AiGatewayOpenAiBaseUrl =
        "https://gateway.ai.cloudflare.com/v1/{CLOUDFLARE_ACCOUNT_ID}/{CLOUDFLARE_GATEWAY_ID}/openai";

    internal const string AiGatewayAnthropicBaseUrl =
        "https://gateway.ai.cloudflare.com/v1/{CLOUDFLARE_ACCOUNT_ID}/{CLOUDFLARE_GATEWAY_ID}/anthropic";

    private const string WorkersAiProvider = "cloudflare-workers-ai";
    private const string AiGatewayProvider = "cloudflare-ai-gateway";

    public static StreamOptions Resolve(
        Model model,
        StreamOptions options,
        ProviderAuthResolver authResolver,
        IReadOnlyDictionary<string, string>? env,
        string? explicitApiKey,
        string? resolvedApiKey,
        out Model requestModel)
    {
        requestModel = model;
        if (!IsCloudflareProvider(model.Provider))
        {
            return options;
        }

        var hasExplicitApiKey = !string.IsNullOrWhiteSpace(explicitApiKey);
        var stored = hasExplicitApiKey
            ? null
            : authResolver.GetStoredAuthEntry(model.Provider);
        var hasStoredApiKey = !string.IsNullOrWhiteSpace(stored?.ApiKey);
        var credentialEnv = hasExplicitApiKey
            ? env
            : hasStoredApiKey
                ? ProviderEnvironment.Merge(stored!.Env, env)
                : null;
        var useAmbientEnv = !hasExplicitApiKey && !hasStoredApiKey;
        var valueEnv = credentialEnv ?? env;
        var accountId = ResolveValue(AccountIdEnvironmentVariable, valueEnv, useAmbientEnv);
        var gatewayId = IsAiGateway(model.Provider)
            ? ResolveValue(GatewayIdEnvironmentVariable, valueEnv, useAmbientEnv)
            : null;

        if (string.IsNullOrWhiteSpace(resolvedApiKey) ||
            EnvironmentApiKeyResolver.IsAuthenticatedMarker(resolvedApiKey))
        {
            throw new ProviderAuthException(
                "auth",
                $"Cloudflare API key is required for {model.Provider}. Set {ApiKeyEnvironmentVariable} or an auth.json api_key entry.");
        }

        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ProviderAuthException(
                "auth",
                $"Cloudflare account ID is required for {model.Provider}. Set {AccountIdEnvironmentVariable}.");
        }

        if (IsAiGateway(model.Provider) && string.IsNullOrWhiteSpace(gatewayId))
        {
            throw new ProviderAuthException(
                "auth",
                $"Cloudflare AI Gateway ID is required for {model.Provider}. Set {GatewayIdEnvironmentVariable}.");
        }

        var resolvedEnv = CreateResolvedEnv(accountId, gatewayId);
        requestModel = model with
        {
            BaseUrl = ResolveBaseUrl(model.BaseUrl, accountId, gatewayId)
        };

        var requestEnv = ProviderEnvironment.Merge(env, resolvedEnv);
        if (!IsAiGateway(model.Provider))
        {
            return options with { Env = requestEnv };
        }

        var headers = options.Headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);
        headers["cf-aig-authorization"] = $"Bearer {resolvedApiKey}";
        headers["Authorization"] = string.Empty;
        headers["x-api-key"] = string.Empty;

        return options with
        {
            ApiKey = null,
            Headers = headers,
            Env = requestEnv
        };
    }

    private static bool IsCloudflareProvider(string provider) =>
        provider.Equals(WorkersAiProvider, StringComparison.OrdinalIgnoreCase) ||
        provider.Equals(AiGatewayProvider, StringComparison.OrdinalIgnoreCase);

    private static bool IsAiGateway(string provider) =>
        provider.Equals(AiGatewayProvider, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> CreateResolvedEnv(string accountId, string? gatewayId)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AccountIdEnvironmentVariable] = accountId
        };
        if (!string.IsNullOrWhiteSpace(gatewayId))
        {
            env[GatewayIdEnvironmentVariable] = gatewayId;
        }

        return env;
    }

    private static string? ResolveValue(
        string name,
        IReadOnlyDictionary<string, string>? env,
        bool useAmbientEnv)
    {
        if (TryGetScopedValue(name, env, out var value))
        {
            return value;
        }

        return useAmbientEnv ? ProviderEnvironment.GetValue(name, env) : null;
    }

    private static bool TryGetScopedValue(
        string name,
        IReadOnlyDictionary<string, string>? env,
        out string? value)
    {
        value = null;
        if (env is null)
        {
            return false;
        }

        if (!env.TryGetValue(name, out value))
        {
            foreach (var (key, candidate) in env)
            {
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = null;
            return false;
        }

        return true;
    }

    private static string? ResolveBaseUrl(string? baseUrl, string accountId, string? gatewayId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        return baseUrl
            .Replace($"{{{AccountIdEnvironmentVariable}}}", accountId, StringComparison.Ordinal)
            .Replace($"{{{GatewayIdEnvironmentVariable}}}", gatewayId ?? string.Empty, StringComparison.Ordinal);
    }
}
