// 作者：xxx
using System.Text.Json;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers.OpenAi;

/// <summary>【AI】【思考模板】共享 Completions 与兼容别名的模板变量和预算规则。</summary>
internal static class OpenAiThinkingTemplates
{
    /// <summary>【AI】【模板应用】写入思考预算，并在模板格式下组装对应请求参数。</summary>
    /// <param name="model">模型与兼容元数据。</param><param name="options">原生或简化请求选项。</param>
    /// <param name="body">尚未应用采样覆盖的请求体。</param><returns>是否已处理模板格式。</returns>
    internal static bool Apply(Model model, StreamOptions options, Dictionary<string, object> body)
    {
        if (!model.Reasoning) return false;
        var requested = RequestedEffort(model, options);
        var budget = ResolveBudget(model, options, requested);
        var budgetField = model.Compat?.ThinkingTokenBudgetField;
        if (string.IsNullOrEmpty(budgetField) && model.Compat?.SupportsThinkingTokenBudget == true)
            budgetField = "thinking_token_budget";
        // 1. 【AI】【回答空间】预算独立于思考格式，并至少给最终回答预留 1024 个 token
        if (!string.IsNullOrEmpty(budgetField) && budget.HasValue) body[budgetField] = budget.Value;
        var format = model.Compat?.ThinkingFormat?.Trim().ToLowerInvariant();
        if (format is not ("chat-template" or "baseten")) return false;
        var values = format == "baseten" ? model.Compat?.ChatTemplateArgs : model.Compat?.ChatTemplateKwargs;
        var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        if (values is not null)
            foreach (var (name, value) in values)
            {
                if (TryResolveValue(model, value, requested, budget, out var result)) resolved[name] = result!;
            }
        // 2. 【AI】【模板报文】空对象整体省略，Baseten 不擅自添加 enable_thinking
        if (resolved.Count > 0) body[format == "baseten" ? "chat_template_args" : "chat_template_kwargs"] = resolved;
        if (format == "baseten" && model.Compat?.SupportsReasoningEffort == true &&
            MappedEffort(model, requested) is { } effort) body["reasoning_effort"] = effort;
        return true;
    }

    /// <summary>【AI】【思考等级】原生入口保留指定等级，简化入口按模型能力收敛并识别关闭。</summary>
    /// <param name="model">目标模型。</param><param name="options">请求选项。</param><returns>启用等级或空值。</returns>
    internal static string? RequestedEffort(Model model, StreamOptions options)
    {
        if (options is OpenAiOptions { ReasoningEffort: { Length: > 0 } effort })
            return effort is "none" or "off" ? null : effort;
        if (options is not SimpleStreamOptions { Reasoning: { } level } || level == ThinkingLevel.Off) return null;
        var name = level switch
        {
            ThinkingLevel.Minimal => "minimal", ThinkingLevel.Low => "low", ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high", ThinkingLevel.ExtraHigh => "xhigh", ThinkingLevel.Max => "max", _ => "off"
        };
        // 1. 【AI】【旧映射兼容】旧版显式声明的高等级仍视为可用，原生映射存在时以其能力为准
        if (model.ThinkingLevelMap is null && model.Compat?.ReasoningEffortMap?.TryGetValue(name, out var legacy) == true && legacy is not null)
            return name;
        var clamped = ModelCatalog.ClampThinkingLevel(model, name);
        return clamped == "off" ? null : clamped;
    }

    /// <summary>【AI】【预算解析】读取自定义或默认预算，并限制在输出上限减去回答空间之内。</summary>
    /// <param name="model">提供默认上限的模型。</param><param name="options">请求上限与预算。</param>
    /// <param name="requested">启用的思考等级。</param><returns>正预算或空值。</returns>
    private static int? ResolveBudget(Model model, StreamOptions options, string? requested)
    {
        var level = requested switch
        {
            "minimal" => ThinkingLevel.Minimal, "low" => ThinkingLevel.Low, "medium" => ThinkingLevel.Medium,
            "high" or "xhigh" or "max" => ThinkingLevel.High, _ => (ThinkingLevel?)null
        };
        if (!level.HasValue) return null;
        var custom = options switch { OpenAiOptions native => native.ThinkingBudgets, SimpleStreamOptions simple => simple.ThinkingBudgets, _ => null };
        var amount = StreamOptionHelpers.GetThinkingBudget(custom, level.Value, 1024, 2048, 8192, 16384);
        if ((options.MaxTokens ?? model.MaxOutputTokens) is not { } limit) return null;
        var ceiling = (long)limit;
        var clamped = Math.Min(amount, Math.Max(0, ceiling - 1024));
        return clamped > 0 ? (int)clamped : null;
    }

    /// <summary>【AI】【等级映射】区分未配置映射和显式 null，关闭时读取 off 映射。</summary>
    /// <param name="model">模型映射。</param><param name="requested">启用等级或空值。</param><returns>可发送的字符串或空值。</returns>
    internal static string? MappedEffort(Model model, string? requested)
    {
        var key = requested ?? "off";
        if (model.ThinkingLevelMap?.TryGetValue(key, out var mapped) == true) return mapped;
        if (model.Compat?.ReasoningEffortMap?.TryGetValue(key, out var legacy) == true) return legacy;
        return requested;
    }

    /// <summary>【AI】【模板变量】保留字面标量，解析启用状态、等级和预算，并按关闭条件省略。</summary>
    /// <param name="model">模型映射。</param><param name="value">字面值或变量对象。</param>
    /// <param name="requested">启用等级。</param><param name="budget">限制后的预算。</param>
    /// <param name="resolved">可序列化的结果，允许字面 null。</param><returns>是否应写入该字段。</returns>
    private static bool TryResolveValue(Model model, object? value, string? requested, int? budget, out object? resolved)
    {
        resolved = value;
        string? variable;
        bool omitWhenOff;
        if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            variable = element.TryGetProperty("$var", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            omitWhenOff = element.TryGetProperty("omitWhenOff", out var omit) && omit.ValueKind == JsonValueKind.True;
        }
        else if (value is IDictionary<string, object> dictionary)
        {
            dictionary.TryGetValue("$var", out var name);
            variable = name as string;
            omitWhenOff = dictionary.TryGetValue("omitWhenOff", out var omit) && omit is true;
        }
        else return true;
        if (requested is null && omitWhenOff) return false;
        resolved = variable switch
        {
            "thinking.enabled" => requested is not null,
            "thinking.budget" => budget,
            _ => MappedEffort(model, requested)
        };
        return resolved is not null;
    }
}
