using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Tau.Ai.Utilities;

/// <summary>
/// 提供严格 JSON Schema 与 grammar constrained sampling 的公共辅助能力。
/// </summary>
public static class ConstrainedSampling
{
    private static readonly string[] UnsupportedKeys = ["$ref", "$defs", "definitions", "allOf", "oneOf", "not", "if", "then", "else", "patternProperties", "dependentSchemas", "dependencies", "unevaluatedProperties", "propertyNames", "contains", "prefixItems"];

    /// <summary>Grammar 约束采样的规范化描述。</summary>
    public sealed record GrammarConstrainedSampling(string Format, string Definition, string InputProperty);

    /// <summary>增量 grammar tool input 的状态。</summary>
    public sealed class GrammarToolInputBuffer
    {
        /// <summary>已接收的完整输入。</summary>
        public string Input { get; set; } = string.Empty;
        /// <summary>是否已经发送属性起始标记。</summary>
        public bool Started { get; set; }
        /// <summary>是否已经发送结束标记。</summary>
        public bool Closed { get; set; }
    }

    /// <summary>
    /// 将工具参数 Schema 转换为 OpenAI strict 模式支持的对象 Schema 副本。
    /// </summary>
    /// <param name="schema">工具参数 Schema。</param>
    /// <returns>严格模式 Schema。</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "JsonNode runtime shape is intentionally dynamic for user-provided schemas.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JsonNode runtime shape is intentionally dynamic for user-provided schemas.")]
    public static JsonElement MakeStrictJsonSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Root schema must have type object.", nameof(schema));
        using var cloneDocument = JsonDocument.Parse(schema.GetRawText());
        var node = JsonNode.Parse(cloneDocument.RootElement.GetRawText()) ?? throw new ArgumentException("Schema is empty.", nameof(schema));
        NormalizeNode(node);
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>按工具定义生成严格参数 Schema。</summary>
    /// <param name="tool">工具定义。</param>
    /// <returns>严格模式参数 Schema。</returns>
    public static JsonElement MakeStrictJsonSchema(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return MakeStrictJsonSchema(tool.ParameterSchema);
    }

    /// <summary>
    /// 判断给定工具 Schema 是否可以启用 strict 采样。
    /// </summary>
    /// <param name="schema">工具参数 Schema。</param>
    /// <param name="supportsStrictMode">provider 是否声明支持 strict。</param>
    /// <returns>支持时返回 true，不支持时返回 null。</returns>
    public static bool? ResolveJsonSchemaStrictSampling(JsonElement schema, bool supportsStrictMode)
    {
        if (!supportsStrictMode) return null;
        try
        {
            _ = MakeStrictJsonSchema(schema);
            return true;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>根据工具 constrainedSampling 配置解析 JSON Schema strict 开关。</summary>
    /// <param name="tool">工具定义。</param>
    /// <param name="supportsStrictMode">provider 是否支持 strict。</param>
    /// <returns>启用 strict 时返回 true，否则返回 null。</returns>
    public static bool? ResolveJsonSchemaStrictSampling(Tool tool, bool supportsStrictMode)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!supportsStrictMode) return null;
        try { _ = MakeStrictJsonSchema(tool); return true; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// 校验 grammar constrained sampling 的格式和定义。
    /// </summary>
    /// <param name="format">grammar 格式，只支持 lark 或 regex。</param>
    /// <param name="definition">grammar 定义文本。</param>
    /// <returns>规范化后的 grammar 定义。</returns>
    public static string ResolveGrammar(string format, string definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) throw new ArgumentException("Grammar definition is required.", nameof(definition));
        if (!format.Equals("lark", StringComparison.OrdinalIgnoreCase) && !format.Equals("regex", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Grammar format must be lark or regex.", nameof(format));
        return definition;
    }

    /// <summary>读取 grammar 工具调用中的字符串输入属性。</summary>
    /// <param name="toolName">工具名称。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="inputProperty">grammar 输入属性名。</param>
    /// <returns>输入文本。</returns>
    public static string GetGrammarToolInput(string toolName, IReadOnlyDictionary<string, object?> arguments, string inputProperty)
    {
        if (!arguments.TryGetValue(inputProperty, out var value) || value is not string text)
            throw new InvalidOperationException($"Grammar tool call '{toolName}' requires argument '{inputProperty}' to be a string.");
        return text;
    }

    /// <summary>从工具参数中解析 OpenAI grammar 约束配置。</summary>
    /// <param name="tool">工具定义。</param>
    /// <param name="supportsOpenAiGrammar">provider 是否支持 grammar 工具。</param>
    /// <returns>可用 grammar 描述，不支持时为空。</returns>
    public static GrammarConstrainedSampling? ResolveGrammarConstrainedSampling(Tool tool, bool supportsOpenAiGrammar)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!supportsOpenAiGrammar || tool.GrammarVariants is null) return null;
        var variant = tool.GrammarVariants.FirstOrDefault(pair => pair.Value is { Length: > 0 });
        if (variant.Key is null) throw new ArgumentException($"Tool '{tool.Name}' has no supported grammar variant.", nameof(tool));
        using var document = JsonDocument.Parse(tool.ParameterSchema.GetRawText());
        var root = document.RootElement;
        if (root.GetProperty("type").GetString() != "object" || !root.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array || required.GetArrayLength() != 1)
            throw new ArgumentException("Grammar constrained sampling requires exactly one required string property.", nameof(tool));
        var inputProperty = required[0].GetString() ?? throw new ArgumentException("Grammar input property must be a string.", nameof(tool));
        if (!root.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(inputProperty, out var input) || input.GetProperty("type").GetString() != "string")
            throw new ArgumentException($"Grammar constrained sampling property '{inputProperty}' must have type string.", nameof(tool));
        return new GrammarConstrainedSampling(variant.Key.Contains("regex", StringComparison.OrdinalIgnoreCase) ? "regex" : "lark", variant.Value, inputProperty);
    }

    /// <summary>批量提取 grammar 工具的输入属性映射。</summary>
    /// <param name="tools">工具列表。</param>
    /// <param name="supportsOpenAiGrammar">provider 是否支持 grammar 工具。</param>
    /// <returns>工具名到输入属性名的映射。</returns>
    public static IReadOnlyDictionary<string, string> CreateGrammarToolInputProperties(IEnumerable<Tool>? tools, bool supportsOpenAiGrammar)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools ?? [])
            if (ResolveGrammarConstrainedSampling(tool, supportsOpenAiGrammar) is { } grammar) result[tool.Name] = grammar.InputProperty;
        return result;
    }

    /// <summary>
    /// 将单调增长的 grammar 字符串转换为 JSON 工具参数增量。
    /// </summary>
    /// <param name="buffer">上一次增量状态。</param>
    /// <param name="inputProperty">输入属性名。</param>
    /// <param name="nextInput">新的完整输入。</param>
    /// <param name="close">是否结束输入。</param>
    /// <returns>应发送的 JSON 增量，没有变化时返回 null。</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Grammar JSON delta intentionally serializes dynamic user text.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Grammar JSON delta intentionally serializes dynamic user text.")]
    public static string? AppendGrammarToolInputJsonDelta(GrammarToolInputBuffer buffer, string inputProperty, string nextInput, bool close)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Closed)
        {
            if (close && nextInput == buffer.Input) return null;
            throw new InvalidOperationException($"Grammar tool input for '{inputProperty}' changed after it was closed.");
        }
        if (!nextInput.StartsWith(buffer.Input, StringComparison.Ordinal))
            throw new InvalidOperationException($"Grammar tool input for '{inputProperty}' changed non-monotonically.");
        var suffix = nextInput[buffer.Input.Length..];
        if (!close && suffix.Length == 0) return null;
        var delta = new StringBuilder();
        if (!buffer.Started) delta.Append('{').Append(JsonSerializer.Serialize(inputProperty)).Append(":\"");
        delta.Append(JsonSerializer.Serialize(suffix)[1..^1]);
        if (close) delta.Append("\"}");
        buffer.Input = nextInput;
        buffer.Started = true;
        buffer.Closed = close;
        return delta.ToString();
    }

    private static void NormalizeNode(JsonNode node, bool isRoot = true)
    {
        if (node is not JsonObject obj) throw new ArgumentException("Schema nodes must be objects.");
        foreach (var key in UnsupportedKeys)
            if (obj.ContainsKey(key)) throw new ArgumentException($"{key} schemas are unsupported.");

        var types = ReadTypes(obj);
        if (types.Count == 0 && obj["anyOf"] is null && obj["properties"] is null && obj["items"] is null)
            throw new ArgumentException("Schema must declare a type.");
        if (types.Contains("object") && types.Count > 1)
            throw new ArgumentException("Object unions are unsupported in strict mode.");
        if (types.Contains("array") && types.Count > 1)
            throw new ArgumentException("Array unions are unsupported in strict mode.");
        if (isRoot && obj["type"]?.GetValue<string>() != "object")
            throw new ArgumentException("Root schema must be an object.");
        if (obj["anyOf"] is JsonArray union)
        {
            if (union.Count == 0) throw new ArgumentException("anyOf must contain at least one schema.");
            foreach (var variant in union)
            {
                if (variant is not JsonObject variantObject) throw new ArgumentException("anyOf variants must be objects.");
                var variantTypes = ReadTypes(variantObject);
                if (variantTypes.Count != 1 || variantTypes[0] is "object" or "array")
                    throw new ArgumentException("Only scalar anyOf unions are supported.");
                NormalizeNode(variantObject, false);
            }
        }

        if (types.Contains("object"))
        {
            if (obj["additionalProperties"] is not null && obj["additionalProperties"]?.GetValue<bool>() != false)
                throw new ArgumentException("additionalProperties must be false.");
            obj["additionalProperties"] = false;
            var properties = obj["properties"] as JsonObject;
            if (properties is not null)
            {
                var required = obj["required"] as JsonArray ?? [];
                var requiredNames = required.Select(x => x?.GetValue<string>()).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
                if (requiredNames.Count != required.Count || requiredNames.Any(name => !properties.ContainsKey(name!)))
                    throw new ArgumentException("required contains an unknown or duplicate property.");
                foreach (var property in properties.ToList())
                {
                    if (property.Value is null) throw new ArgumentException("Property schema is null.");
                    NormalizeNode(property.Value, false);
                    if (!requiredNames.Contains(property.Key) && property.Value is JsonObject propertyObject)
                    {
                        var propertyTypes = ReadTypes(propertyObject);
                        if (!AllowsNull(propertyObject)) obj["properties"]!.AsObject()[property.Key] = new JsonObject { ["anyOf"] = new JsonArray(propertyObject.DeepClone(), new JsonObject { ["type"] = "null" }) };
                    }
                }
                obj["required"] = new JsonArray(properties.Select(p => JsonValue.Create(p.Key)).ToArray());
            }
        }
        if (obj["items"] is JsonArray) throw new ArgumentException("Tuple items are unsupported.");
        if (obj["items"] is JsonNode items) NormalizeNode(items, false);
    }

    private static List<string> ReadTypes(JsonObject obj)
    {
        if (obj["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var single)) return [single];
        if (obj["type"] is JsonArray array)
            return array.Select(item => item?.GetValue<string>()).Where(item => item is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToList();
        return [];
    }

    private static bool AllowsNull(JsonObject obj) => ReadTypes(obj).Contains("null", StringComparer.Ordinal) || (obj.ContainsKey("const") && obj["const"] is null) || (obj["enum"] is JsonArray values && values.Any(item => item is null)) || (obj["anyOf"] is JsonArray anyOf && anyOf.Any(item => item is JsonObject child && AllowsNull(child)));
}
