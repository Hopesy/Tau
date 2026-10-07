// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Registry;

public sealed partial class ModelConfigurationStore
{
    /// <summary>【AI】【兼容联合校验】按上游三个兼容对象的联合规则匹配，未知字段不被误判为另一个协议的无效字段。</summary>
    /// <param name="value">兼容对象。</param><param name="path">字段路径。</param>
    private static void ValidateConfigCompatibility(JsonElement value, string path)
    {
        RequireConfigObject(value, path);
        var errors = new List<string>();
        foreach (var validate in new Action<JsonElement, string>[] { ValidateCompletionsCompatibility, ValidateResponsesCompatibility, ValidateAnthropicCompatibility })
        {
            try { validate(value, path); return; }
            catch (JsonException error) { errors.Add(error.Message); }
        }
        throw new JsonException(string.Join(" ", errors.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>【AI】【补全协议校验】验证 Chat Completions 的布尔选项、思考格式、模板参数和网关路由。</summary>
    /// <param name="value">兼容对象。</param><param name="path">字段路径。</param>
    private static void ValidateCompletionsCompatibility(JsonElement value, string path)
    {
        ValidateConfigBooleans(value, path, "supportsStore", "supportsDeveloperRole", "supportsReasoningEffort",
            "supportsUsageInStreaming", "supportsFinishReason", "requiresToolResultName", "requiresAssistantAfterToolResult",
            "requiresThinkingAsText", "requiresReasoningContentOnAssistantMessages", "supportsOpenAIGrammarTools",
            "supportsStrictMode", "sendSessionAffinityHeaders", "supportsLongCacheRetention");
        ValidateConfigField(value, "maxTokensField", path, (item, field) => RequireConfigEnum(item, field, "max_completion_tokens", "max_tokens"));
        ValidateConfigField(value, "thinkingFormat", path, (item, field) => RequireConfigEnum(item, field,
            "openai", "openrouter", "together", "baseten", "deepseek", "zai", "qwen", "chat-template", "qwen-chat-template", "string-thinking", "ant-ling"));
        ValidateConfigField(value, "cacheControlFormat", path, (item, field) => RequireConfigEnum(item, field, "anthropic"));
        ValidateConfigField(value, "sessionAffinityFormat", path, RequireConfigAffinity);
        ValidateConfigField(value, "vllmPriority", path, RequireConfigNumber);
        ValidateConfigField(value, "chatTemplateKwargs", path, ValidateConfigTemplate);
        ValidateConfigField(value, "chatTemplateArgs", path, ValidateConfigTemplate);
        ValidateConfigField(value, "openRouterRouting", path, ValidateConfigOpenRouter);
        ValidateConfigField(value, "vercelGatewayRouting", path, (item, field) =>
        {
            RequireConfigObject(item, field);
            foreach (var name in new[] { "only", "order" }) ValidateConfigField(item, name, field, RequireConfigStringArray);
        });
    }

    /// <summary>【AI】【响应协议校验】验证 Responses 的已声明字段，保留未知字段以匹配联合 schema。</summary>
    /// <param name="value">兼容对象。</param><param name="path">字段路径。</param>
    private static void ValidateResponsesCompatibility(JsonElement value, string path)
    {
        ValidateConfigBooleans(value, path, "supportsDeveloperRole", "supportsLongCacheRetention", "supportsStrictMode",
            "supportsOpenAIGrammarTools", "supportsMaxOutputTokens");
        ValidateConfigField(value, "sessionAffinityFormat", path, RequireConfigAffinity);
    }

    /// <summary>【AI】【Anthropic 校验】验证布尔开关和最多三个带完整费率的回退模型。</summary>
    /// <param name="value">兼容对象。</param><param name="path">字段路径。</param>
    private static void ValidateAnthropicCompatibility(JsonElement value, string path)
    {
        ValidateConfigBooleans(value, path, "supportsEagerToolInputStreaming", "supportsLongCacheRetention", "sendSessionAffinityHeaders",
            "supportsCacheControlOnTools", "supportsTemperature", "forceAdaptiveThinking", "allowEmptySignature", "supportsStrictTools", "supportsMidConvoEffort");
        ValidateConfigField(value, "allowedFallbackModels", path, (models, field) =>
        {
            if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 3) throw new JsonException(field + " must be an array with at most 3 items.");
            var index = 0;
            foreach (var model in models.EnumerateArray())
            {
                var modelPath = field + "." + index++;
                RequireConfigObject(model, modelPath);
                foreach (var name in new[] { "provider", "model", "cost" })
                    if (!model.TryGetProperty(name, out _)) throw new JsonException(modelPath + "." + name + " is required.");
                RequireConfigString(model.GetProperty("provider"), modelPath + ".provider");
                RequireConfigString(model.GetProperty("model"), modelPath + ".model");
                ValidateConfigCost(model.GetProperty("cost"), modelPath + ".cost", partial: false);
            }
        });
    }

    /// <summary>【AI】【模板参数校验】允许标量与思考变量，拒绝数组和缺少变量声明的对象。</summary>
    /// <param name="value">模板参数映射。</param><param name="path">字段路径。</param>
    private static void ValidateConfigTemplate(JsonElement value, string path)
    {
        RequireConfigObject(value, path);
        foreach (var item in value.EnumerateObject())
        {
            var field = path + "." + item.Name;
            if (item.Value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) continue;
            if (item.Value.ValueKind == JsonValueKind.Number) { RequireConfigNumber(item.Value, field); continue; }
            RequireConfigObject(item.Value, field);
            if (!item.Value.TryGetProperty("$var", out var variable)) throw new JsonException(field + ".$var is required.");
            RequireConfigEnum(variable, field + ".$var", "thinking.enabled", "thinking.effort");
            ValidateConfigField(item.Value, "omitWhenOff", field, RequireConfigBoolean);
        }
    }

    /// <summary>【AI】【路由校验】验证 OpenRouter 的路由数组、价格上限、排序与性能分位数。</summary>
    /// <param name="value">路由配置。</param><param name="path">字段路径。</param>
    private static void ValidateConfigOpenRouter(JsonElement value, string path)
    {
        RequireConfigObject(value, path);
        ValidateConfigBooleans(value, path, "allow_fallbacks", "require_parameters", "zdr", "enforce_distillable_text");
        ValidateConfigField(value, "data_collection", path, (item, field) => RequireConfigEnum(item, field, "deny", "allow"));
        foreach (var name in new[] { "order", "only", "ignore", "quantizations" }) ValidateConfigField(value, name, path, RequireConfigStringArray);
        ValidateConfigField(value, "sort", path, (item, field) =>
        {
            if (item.ValueKind == JsonValueKind.String) return;
            RequireConfigObject(item, field);
            ValidateConfigField(item, "by", field, RequireConfigText);
            ValidateConfigField(item, "partition", field, (partition, partitionPath) =>
            { if (partition.ValueKind != JsonValueKind.Null) RequireConfigText(partition, partitionPath); });
        });
        ValidateConfigField(value, "max_price", path, (item, field) =>
        {
            RequireConfigObject(item, field);
            foreach (var name in new[] { "prompt", "completion", "image", "audio", "request" })
                ValidateConfigField(item, name, field, (price, pricePath) =>
                { if (price.ValueKind != JsonValueKind.String) RequireConfigNumber(price, pricePath); });
        });
        foreach (var name in new[] { "preferred_min_throughput", "preferred_max_latency" })
            ValidateConfigField(value, name, path, (item, field) =>
            {
                if (item.ValueKind == JsonValueKind.Number) { RequireConfigNumber(item, field); return; }
                RequireConfigObject(item, field);
                foreach (var percentile in new[] { "p50", "p75", "p90", "p99" }) ValidateConfigField(item, percentile, field, RequireConfigNumber);
            });
    }

    /// <summary>【AI】【布尔字段组】校验一组可选开关。</summary>
    /// <param name="value">父对象。</param><param name="path">父路径。</param><param name="names">字段名称。</param>
    private static void ValidateConfigBooleans(JsonElement value, string path, params string[] names)
    {
        foreach (var name in names) ValidateConfigField(value, name, path, RequireConfigBoolean);
    }

    /// <summary>【AI】【亲和格式】限制为上游支持的会话亲和头格式。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param>
    private static void RequireConfigAffinity(JsonElement value, string path) => RequireConfigEnum(value, path, "openai", "openai-nosession", "openrouter");

    /// <summary>【AI】【枚举校验】要求精确匹配允许值，诊断不回显配置值。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param><param name="allowed">允许字符串。</param>
    private static void RequireConfigEnum(JsonElement value, string path, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.String || !allowed.Contains(value.GetString(), StringComparer.Ordinal))
            throw new JsonException(path + " must be one of the supported values.");
    }

    /// <summary>【AI】【文本类型】允许空文本但禁止其他 JSON 类型。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param>
    private static void RequireConfigText(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String) throw new JsonException(path + " must be a string.");
    }

    /// <summary>【AI】【文本数组】路由列表允许为空，但每项必须为字符串。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param>
    private static void RequireConfigStringArray(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new JsonException(path + " must be an array of strings.");
    }
}
