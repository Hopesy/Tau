// 作者：xxx
namespace Tau.Ai.Providers.OpenAi;

/// <summary>【AI】【思考协议】统一两种 Completions 实现的启用、关闭和等级映射行为。</summary>
internal static class OpenAiReasoning
{
    /// <summary>【AI】【思考字段】根据兼容格式生成供应商所需字段，并复用模板与预算规则。</summary>
    /// <param name="model">模型与思考格式。</param><param name="options">原生或简化生成选项。</param>
    /// <param name="body">待发送的请求体。</param>
    internal static void Apply(Model model, StreamOptions options, Dictionary<string, object> body)
    {
        if (!model.Reasoning) return;
        model = model with { Compat = OpenAiCompatibility.Resolve(model) };
        if (OpenAiThinkingTemplates.Apply(model, options, body)) return;
        var requested = OpenAiThinkingTemplates.RequestedEffort(model, options);
        var enabled = requested is not null;
        var mapped = OpenAiThinkingTemplates.MappedEffort(model, requested);
        var effort = mapped ?? requested;
        var supportsEffort = model.Compat?.SupportsReasoningEffort == true;
        var format = model.Compat?.ThinkingFormat?.Trim().ToLowerInvariant();
        // 1. 【AI】【协议区分】ZAI 使用 thinking 对象，Qwen 使用 enable_thinking，路由服务使用嵌套 reasoning
        switch (format)
        {
            case "zai":
                var thinking = new Dictionary<string, object> { ["type"] = enabled ? "enabled" : "disabled" };
                if (enabled) thinking["clear_thinking"] = false;
                body["thinking"] = thinking;
                if (enabled && supportsEffort && mapped is not null) body["reasoning_effort"] = mapped;
                break;
            case "qwen":
                body["enable_thinking"] = enabled;
                if (enabled && supportsEffort && effort is not null) body["reasoning_effort"] = effort;
                break;
            case "qwen-chat-template":
                body["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = enabled, ["preserve_thinking"] = true };
                break;
            case "deepseek":
                if (enabled || !HasNullMapping(model, "off"))
                    body["thinking"] = new Dictionary<string, object> { ["type"] = enabled ? "enabled" : "disabled" };
                if (enabled && supportsEffort && effort is not null) body["reasoning_effort"] = effort;
                break;
            case "openrouter":
                if (enabled || !HasNullMapping(model, "off"))
                    body["reasoning"] = new Dictionary<string, object> { ["effort"] = effort ?? "none" };
                break;
            case "ant-ling":
                if (enabled && TryGetMapping(model, requested!, out var antEffort) && antEffort is not null)
                    body["reasoning"] = new Dictionary<string, object> { ["effort"] = antEffort };
                break;
            case "together":
                body["reasoning"] = new Dictionary<string, object> { ["enabled"] = enabled };
                if (enabled && supportsEffort && effort is not null) body["reasoning_effort"] = effort;
                break;
            case "string-thinking":
                if (enabled || !HasNullMapping(model, "off")) body["thinking"] = effort ?? "none";
                break;
            default:
                // 2. 【AI】【等级关闭】普通格式仅在声明支持时发送 effort；关闭时需要模型提供字符串映射
                if (supportsEffort && effort is not null) body["reasoning_effort"] = effort;
                break;
        }
    }

    /// <summary>【AI】【空映射】显式 null 表示禁止发送关闭值，区别于未配置映射。</summary>
    /// <param name="model">模型映射。</param><param name="key">要检查的等级。</param><returns>是否显式设置为空。</returns>
    private static bool HasNullMapping(Model model, string key) => TryGetMapping(model, key, out var value) && value is null;

    /// <summary>【AI】【映射读取】优先读取原生 thinkingLevelMap，再兼容旧版 effort 映射。</summary>
    /// <param name="model">模型元数据。</param><param name="key">等级。</param><param name="value">映射值。</param>
    /// <returns>是否声明该等级。</returns>
    private static bool TryGetMapping(Model model, string key, out string? value)
    {
        if (model.ThinkingLevelMap?.TryGetValue(key, out value) == true) return true;
        if (model.Compat?.ReasoningEffortMap?.TryGetValue(key, out value) == true) return true;
        value = null;
        return false;
    }
}
