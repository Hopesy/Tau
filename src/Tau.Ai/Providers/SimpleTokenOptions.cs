// 作者：xxx
namespace Tau.Ai.Providers;

/// <summary>【AI】【生成预算】共享简化入口的上下文留量与思考预算计算。</summary>
internal static class SimpleTokenOptions
{
    internal const int ContextSafetyTokens = 4_096;
    internal const int MinimumAnswerTokens = 1_024;

    /// <summary>【AI】【简化入口】列出主线通过 buildBaseOptions 分配上下文预算的协议。</summary>
    /// <param name="api">标准化协议名。</param><returns>是否使用共享上下文裁剪。</returns>
    internal static bool UsesContextLimit(string api) => api is "openai-completions" or "openai-chat-completions" or
        "openai-responses" or "openai-codex-responses" or "azure-openai-responses" or "anthropic-messages" or
        "mistral-conversations" or "google-generative-language" or "google-vertex" or "bedrock-converse-stream";

    /// <summary>【AI】【上下文预算】为简化请求填充默认输出上限，并在降级图片或折叠系统消息前裁剪。</summary>
    /// <param name="model">模型输出与上下文上限。</param><param name="context">原始上下文。</param>
    /// <param name="options">调用方选项，不会被修改。</param><returns>保留派生类型的裁剪副本；未知输出上限时保留原值。</returns>
    internal static SimpleStreamOptions WithContextLimit(Model model, LlmContext context, SimpleStreamOptions options)
    {
        if ((options.MaxTokens ?? model.MaxOutputTokens) is not { } requested) return options;
        var limit = ClampMaxTokensToContext(model, Transcript.NormalizeContext(context), requested);
        return options.MaxTokens == limit ? options : options with { MaxTokens = limit };
    }

    /// <summary>按上下文估值预留安全空间，上下文耗尽时保留一个输出 token。</summary>
    /// <param name="model">包含上下文窗口的模型，缺省或非正窗口表示不限。</param>
    /// <param name="context">已经规范化、尚未按供应商能力折叠的会话。</param>
    /// <param name="maxTokens">请求的输出上限。</param>
    /// <returns>按上游规则限制后的输出上限。</returns>
    internal static int ClampMaxTokensToContext(Model model, LlmContext context, int maxTokens)
    {
        if (model.ContextWindow is null or <= 0) return Math.Max(1, maxTokens);
        var available = model.ContextWindow.Value - TokenEstimation.EstimateContextTokens(context).Tokens - ContextSafetyTokens;
        return (int)Math.Min(maxTokens, Math.Max(1, available));
    }

    /// <summary>给显式回答预算追加思考空间，未指定回答预算时在模型上限内分配。</summary>
    /// <param name="baseMaxTokens">回答预算，未指定时为空。</param>
    /// <param name="modelMaxTokens">模型输出上限。</param>
    /// <param name="thinkingBudget">原始思考预算。</param>
    /// <returns>输出总上限与思考预算。</returns>
    internal static (int MaxTokens, int ThinkingBudget) AdjustMaxTokensForThinking(int? baseMaxTokens, int modelMaxTokens, int thinkingBudget)
    {
        var maxTokens = baseMaxTokens is null ? modelMaxTokens :
            (int)Math.Clamp((long)baseMaxTokens.Value + thinkingBudget, int.MinValue, modelMaxTokens);
        if (maxTokens <= thinkingBudget) thinkingBudget = Math.Min(thinkingBudget, Math.Max(0, maxTokens - MinimumAnswerTokens));
        return (maxTokens, thinkingBudget);
    }

    /// <summary>【AI】【Anthropic 预算】先限制基础输出，再追加思考并复核上下文余量。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">原始或规范化会话。</param>
    /// <param name="maxTokens">可选输出上限。</param>
    /// <param name="thinkingBudget">启用固定预算思考时的预算，关闭或自适应时为空。</param>
    /// <returns>请求输出上限和可选思考预算。</returns>
    internal static (int MaxTokens, int? ThinkingBudget) ResolveAnthropic(Model model, LlmContext context, int? maxTokens, int? thinkingBudget)
    {
        // 1. 【AI】【Anthropic 预算】将旧字段纳入估算，保持所有系统增量和原始图片成本
        context = Transcript.NormalizeContext(context);
        var modelMaxTokens = model.MaxOutputTokens ?? 4_096;
        var baseMaxTokens = ClampMaxTokensToContext(model, context, maxTokens ?? modelMaxTokens);
        if (thinkingBudget is null) return (baseMaxTokens, null);

        // 2. 【AI】【Anthropic 预算】追加思考后再次裁剪，为回答预留最多 1024 token
        var adjusted = AdjustMaxTokensForThinking(baseMaxTokens, modelMaxTokens, thinkingBudget.Value);
        var finalMaxTokens = ClampMaxTokensToContext(model, context, adjusted.MaxTokens);
        return (finalMaxTokens, Math.Min(adjusted.ThinkingBudget, Math.Max(0, finalMaxTokens - MinimumAnswerTokens)));
    }
}
