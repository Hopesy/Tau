// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Registry;

public sealed partial class ModelConfigurationStore
{
    /// <summary>【AI】【文件结构校验】先校验完整配置树，再发布任何目录或认证选项，未知扩展字段保持兼容。</summary>
    /// <param name="root">原始 models.json 根对象。</param>
    private static void ValidateFileConfiguration(JsonElement root)
    {
        RequireConfigObject(root, "root");
        if (!root.TryGetProperty("providers", out var providers)) throw new JsonException("providers is required.");
        RequireConfigObject(providers, "providers");
        foreach (var item in providers.EnumerateObject())
        {
            var path = "providers." + item.Name;
            var provider = item.Value;
            RequireConfigObject(provider, path);
            foreach (var name in new[] { "name", "baseUrl", "apiKey", "api" }) ValidateConfigField(provider, name, path, RequireConfigString);
            ValidateConfigField(provider, "authHeader", path, RequireConfigBoolean);
            ValidateConfigField(provider, "headers", path, RequireConfigHeaders);
            ValidateConfigField(provider, "compat", path, ValidateConfigCompatibility);
            ValidateConfigField(provider, "oauth", path, (value, field) =>
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() != "radius") throw new JsonException(field + " must be radius.");
            });
            if (provider.TryGetProperty("models", out var models))
            {
                if (models.ValueKind != JsonValueKind.Array) throw new JsonException(path + ".models must be an array.");
                var index = 0;
                foreach (var model in models.EnumerateArray()) ValidateFileModel(model, path + ".models." + index++, isOverride: false);
            }
            if (provider.TryGetProperty("modelOverrides", out var overrides))
            {
                RequireConfigObject(overrides, path + ".modelOverrides");
                foreach (var model in overrides.EnumerateObject()) ValidateFileModel(model.Value, path + ".modelOverrides." + model.Name, isOverride: true);
            }
            ValidateProviderMetadata(provider);
        }
    }

    /// <summary>【AI】【模型结构校验】验证模型及未匹配覆盖的基础字段，非聊天能力继续支持 Tau 扩展格式。</summary>
    /// <param name="model">模型或覆盖对象。</param><param name="path">诊断路径。</param><param name="isOverride">是否为局部覆盖。</param>
    private static void ValidateFileModel(JsonElement model, string path, bool isOverride)
    {
        RequireConfigObject(model, path);
        if (!isOverride && !model.TryGetProperty("id", out _)) throw new JsonException(path + ".id is required.");
        foreach (var name in new[] { "id", "name", "api", "baseUrl" }) ValidateConfigField(model, name, path, RequireConfigString);
        ValidateConfigField(model, "reasoning", path, RequireConfigBoolean);
        ValidateConfigField(model, "headers", path, RequireConfigHeaders);
        ValidateConfigField(model, "samplingParams", path, RequireConfigObject);
        ValidateConfigField(model, "compat", path, ValidateConfigCompatibility);
        foreach (var name in new[] { "contextWindow", "maxTokens", "maxOutputTokens" }) ValidateConfigField(model, name, path, RequireConfigTokenCount);
        ValidateConfigField(model, "input", path, (value, field) =>
        {
            if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String || item.GetString() is not ("text" or "image")))
                throw new JsonException(field + " must be an array containing text or image.");
        });
        ValidateConfigField(model, "thinkingLevelMap", path, (value, field) =>
        {
            RequireConfigObject(value, field);
            foreach (var level in new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" })
                if (value.TryGetProperty(level, out var mapped) && mapped.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new JsonException(field + "." + level + " must be a string or null.");
        });
        ValidateConfigField(model, "cost", path, (value, field) => ValidateConfigCost(value, field,
            partial: isOverride || model.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != ModelTypes.Chat));
    }

    /// <summary>【AI】【价格结构校验】完整聊天模型要求全部费率，覆盖和非聊天扩展允许部分费率；分层价格要求阈值及全部费率。</summary>
    /// <param name="cost">价格对象。</param><param name="path">诊断路径。</param><param name="partial">是否允许省略基础费率。</param>
    /// <param name="includeTiers">是否解析顶层 tiers，层内同名字段属于未知扩展。</param>
    private static void ValidateConfigCost(JsonElement cost, string path, bool partial, bool includeTiers = true)
    {
        RequireConfigObject(cost, path);
        foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite" })
        {
            if (!cost.TryGetProperty(name, out var value))
            {
                if (!partial) throw new JsonException(path + "." + name + " is required.");
                continue;
            }
            RequireConfigNumber(value, path + "." + name);
            if (!value.TryGetDecimal(out var rate) || rate == 0m && value.GetDouble() != 0d)
                throw new JsonException(path + "." + name + " must fit the decimal rate range.");
        }
        if (!includeTiers || !cost.TryGetProperty("tiers", out var tiers)) return;
        if (tiers.ValueKind != JsonValueKind.Array) throw new JsonException(path + ".tiers must be an array.");
        var index = 0;
        foreach (var tier in tiers.EnumerateArray())
        {
            var field = path + ".tiers." + index++;
            RequireConfigObject(tier, field);
            if (!tier.TryGetProperty("inputTokensAbove", out var threshold)) throw new JsonException(field + ".inputTokensAbove is required.");
            RequireConfigNumber(threshold, field + ".inputTokensAbove");
            ValidateConfigCost(tier, field, partial: false, includeTiers: false);
        }
    }

    /// <summary>【AI】【可选字段】缺失字段保留默认值，显式 null 等无效值交给对应规则拒绝。</summary>
    /// <param name="parent">父对象。</param><param name="name">字段名称。</param><param name="path">父路径。</param><param name="validate">字段规则。</param>
    private static void ValidateConfigField(JsonElement parent, string name, string path, Action<JsonElement, string> validate)
    {
        if (parent.TryGetProperty(name, out var value)) validate(value, path + "." + name);
    }

    /// <summary>【AI】【对象校验】拒绝数组、标量和 null。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param>
    private static void RequireConfigObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException(path + " must be an object.");
    }

    /// <summary>【AI】【文本校验】名称、标识和配置表达式必须是非空字符串，错误不包含字段值。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param>
    private static void RequireConfigString(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length == 0) throw new JsonException(path + " must be a non-empty string.");
    }

    /// <summary>【AI】【布尔校验】禁止把字符串和数字静默解释为默认值。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param>
    private static void RequireConfigBoolean(JsonElement value, string path)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException(path + " must be a boolean.");
    }

    /// <summary>【AI】【数值校验】接受有限数字，拒绝数字字符串及非有限指数。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param>
    private static void RequireConfigNumber(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException(path + " must be a finite number.");
    }

    /// <summary>【AI】【窗口校验】计数必须可由 .NET 模型的整数表示，支持整数的小数及指数写法。</summary>
    /// <param name="value">字段值。</param><param name="path">诊断路径。</param>
    private static void RequireConfigTokenCount(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number != decimal.Truncate(number) ||
            number < int.MinValue || number > int.MaxValue) throw new JsonException(path + " must be a representable integer.");
    }

    /// <summary>【AI】【请求头校验】请求头映射只接受字符串值，允许空字符串，验证期间不解析环境或执行命令。</summary>
    /// <param name="value">请求头对象。</param><param name="path">诊断路径。</param>
    private static void RequireConfigHeaders(JsonElement value, string path)
    {
        RequireConfigObject(value, path);
        foreach (var header in value.EnumerateObject())
            if (header.Value.ValueKind != JsonValueKind.String) throw new JsonException(path + "." + header.Name + " must be a string.");
    }
}
