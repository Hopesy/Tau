// 作者：xxx
namespace Tau.Ai.Providers.OpenAi;

/// <summary>【AI】【兼容检测】按 pi 主线的提供方与地址规则生成 Completions 默认能力。</summary>
internal static class OpenAiCompatibility
{
    /// <summary>【AI】【能力合并】自动检测默认能力，显式 compat 字段始终优先。</summary>
    /// <param name="model">提供方、地址、模型标识和显式覆盖。</param><returns>不会修改输入模型的已补足能力副本。</returns>
    internal static ModelCompatibility Resolve(Model model)
    {
        var provider = model.Provider;
        var url = model.BaseUrl ?? "";
        var zai = provider is "zai" or "zai-coding-cn" || url.Contains("api.z.ai", StringComparison.Ordinal) || url.Contains("open.bigmodel.cn", StringComparison.Ordinal);
        var together = provider == "together" || url.Contains("api.together.ai", StringComparison.Ordinal) || url.Contains("api.together.xyz", StringComparison.Ordinal);
        var moonshot = provider is "moonshotai" or "moonshotai-cn" || url.Contains("api.moonshot.", StringComparison.Ordinal);
        var openRouter = provider == "openrouter" || url.Contains("openrouter.ai", StringComparison.Ordinal);
        var workers = provider == "cloudflare-workers-ai" || url.Contains("api.cloudflare.com", StringComparison.Ordinal);
        var gateway = provider == "cloudflare-ai-gateway" || url.Contains("gateway.ai.cloudflare.com", StringComparison.Ordinal);
        var nvidia = provider == "nvidia" || url.Contains("integrate.api.nvidia.com", StringComparison.Ordinal);
        var ant = provider == "ant-ling" || url.Contains("api.ant-ling.com", StringComparison.Ordinal);
        var cerebras = provider == "cerebras" || url.Contains("cerebras.ai", StringComparison.Ordinal);
        var deepSeek = provider == "deepseek" || url.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase);
        var grok = provider == "xai" || url.Contains("api.x.ai", StringComparison.Ordinal);
        var chutes = url.Contains("chutes.ai", StringComparison.Ordinal);
        var nonStandard = nvidia || cerebras || grok || together || chutes || deepSeek || zai || moonshot ||
            provider == "opencode" || url.Contains("opencode.ai", StringComparison.Ordinal) || workers || gateway || ant;
        var maxTokens = chutes || deepSeek || moonshot || gateway || together || nvidia || ant || zai;
        var routerDeveloper = openRouter && (model.Id.StartsWith("anthropic/", StringComparison.Ordinal) || model.Id.StartsWith("openai/", StringComparison.Ordinal));
        var original = model.Compat ?? new ModelCompatibility();
        // 1. 【AI】【默认能力】URL 检测覆盖自定义提供方名称，未配置项继承检测值
        return original with
        {
            SupportsStore = original.SupportsStore ?? !nonStandard,
            SupportsDeveloperRole = original.SupportsDeveloperRole ?? (routerDeveloper || (!nonStandard && !openRouter)),
            SupportsReasoningEffort = original.SupportsReasoningEffort ?? !(grok || zai || moonshot || together || gateway || nvidia || ant),
            SupportsUsageInStreaming = original.SupportsUsageInStreaming ?? true,
            SupportsFinishReason = original.SupportsFinishReason ?? true,
            MaxTokensField = original.MaxTokensField ?? (maxTokens ? "max_tokens" : "max_completion_tokens"),
            RequiresToolResultName = original.RequiresToolResultName ?? false,
            RequiresAssistantAfterToolResult = original.RequiresAssistantAfterToolResult ?? false,
            RequiresThinkingAsText = original.RequiresThinkingAsText ?? false,
            RequiresReasoningContentOnAssistantMessages = original.RequiresReasoningContentOnAssistantMessages ?? deepSeek,
            ThinkingFormat = original.ThinkingFormat ?? (deepSeek ? "deepseek" : zai ? "zai" : together ? "together" : ant ? "ant-ling" : openRouter ? "openrouter" : "openai"),
            ZaiToolStream = original.ZaiToolStream ?? false,
            SupportsThinkingTokenBudget = original.SupportsThinkingTokenBudget ?? false,
            SupportsStrictMode = original.SupportsStrictMode ?? false,
            SupportsOpenAiGrammarTools = original.SupportsOpenAiGrammarTools ?? false,
            SupportsMidConvoSystemMessages = original.SupportsMidConvoSystemMessages ?? false,
            SupportsMidConvoToolAdditions = original.SupportsMidConvoToolAdditions ?? false,
            CacheControlFormat = original.CacheControlFormat ?? (provider == "openrouter" && model.Id.StartsWith("anthropic/", StringComparison.Ordinal) ? "anthropic" : null),
            SendSessionAffinityHeaders = original.SendSessionAffinityHeaders ?? openRouter,
            SessionAffinityFormat = original.SessionAffinityFormat ?? (openRouter ? "openrouter" : "openai"),
            SupportsLongCacheRetention = original.SupportsLongCacheRetention ?? !(together || workers || gateway || nvidia || ant),
            SupportsTemperature = original.SupportsTemperature ?? true
        };
    }
}
