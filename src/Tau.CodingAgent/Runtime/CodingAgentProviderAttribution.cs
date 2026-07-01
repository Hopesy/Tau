using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// 【CodingAgent】【Provider归因】对齐上游 <c>core/provider-attribution.ts</c>，为发往模型提供方的请求合并产品归因请求头
/// 和 opencode 会话亲和请求头。归因请求头是最低优先级基础层，显式认证/请求头可以覆盖它，合并顺序与上游保持一致。
/// </summary>
public static class CodingAgentProviderAttribution
{
    private const string OpenRouterHost = "openrouter.ai";
    private const string NvidiaNimHost = "integrate.api.nvidia.com";
    private const string CloudflareApiHost = "api.cloudflare.com";
    private const string CloudflareAiGatewayHost = "gateway.ai.cloudflare.com";
    private const string OpenCodeHost = "opencode.ai";
    private const string VercelGatewayHost = "ai-gateway.vercel.sh";

    /// <summary>
    /// 为一次模型请求构造合并后的归因请求头和会话请求头集合；没有任何适用请求头时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="model">当前请求使用的模型配置，用于判断 provider/baseUrl 是否需要归因或会话请求头。</param>
    /// <param name="installTelemetryEnabled">是否启用安装遥测；禁用时不写入产品归因请求头。</param>
    /// <param name="sessionId">当前 CodingAgent 会话标识；仅 opencode 系列提供方会使用。</param>
    /// <param name="headerSources">调用方已有的认证/请求头集合，后出现的集合会覆盖前面的同名请求头。</param>
    /// <returns>合并后的请求头字典；如果没有任何请求头需要发送，则返回 <see langword="null"/>。</returns>
    public static IDictionary<string, string>? MergeAttributionHeaders(
        Model model,
        bool installTelemetryEnabled,
        string? sessionId,
        params IReadOnlyDictionary<string, string>?[] headerSources)
    {
        ArgumentNullException.ThrowIfNull(model);

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sessionHeaders = GetSessionHeaders(model, sessionId);
        if (sessionHeaders is not null)
        {
            foreach (var (key, value) in sessionHeaders)
            {
                merged[key] = value;
            }
        }

        var attributionHeaders = GetDefaultAttributionHeaders(model, installTelemetryEnabled);
        if (attributionHeaders is not null)
        {
            foreach (var (key, value) in attributionHeaders)
            {
                merged[key] = value;
            }
        }

        if (headerSources is not null)
        {
            foreach (var source in headerSources)
            {
                if (source is null)
                {
                    continue;
                }

                foreach (var (key, value) in source)
                {
                    merged[key] = value;
                }
            }
        }

        return merged.Count > 0 ? merged : null;
    }

    /// <summary>
    /// 根据模型提供方生成默认产品归因请求头。
    /// </summary>
    /// <param name="model">当前请求使用的模型配置。</param>
    /// <param name="installTelemetryEnabled">是否允许写入产品归因请求头。</param>
    /// <returns>需要附加的默认归因请求头；禁用遥测或不匹配提供方时返回 <see langword="null"/>。</returns>
    private static IReadOnlyDictionary<string, string>? GetDefaultAttributionHeaders(
        Model model,
        bool installTelemetryEnabled)
    {
        if (!installTelemetryEnabled)
        {
            return null;
        }

        if (IsOpenRouterModel(model))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HTTP-Referer"] = "https://pi.dev",
                ["X-OpenRouter-Title"] = "pi",
                ["X-OpenRouter-Categories"] = "cli-agent"
            };
        }

        if (IsNvidiaNimModel(model))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-BILLING-INVOKE-ORIGIN"] = "Pi"
            };
        }

        if (IsCloudflareModel(model))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = "pi-coding-agent"
            };
        }

        if (IsVercelGatewayModel(model))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["http-referer"] = "https://pi.dev",
                ["x-title"] = "pi"
            };
        }

        return null;
    }

    /// <summary>
    /// 根据模型提供方和当前会话生成 opencode 会话亲和请求头。
    /// </summary>
    /// <param name="model">当前请求使用的模型配置。</param>
    /// <param name="sessionId">当前 CodingAgent 会话标识。</param>
    /// <returns>opencode 会话请求头；没有会话或不匹配 opencode 提供方时返回 <see langword="null"/>。</returns>
    private static IReadOnlyDictionary<string, string>? GetSessionHeaders(Model model, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        if (!string.Equals(model.Provider, "opencode", StringComparison.Ordinal) &&
            !string.Equals(model.Provider, "opencode-go", StringComparison.Ordinal) &&
            !MatchesHost(model.BaseUrl, OpenCodeHost))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-opencode-session"] = sessionId,
            ["x-opencode-client"] = "pi"
        };
    }

    /// <summary>
    /// 判断模型是否属于 OpenRouter 或指向 OpenRouter 主机。
    /// </summary>
    /// <param name="model">需要检查的模型配置。</param>
    /// <returns>属于 OpenRouter 时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    private static bool IsOpenRouterModel(Model model) =>
        string.Equals(model.Provider, "openrouter", StringComparison.Ordinal) ||
        (model.BaseUrl?.Contains(OpenRouterHost, StringComparison.Ordinal) ?? false);

    /// <summary>
    /// 判断模型是否属于 NVIDIA NIM 或指向 NVIDIA NIM 主机。
    /// </summary>
    /// <param name="model">需要检查的模型配置。</param>
    /// <returns>属于 NVIDIA NIM 时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    private static bool IsNvidiaNimModel(Model model) =>
        string.Equals(model.Provider, "nvidia", StringComparison.Ordinal) ||
        MatchesHost(model.BaseUrl, NvidiaNimHost);

    /// <summary>
    /// 判断模型是否属于 Cloudflare Workers AI、Cloudflare AI Gateway 或对应主机。
    /// </summary>
    /// <param name="model">需要检查的模型配置。</param>
    /// <returns>属于 Cloudflare 系列提供方时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    private static bool IsCloudflareModel(Model model) =>
        string.Equals(model.Provider, "cloudflare-workers-ai", StringComparison.Ordinal) ||
        string.Equals(model.Provider, "cloudflare-ai-gateway", StringComparison.Ordinal) ||
        MatchesHost(model.BaseUrl, CloudflareApiHost) ||
        MatchesHost(model.BaseUrl, CloudflareAiGatewayHost);

    /// <summary>
    /// 判断模型是否属于 Vercel AI Gateway 或指向 Vercel Gateway 主机。
    /// </summary>
    /// <param name="model">需要检查的模型配置。</param>
    /// <returns>属于 Vercel AI Gateway 时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    private static bool IsVercelGatewayModel(Model model) =>
        string.Equals(model.Provider, "vercel-ai-gateway", StringComparison.Ordinal) ||
        MatchesHost(model.BaseUrl, VercelGatewayHost);

    /// <summary>
    /// 判断 baseUrl 的主机名是否精确匹配指定主机。
    /// </summary>
    /// <param name="baseUrl">待解析的模型基础地址。</param>
    /// <param name="expectedHost">期望匹配的主机名。</param>
    /// <returns>地址可解析且主机名一致时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    private static bool MatchesHost(string? baseUrl, string expectedHost)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, expectedHost, StringComparison.Ordinal);
    }
}
