using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Google;

internal static partial class GoogleMessageConverter
{
    /// <summary>【Google】【工具声明】按模型能力解析严格约束并写入当前工具集合和调用模式。</summary>
    /// <param name="body">请求报文。</param><param name="model">目标模型。</param><param name="tools">当前工具集合。</param>
    /// <param name="options">原生或简化选项。</param><param name="useParameters">是否为旧 Cloud Code 后端发送 OpenAPI 参数。</param>
    public static void ApplyTools(Dictionary<string, object> body, Model model, IReadOnlyList<Tool>? tools,
        StreamOptions options, bool useParameters = false)
    {
        if (tools is not { Count: > 0 }) return;
        var supportsStrictMode = GetGeminiMajorVersion(model.Id) >= 3;
        var choice = options switch
        {
            GoogleOptions native => native.ToolChoice,
            GoogleVertexOptions vertex => vertex.ToolChoice,
            GoogleGeminiCliOptions cli => cli.ToolChoice,
            SimpleStreamOptions simple => simple.ToolChoice as string,
            _ => null
        };
        var strictMode = false;
        var declarations = new List<object>();
        // 1. 【Google】【严格工具】require 不满足时在发送前失败，prefer 可保留原始 Schema
        foreach (var tool in tools)
        {
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            strictMode |= strict == true;
            var parameters = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict);
            declarations.Add(new Dictionary<string, object>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                [useParameters ? "parameters" : "parametersJsonSchema"] = useParameters ? SanitizeSchema(parameters) : parameters
            });
        }
        body["tools"] = new List<object> { new Dictionary<string, object> { ["functionDeclarations"] = declarations } };
        // 2. 【Google】【调用模式】none 和 any 优先，其余选择在严格工具存在时使用 VALIDATED
        var mode = choice switch
        {
            "none" => "NONE", "any" => "ANY", _ when strictMode => "VALIDATED",
            _ when !string.IsNullOrEmpty(choice) => "AUTO", _ => null
        };
        if (mode is not null) body["toolConfig"] = new Dictionary<string, object>
        {
            ["functionCallingConfig"] = new Dictionary<string, object> { ["mode"] = mode }
        };
    }

    /// <summary>【Google】【旧参数格式】仅移除对象层级的元声明，保留数组内容及 additionalProperties。</summary>
    /// <param name="schema">参数 Schema。</param><returns>独立的 OpenAPI 兼容对象。</returns>
    private static object SanitizeSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return schema;

        var result = new Dictionary<string, object>();
        foreach (var prop in schema.EnumerateObject())
        {
            if (prop.Name is "$schema" or "$id" or "$anchor" or "$dynamicAnchor" or "$vocabulary" or "$comment" or "$defs" or "definitions")
                continue;

            result[prop.Name] = SanitizeSchema(prop.Value);
        }
        return result;
    }

    /// <summary>【Google】【文字清理】移除不能进入 UTF-8 报文的无配对代理字符。</summary>
    /// <param name="text">原始文字。</param><returns>合法 Unicode 文字。</returns>
    private static string SanitizeText(string text) =>
        UnicodeTextSanitizer.RemoveUnpairedSurrogates(text);
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class GoogleJsonContext : JsonSerializerContext;
