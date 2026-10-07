using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// 【AI】【严格采样】将工具参数 Schema 转换为严格采样支持的对象副本，不修改调用方定义。
    /// </summary>
    /// <param name="schema">工具参数 Schema。</param>
    /// <param name="isUnsupportedKeyword">可选的供应商关键字检查；返回 true 表示不支持该键值。</param>
    /// <returns>严格模式 Schema。</returns>
    public static JsonElement MakeStrictJsonSchema(JsonElement schema, Func<string, JsonElement, bool>? isUnsupportedKeyword = null)
    {
        if (schema.ValueKind != JsonValueKind.Object) throw new UnsupportedStrictJsonSchemaException("root schema must have type object");
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        NormalizeNode(node, isUnsupportedKeyword);
        if (!IsString(node["type"], "object")) throw new UnsupportedStrictJsonSchemaException("root schema must have type object");
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
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
    /// 【AI】【严格采样】仅探测 Schema 是否支持 strict；发送工具时应使用带工具配置的重载。
    /// </summary>
    /// <param name="schema">工具参数 Schema。</param>
    /// <param name="supportsStrictMode">provider 是否声明支持 strict。</param>
    /// <param name="isUnsupportedKeyword">可选的供应商关键字检查。</param>
    /// <returns>支持时返回 true，不支持时返回 null。</returns>
    public static bool? ResolveJsonSchemaStrictSampling(JsonElement schema, bool supportsStrictMode, Func<string, JsonElement, bool>? isUnsupportedKeyword = null)
    {
        if (!supportsStrictMode) return null;
        try
        {
            _ = MakeStrictJsonSchema(schema, isUnsupportedKeyword);
            return true;
        }
        catch (UnsupportedStrictJsonSchemaException)
        {
            return null;
        }
    }

    /// <summary>【AI】【严格采样】解析工具显式配置，prefer 允许回退，require 不满足时报告工具名和原因。</summary>
    /// <param name="tool">工具定义。</param>
    /// <param name="supportsStrictMode">provider 是否支持 strict。</param>
    /// <param name="isUnsupportedKeyword">可选的供应商关键字检查。</param>
    /// <returns>启用 strict 时返回 true，否则返回 null。</returns>
    public static bool? ResolveJsonSchemaStrictSampling(Tool tool, bool supportsStrictMode, Func<string, JsonElement, bool>? isUnsupportedKeyword = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.ConstrainedSampling is not { Type: "json_schema" } config) return null;
        if (supportsStrictMode)
        {
            try
            {
                _ = MakeStrictJsonSchema(tool.ParameterSchema, isUnsupportedKeyword);
                return true;
            }
            catch (UnsupportedStrictJsonSchemaException exception)
            {
                if (config.Strict != "require") return null;
                throw new InvalidOperationException($"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but {exception.Message}.", exception);
            }
        }
        if (config.Strict == "require")
            throw new InvalidOperationException($"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
        return null;
    }

    /// <summary>【AI】【严格采样】按已解析的 strict 开关返回参数，非严格模式保留原始 Schema。</summary>
    /// <param name="tool">工具定义。</param>
    /// <param name="strict">是否已通过严格采样检查。</param>
    /// <returns>严格副本或原始参数 Schema。</returns>
    public static JsonElement GetJsonSchemaToolParameters(Tool tool, bool? strict)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return strict == true ? MakeStrictJsonSchema(tool) : tool.ParameterSchema;
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

    /// <summary>【AI】【语法输入】读取 grammar 工具调用中的字符串输入属性。</summary>
    /// <param name="toolName">工具名称。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="inputProperty">grammar 输入属性名。</param>
    /// <returns>输入文本。</returns>
    public static string GetGrammarToolInput(string toolName, IReadOnlyDictionary<string, object?> arguments, string inputProperty)
    {
        arguments.TryGetValue(inputProperty, out var value);
        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => throw new InvalidOperationException($"Grammar tool call \"{toolName}\" requires argument \"{inputProperty}\" to be a string.")
        };
    }

    /// <summary>【AI】【语法采样】仅解析显式 grammar 工具，按上游规则优先选择 OpenAI Lark 变体。</summary>
    /// <param name="tool">工具定义。</param>
    /// <param name="supportsOpenAiGrammar">provider 是否支持 grammar 工具。</param>
    /// <returns>可用 grammar 描述；未开启或不支持时为空，配置损坏时报告工具名和原因。</returns>
    public static GrammarConstrainedSampling? ResolveGrammarConstrainedSampling(Tool tool, bool supportsOpenAiGrammar)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!supportsOpenAiGrammar || tool.ConstrainedSampling is not { Type: "grammar" } config) return null;

        // 1. 【AI】【语法采样】忽略未知变体，Lark 优先级不受字典插入顺序影响
        var variants = config.Variants;
        var lark = variants?.GetValueOrDefault("openai_lark");
        var regex = variants?.GetValueOrDefault("openai_regex");
        var hasLark = HasGrammarDefinition(lark);
        if (!hasLark && !HasGrammarDefinition(regex))
            throw new InvalidOperationException($"Tool \"{tool.Name}\" cannot use grammar constrained sampling: no supported grammar variant was provided.");

        // 2. 【AI】【语法采样】推断唯一必填字符串属性，允许其他可选字段并保留原始定义
        var root = tool.ParameterSchema;
        string? reason = null;
        string? inputProperty = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object")
            reason = "grammar constrained sampling requires an object parameter schema";
        else if (!root.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array || required.GetArrayLength() != 1 || required[0].ValueKind != JsonValueKind.String)
            reason = "grammar constrained sampling requires exactly one required string property";
        else
        {
            inputProperty = required[0].GetString()!;
            if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(inputProperty, out var input) || !IsTruthyGrammarProperty(input))
                reason = $"grammar constrained sampling requires a properties entry for {inputProperty}";
            else if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("type", out var inputType) || inputType.ValueKind != JsonValueKind.String || inputType.GetString() != "string")
                reason = $"grammar constrained sampling property {inputProperty} must have type string";
        }
        if (reason is not null) throw new InvalidOperationException($"Tool \"{tool.Name}\" cannot use grammar constrained sampling: {reason}.");
        return new(hasLark ? "lark" : "regex", hasLark ? lark! : regex!, inputProperty!);
    }

    /// <summary>按 JavaScript trim 的空白集合识别非空定义，保留 BOM 与 NEL 的语言差异。</summary>
    /// <param name="definition">变体定义，允许缺省。</param>
    /// <returns>存在 trim 后的非空字符时为 true。</returns>
    private static bool HasGrammarDefinition(string? definition) => definition?.Any(character => character is not
        (>= '\t' and <= '\r' or ' ' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or
        '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff')) == true;

    /// <summary>按上游的真假值检查属性条目，避免非法标量产生不同的错误原因。</summary>
    /// <param name="property">参数属性的 JSON 值。</param>
    /// <returns>是否存在真值条目。</returns>
    private static bool IsTruthyGrammarProperty(JsonElement property) => property.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.String => property.GetString()!.Length > 0,
        JsonValueKind.Number => property.GetDouble() != 0,
        _ => true
    };

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
    /// 【AI】【语法增量】将单调增长的 grammar 字符串转换为 JSON 工具参数增量。
    /// </summary>
    /// <param name="buffer">上一次增量状态。</param>
    /// <param name="inputProperty">输入属性名。</param>
    /// <param name="nextInput">新的完整输入。</param>
    /// <param name="close">是否结束输入。</param>
    /// <returns>应发送的 JSON 增量，没有变化时返回 null。</returns>
    public static string? AppendGrammarToolInputJsonDelta(GrammarToolInputBuffer buffer, string inputProperty, string nextInput, bool close)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Closed)
        {
            if (close && nextInput == buffer.Input) return null;
            throw new InvalidOperationException($"grammar tool input for property \"{inputProperty}\" changed after it was closed");
        }
        if (!nextInput.StartsWith(buffer.Input, StringComparison.Ordinal))
            throw new InvalidOperationException($"grammar tool input for property \"{inputProperty}\" changed non-monotonically");
        var suffix = nextInput[buffer.Input.Length..];
        if (!close && suffix.Length == 0) return null;
        var delta = new StringBuilder();
        if (!buffer.Started) delta.Append('{').Append(QuoteGrammarJsonString(inputProperty)).Append(":\"");
        delta.Append(QuoteGrammarJsonString(suffix)[1..^1]);
        if (close) delta.Append("\"}");
        buffer.Input = nextInput;
        buffer.Started = true;
        buffer.Closed = close;
        return delta.ToString();
    }

    /// <summary>【AI】【语法增量】按 JSON.stringify 转义字符串，保留普通 Unicode，并转义孤立代理字符。</summary>
    /// <param name="text">属性名或新增输入文本。</param>
    /// <returns>含双引号的 JSON 字符串。</returns>
    private static string QuoteGrammarJsonString(string text)
    {
        var result = new StringBuilder("\"");
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                    {
                        result.Append(character).Append(text[++index]);
                    }
                    else if (character < ' ' || char.IsSurrogate(character))
                    {
                        result.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else result.Append(character);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    /// <summary>【AI】【严格采样】递归检查 Schema 节点，并把可选属性转换为必填的可空属性。</summary>
    /// <param name="node">待规范化的副本节点。</param>
    /// <param name="isUnsupportedKeyword">供应商附加的关键字限制。</param>
    private static void NormalizeNode(JsonNode? node, Func<string, JsonElement, bool>? isUnsupportedKeyword)
    {
        // 1. 【AI】【严格采样】只检查 Schema 自身的关键字，不把属性名、默认值或示例当成 Schema
        if (node is not JsonObject obj) throw new UnsupportedStrictJsonSchemaException("boolean schemas are unsupported");
        foreach (var key in UnsupportedKeys)
            if (obj.ContainsKey(key)) throw new UnsupportedStrictJsonSchemaException($"{key} schemas are unsupported");
        if (isUnsupportedKeyword is not null)
        {
            using var document = JsonDocument.Parse(obj.ToJsonString());
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (isUnsupportedKeyword(property.Name, property.Value))
                    throw new UnsupportedStrictJsonSchemaException($"{property.Name}: {property.Value.GetRawText()} is unsupported");
            }
        }

        // 2. 【AI】【严格采样】标量联合与数组元素递归校验，拒绝结构联合和元组
        if (obj.ContainsKey("anyOf"))
        {
            if (obj["anyOf"] is not JsonArray { Count: > 0 } union)
                throw new UnsupportedStrictJsonSchemaException("anyOf must contain at least one schema");
            foreach (var variant in union)
            {
                if (variant is JsonObject child && (ReadTypes(child).Any(type => type is "object" or "array") || child.ContainsKey("properties") || child.ContainsKey("items")))
                    throw new UnsupportedStrictJsonSchemaException("object and array unions are unsupported");
                NormalizeNode(variant, isUnsupportedKeyword);
            }
        }
        if (obj.ContainsKey("items"))
        {
            if (obj["items"] is JsonArray) throw new UnsupportedStrictJsonSchemaException("tuple schemas are unsupported");
            NormalizeNode(obj["items"], isUnsupportedKeyword);
        }

        // 3. 【AI】【严格采样】校验对象声明，保留属性顺序，补齐 required 和 additionalProperties
        var isObject = IsString(obj["type"], "object");
        if (obj.ContainsKey("properties") && !isObject) throw new UnsupportedStrictJsonSchemaException("properties require type object");
        if (!isObject) return;
        if (obj.ContainsKey("additionalProperties") && !(obj["additionalProperties"] is JsonValue additional && additional.TryGetValue<bool>(out var allowed) && !allowed))
            throw new UnsupportedStrictJsonSchemaException("schema-valued or true additionalProperties is unsupported");
        if (obj.ContainsKey("properties") && obj["properties"] is not JsonObject)
            throw new UnsupportedStrictJsonSchemaException("object properties must be a schema map");
        if (obj.ContainsKey("required") && (obj["required"] is not JsonArray required || required.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _))))
            throw new UnsupportedStrictJsonSchemaException("object required must be a string array");

        var properties = obj["properties"] as JsonObject ?? new JsonObject();
        var requiredNames = (obj["required"] as JsonArray ?? []).Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if (requiredNames.Any(name => !properties.ContainsKey(name)))
            throw new UnsupportedStrictJsonSchemaException("required contains an unknown property");
        foreach (var property in properties.ToList())
        {
            NormalizeNode(property.Value, isUnsupportedKeyword);
            if (!requiredNames.Contains(property.Key) && !AllowsNull(property.Value!.AsObject()))
                properties[property.Key] = new JsonObject { ["anyOf"] = new JsonArray(property.Value.DeepClone(), new JsonObject { ["type"] = "null" }) };
        }
        obj["required"] = new JsonArray(properties.Select(property => JsonValue.Create(property.Key)).ToArray());
        obj["additionalProperties"] = false;
    }

    /// <summary>读取 Schema 的字符串类型声明，忽略非字符串值。</summary>
    /// <param name="obj">Schema 对象。</param>
    /// <returns>类型名称列表。</returns>
    private static List<string> ReadTypes(JsonObject obj)
    {
        if (obj["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var single)) return [single];
        if (obj["type"] is JsonArray array)
            return array.OfType<JsonValue>().Where(item => item.TryGetValue<string>(out _)).Select(item => item.GetValue<string>()).ToList();
        return [];
    }

    /// <summary>检查类型、常量、枚举或联合是否已显式允许 null。</summary>
    /// <param name="obj">Schema 对象。</param>
    /// <returns>允许 null 时为 true。</returns>
    private static bool AllowsNull(JsonObject obj) => ReadTypes(obj).Contains("null", StringComparer.Ordinal) || (obj.ContainsKey("const") && obj["const"] is null) || (obj["enum"] is JsonArray values && values.Any(item => item is null)) || (obj["anyOf"] is JsonArray anyOf && anyOf.Any(item => item is JsonObject child && AllowsNull(child)));

    /// <summary>比较节点是否为指定字符串，避免非法类型触发非预期异常。</summary>
    /// <param name="node">待检查节点。</param>
    /// <param name="expected">预期文本。</param>
    /// <returns>字符串完全匹配时为 true。</returns>
    private static bool IsString(JsonNode? node, string expected) => node is JsonValue value && value.TryGetValue<string>(out var text) && text == expected;

    /// <summary>标识可按 prefer 策略回退的 Schema 不兼容，其他运行错误继续传播。</summary>
    private sealed class UnsupportedStrictJsonSchemaException : ArgumentException
    {
        /// <summary>创建携带具体不支持原因的异常。</summary>
        /// <param name="message">与上游一致的不支持原因。</param>
        public UnsupportedStrictJsonSchemaException(string message) : base(message) { }
    }
}
