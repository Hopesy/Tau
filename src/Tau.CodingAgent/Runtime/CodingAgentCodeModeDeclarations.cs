// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tau.AgentCore;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【脚本声明】把工具 JSON Schema 转换为有界的 TypeScript 调用说明。</summary>
public static class CodingAgentCodeModeDeclarations
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>【CodingAgent】【MCP 共享类型】提供脚本声明中 CallToolResult 引用的完整协议类型。</summary>
    public const string McpTypeScriptPreamble = """
        type Role = "user" | "assistant";
        type MetaObject = Record<string, unknown>;
        type Annotations = { audience?: Role[]; priority?: number; lastModified?: string; };
        type Icon = { src: string; mimeType?: string; sizes?: string[]; theme?: "light" | "dark"; };
        type TextResourceContents = { uri: string; mimeType?: string; _meta?: MetaObject; text: string; };
        type BlobResourceContents = { uri: string; mimeType?: string; _meta?: MetaObject; blob: string; };
        type TextContent = { type: "text"; text: string; annotations?: Annotations; _meta?: MetaObject; };
        type ImageContent = { type: "image"; data: string; mimeType: string; annotations?: Annotations; _meta?: MetaObject; };
        type AudioContent = { type: "audio"; data: string; mimeType: string; annotations?: Annotations; _meta?: MetaObject; };
        type ResourceLink = { icons?: Icon[]; name: string; title?: string; uri: string; description?: string; mimeType?: string; annotations?: Annotations; size?: number; _meta?: MetaObject; type: "resource_link"; };
        type EmbeddedResource = { type: "resource"; resource: TextResourceContents | BlobResourceContents; annotations?: Annotations; _meta?: MetaObject; };
        type ContentBlock = TextContent | ImageContent | AudioContent | ResourceLink | EmbeddedResource;
        type CallToolResult<TStructured = { [key: string]: unknown }> = { _meta?: MetaObject; content: ContentBlock[]; isError?: boolean; structuredContent?: TStructured; [key: string]: unknown; };
        """;

    /// <summary>【CodingAgent】【工具示例】生成供 ALL_TOOLS 和搜索结果使用的说明及工具声明。</summary>
    /// <param name="tool">工具元数据。</param><returns>说明与 TypeScript 签名。</returns>
    public static string Sample(IAgentTool tool) => tool.Description.Trim() + "\n\ncodemode tool declaration:\n```ts\ndeclare const tools: { " +
        CodingAgentCodeModeSandbox.Identifier(tool.Name) + "(args: " + SchemaType(tool.ParameterSchema, 16000) + "): Promise<" + OutputType(tool.OutputSchema) + ">; };\n```";

    /// <summary>【CodingAgent】【输出声明】识别 MCP 原始结果包络，其余工具未声明结构时返回文本。</summary>
    /// <param name="schema">输出 Schema。</param><returns>工具返回类型。</returns>
    public static string OutputType(JsonElement? schema)
    {
        if (schema is null) return "string";
        var root = JsonNode.Parse(schema.Value.GetRawText());
        if (root is JsonObject obj && obj["properties"] is JsonObject properties && properties["content"] is JsonObject content &&
            String(content["type"]) == "array" && content["items"] is JsonObject item && String(item["type"]) == "object" &&
            properties["isError"] is JsonObject error && String(error["type"]) == "boolean" && properties["_meta"] is JsonObject meta && String(meta["type"]) == "object")
        {
            var structured = properties["structuredContent"];
            var type = structured is JsonObject || structured?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? Render(structured, new(structured)) : "unknown";
            return type == "unknown" ? "CallToolResult" : "CallToolResult<" + type + ">";
        }
        return SchemaType(schema.Value);
    }

    /// <summary>【CodingAgent】【Schema 类型】展开本地引用，递归和外部引用回退 unknown，超出长度也回退。</summary>
    /// <param name="schema">JSON Schema。</param><param name="maxChars">可选字符上限。</param><returns>TypeScript 类型表达式。</returns>
    public static string SchemaType(JsonElement schema, int? maxChars = null)
    {
        var root = JsonNode.Parse(schema.GetRawText()); var type = Render(root, new(root));
        return maxChars is { } maximum && type.Length > maximum ? "unknown" : type;
    }

    /// <summary>【CodingAgent】【Schema 分支】依次处理引用、常量、联合、交叉、类型和可推断结构。</summary>
    /// <param name="schema">当前 Schema。</param><param name="context">引用展开状态。</param><returns>类型文本。</returns>
    private static string Render(JsonNode? schema, Context context)
    {
        if (schema?.GetValueKind() == JsonValueKind.False) return "never";
        if (schema is not JsonObject obj) return "unknown";
        if (String(obj["$ref"]) is { } reference)
        {
            if (context.Expansions >= 32 || !context.Resolving.Add(reference)) return "unknown";
            try { var target = Resolve(reference, context.Root); if (target is null) return "unknown"; context.Expansions++; return Render(target, context); }
            finally { context.Resolving.Remove(reference); }
        }
        if (obj.ContainsKey("const")) return obj["const"]?.ToJsonString(Json) ?? "null";
        if (obj["enum"] is JsonArray values) return Union(values.Select(value => value?.ToJsonString(Json) ?? "null"));
        if ((obj["anyOf"] as JsonArray ?? obj["oneOf"] as JsonArray) is { } variants) return Union(variants.Select(value => Render(value, context)));
        if (obj["allOf"] is JsonArray parts)
        {
            var types = parts.Select(value => Render(value, context)).Where(type => type != "unknown").ToArray();
            return types.Length == 0 ? "unknown" : string.Join(" & ", types.Select(type => type.Contains(" | ", StringComparison.Ordinal) ? "(" + type + ")" : type));
        }
        if (obj["type"] is JsonArray typesArray) return Union(typesArray.Select(type => { var branch = (JsonObject)obj.DeepClone(); branch["type"] = type?.DeepClone(); return Render(branch, context); }));
        return String(obj["type"]) switch
        {
            "string" => "string", "number" or "integer" => "number", "boolean" => "boolean", "null" => "null",
            "array" => ArrayType(obj, context), "object" => ObjectType(obj, context),
            null when obj.ContainsKey("properties") || obj.ContainsKey("additionalProperties") || obj.ContainsKey("required") => ObjectType(obj, context),
            null when obj.ContainsKey("items") || obj.ContainsKey("prefixItems") => ArrayType(obj, context), _ => "unknown"
        };
    }

    /// <summary>【CodingAgent】【数组类型】普通 items 优先，其后处理新旧元组表示。</summary><param name="schema">数组 Schema。</param><param name="context">引用上下文。</param><returns>数组或元组类型。</returns>
    private static string ArrayType(JsonObject schema, Context context)
    {
        if (schema.ContainsKey("items") && schema["items"] is not JsonArray) return "Array<" + Render(schema["items"], context) + ">";
        var tuple = schema["prefixItems"] as JsonArray ?? schema["items"] as JsonArray;
        return tuple is { Count: > 0 } ? "[" + string.Join(", ", tuple.Select(value => Render(value, context))) + "]" : "unknown[]";
    }

    /// <summary>【CodingAgent】【对象类型】排序属性、标记可选字段，保留描述为独立注释行并处理索引签名。</summary>
    /// <param name="schema">对象 Schema。</param><param name="context">引用上下文。</param><returns>对象类型。</returns>
    private static string ObjectType(JsonObject schema, Context context)
    {
        var properties = schema["properties"] as JsonObject ?? new();
        var required = (schema["required"] as JsonArray ?? []).Select(String).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var names = properties.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        var members = names.Select(name => (Regex.IsMatch(name, "\\A[A-Za-z_$][A-Za-z0-9_$]*\\z", RegexOptions.CultureInvariant) ? name : JsonValue.Create(name)!.ToJsonString(Json)) +
            (required.Contains(name) ? "" : "?") + ": " + Render(properties[name], context) + ";").ToList();
        if (schema.ContainsKey("additionalProperties") && schema["additionalProperties"]?.GetValueKind() != JsonValueKind.False)
            members.Add("[key: string]: " + Render(schema["additionalProperties"], context) + ";");
        else if (!schema.ContainsKey("additionalProperties") && names.Length == 0) members.Add("[key: string]: unknown;");
        if (members.Count == 0) return "{}";
        var descriptions = names.Select(name => properties[name] is JsonObject property ? String(property["description"])?.Trim() ?? "" : "").ToArray();
        if (!descriptions.Any(value => value.Length > 0)) return "{ " + string.Join(' ', members) + " }";
        var lines = new List<string> { "{" };
        for (var index = 0; index < names.Length; index++)
        {
            foreach (var line in descriptions[index].Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)) lines.Add("  // " + line);
            lines.Add("  " + members[index].Replace("\n", "\n  ", StringComparison.Ordinal));
        }
        lines.AddRange(members.Skip(names.Length).Select(member => "  " + member)); lines.Add("}");
        return string.Join('\n', lines);
    }

    /// <summary>【CodingAgent】【本地引用】解码 JSON Pointer 转义，不读取外部文件或网络。</summary><param name="reference">引用。</param><param name="root">根 Schema。</param><returns>目标或空值。</returns>
    private static JsonNode? Resolve(string reference, JsonNode? root)
    {
        if (reference != "#" && !reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        var current = root;
        foreach (var segment in (reference.Length > 2 ? reference[2..] : "").Split('/').Where(part => part.Length > 0))
        {
            var key = Uri.UnescapeDataString(segment).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(key, out current)) return null;
        }
        return current is JsonObject || current?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? current : null;
    }
    /// <summary>【CodingAgent】【类型合并】去重联合类型，unknown 吸收其他分支。</summary><param name="types">类型集合。</param><returns>合并类型。</returns>
    private static string Union(IEnumerable<string> types) { var values = types.Distinct(StringComparer.Ordinal).ToArray(); return values.Contains("unknown") ? "unknown" : values.Length == 0 ? "never" : string.Join(" | ", values); }
    /// <summary>【CodingAgent】【Schema 字符串】读取可选字符串。</summary><param name="value">JSON 值。</param><returns>字符串或空值。</returns>
    private static string? String(JsonNode? value) => value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    /// <summary>【CodingAgent】【引用限制】记录当前路径和全局展开次数。</summary><param name="root">根 Schema。</param>
    private sealed class Context(JsonNode? root) { internal JsonNode? Root { get; } = root; internal HashSet<string> Resolving { get; } = new(StringComparer.Ordinal); internal int Expansions; }
}
