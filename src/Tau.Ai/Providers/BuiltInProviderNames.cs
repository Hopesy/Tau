// 作者：xxx
using System.Collections.Frozen;

namespace Tau.Ai.Providers;

/// <summary>【AI】【提供方名称】来自主线 packages/ai/src/providers 的内置提供方显示名称。</summary>
public static class BuiltInProviderNames
{
    /// <summary>供 SDK、宿主界面和扩展快照共同读取的不可变名称表。</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["amazon-bedrock"] = "Amazon Bedrock",
        ["ant-ling"] = "Ant Ling",
        ["anthropic"] = "Anthropic",
        ["azure-openai-responses"] = "Azure OpenAI",
        ["baseten"] = "Baseten",
        ["cerebras"] = "Cerebras",
        ["cloudflare-ai-gateway"] = "Cloudflare AI Gateway",
        ["cloudflare-workers-ai"] = "Cloudflare Workers AI",
        ["deepseek"] = "DeepSeek",
        ["fireworks"] = "Fireworks",
        ["github-copilot"] = "GitHub Copilot",
        ["google"] = "Google",
        ["google-vertex"] = "Google Vertex AI",
        ["groq"] = "Groq",
        ["huggingface"] = "Hugging Face",
        ["kimi-coding"] = "Kimi For Coding",
        ["meta"] = "Meta",
        ["minimax"] = "MiniMax",
        ["minimax-cn"] = "MiniMax CN",
        ["mistral"] = "Mistral",
        ["moonshotai"] = "Moonshot AI",
        ["moonshotai-cn"] = "Moonshot AI CN",
        ["nvidia"] = "NVIDIA",
        ["openai"] = "OpenAI",
        ["openai-codex"] = "OpenAI Codex (legacy)",
        ["opencode"] = "OpenCode Zen",
        ["opencode-go"] = "OpenCode Go",
        ["openrouter"] = "OpenRouter",
        ["qwen-token-plan"] = "Qwen Token Plan",
        ["qwen-token-plan-cn"] = "Qwen Token Plan CN",
        ["qwen-token-plan-individual"] = "Qwen Token Plan Individual",
        ["radius"] = "Radius",
        ["together"] = "Together",
        ["typesafe"] = "TypeSafe",
        ["vercel-ai-gateway"] = "Vercel AI Gateway",
        ["xai"] = "xAI",
        ["xiaomi"] = "Xiaomi",
        ["xiaomi-token-plan-ams"] = "Xiaomi Token Plan AMS",
        ["xiaomi-token-plan-cn"] = "Xiaomi Token Plan CN",
        ["xiaomi-token-plan-sgp"] = "Xiaomi Token Plan SGP",
        ["zai"] = "Z.AI",
        ["zai-coding-cn"] = "Z.AI Coding CN"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>【AI】【提供方名称】按主线精确标识查询内置显示名。</summary>
    /// <param name="providerId">提供方标识。</param><returns>内置名称，未知标识返回空值。</returns>
    public static string? TryGet(string providerId) => string.IsNullOrEmpty(providerId) ? null : All.GetValueOrDefault(providerId);
}
