namespace Tau.CodingAgent.Runtime;

/// <summary>
/// 【CodingAgent】【Provider显示名】对齐上游 <c>core/provider-display-names.ts</c>，把内置 provider id 映射为人类可读显示名。
/// 认证选择器和 provider 选择器用它渲染列表标签。
/// </summary>
public static class CodingAgentProviderDisplayNames
{
    private static readonly IReadOnlyDictionary<string, string> BuiltInDisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["anthropic"] = "Anthropic",
            ["amazon-bedrock"] = "Amazon Bedrock",
            ["ant-ling"] = "Ant Ling",
            ["azure-openai-responses"] = "Azure OpenAI Responses",
            ["cerebras"] = "Cerebras",
            ["cloudflare-ai-gateway"] = "Cloudflare AI Gateway",
            ["cloudflare-workers-ai"] = "Cloudflare Workers AI",
            ["deepseek"] = "DeepSeek",
            ["fireworks"] = "Fireworks",
            ["google"] = "Google Gemini",
            ["google-vertex"] = "Google Vertex AI",
            ["groq"] = "Groq",
            ["huggingface"] = "Hugging Face",
            ["kimi-coding"] = "Kimi For Coding",
            ["mistral"] = "Mistral",
            ["minimax"] = "MiniMax",
            ["minimax-cn"] = "MiniMax (China)",
            ["moonshotai"] = "Moonshot AI",
            ["moonshotai-cn"] = "Moonshot AI (China)",
            ["nvidia"] = "NVIDIA NIM",
            ["opencode"] = "OpenCode Zen",
            ["opencode-go"] = "OpenCode Go",
            ["openai"] = "OpenAI",
            ["openrouter"] = "OpenRouter",
            ["together"] = "Together AI",
            ["vercel-ai-gateway"] = "Vercel AI Gateway",
            ["xai"] = "xAI",
            ["zai"] = "ZAI Coding Plan (Global)",
            ["zai-coding-cn"] = "ZAI Coding Plan (China)",
            ["xiaomi"] = "Xiaomi MiMo",
            ["xiaomi-token-plan-cn"] = "Xiaomi MiMo Token Plan (China)",
            ["xiaomi-token-plan-ams"] = "Xiaomi MiMo Token Plan (Amsterdam)",
            ["xiaomi-token-plan-sgp"] = "Xiaomi MiMo Token Plan (Singapore)"
        };

    /// <summary>
    /// 查询指定 provider id 的内置显示名。
    /// </summary>
    /// <param name="providerId">需要查找显示名的 provider id。</param>
    /// <returns>存在内置显示名时返回显示名；不存在或参数为空时返回 <see langword="null"/>。</returns>
    public static string? TryGetBuiltIn(string providerId) =>
        !string.IsNullOrEmpty(providerId) && BuiltInDisplayNames.TryGetValue(providerId, out var name)
            ? name
            : null;

    /// <summary>
    /// 解析 provider 的最终显示名，未命中内置映射时回退到 provider id 本身。
    /// </summary>
    /// <param name="providerId">需要解析显示名的 provider id。</param>
    /// <returns>用于界面展示的 provider 显示名。</returns>
    public static string Resolve(string providerId) =>
        TryGetBuiltIn(providerId) ?? providerId;

    /// <summary>
    /// 判断 provider id 是否存在内置显示名映射。
    /// </summary>
    /// <param name="providerId">需要检查的 provider id。</param>
    /// <returns>命中内置显示名表时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool IsBuiltInProvider(string providerId) =>
        !string.IsNullOrEmpty(providerId) && BuiltInDisplayNames.ContainsKey(providerId);
}
