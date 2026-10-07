// 作者：xxx
using System.Text.Json;
using Tau.Ai.Utilities;

namespace Tau.Ai.Tests;

/// <summary>【AI】【严格采样回归】验证上游 JSON Schema 规范化、显式策略及失败边界。</summary>
public sealed class ConstrainedSamplingTests
{
    /// <summary>递归转换可选字段、对象和数组，保留元数据、字段顺序及原始定义。</summary>
    [Fact]
    public void MakeStrict_NormalizesNestedSchemasWithoutMutatingInput()
    {
        var schema = Json("""
            {"type":"object","title":"Input","properties":{
              "path":{"type":"string"},
              "metadata":{"type":"object","properties":{"enabled":{"type":"boolean","default":true}}},
              "rows":{"type":"array","items":{"type":"object","properties":{"value":{"type":"number"}}}},
              "optionalObject":{"type":"object","properties":{"name":{"type":"string"}}}
            },"required":["path","metadata","path"]}
            """);
        var original = schema.GetRawText();
        var strict = ConstrainedSampling.MakeStrictJsonSchema(schema);
        Assert.Equal(original, schema.GetRawText());
        Assert.Equal("Input", strict.GetProperty("title").GetString());
        Assert.False(strict.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "path", "metadata", "rows", "optionalObject" }, strict.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var properties = strict.GetProperty("properties");
        Assert.False(properties.GetProperty("path").TryGetProperty("anyOf", out _));
        var metadata = properties.GetProperty("metadata");
        Assert.False(metadata.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("enabled", Assert.Single(metadata.GetProperty("required").EnumerateArray()).GetString());
        var enabled = metadata.GetProperty("properties").GetProperty("enabled").GetProperty("anyOf");
        Assert.True(enabled[0].GetProperty("default").GetBoolean());
        Assert.Equal("null", enabled[1].GetProperty("type").GetString());
        var rows = properties.GetProperty("rows").GetProperty("anyOf")[0];
        Assert.False(rows.GetProperty("items").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("null", rows.GetProperty("items").GetProperty("properties").GetProperty("value").GetProperty("anyOf")[1].GetProperty("type").GetString());
        Assert.False(properties.GetProperty("optionalObject").GetProperty("anyOf")[0].GetProperty("additionalProperties").GetBoolean());
    }

    /// <summary>无 properties 的空对象仍补空 required，并保持未声明的 properties 缺省。</summary>
    [Fact]
    public void MakeStrict_NormalizesEmptyObject()
    {
        var strict = ConstrainedSampling.MakeStrictJsonSchema(Json("{\"type\":\"object\"}"));
        Assert.Empty(strict.GetProperty("required").EnumerateArray());
        Assert.False(strict.GetProperty("additionalProperties").GetBoolean());
        Assert.False(strict.TryGetProperty("properties", out _));
    }

    /// <summary>已显式允许 null 的字段不再重复包装，包括常量、枚举和嵌套标量联合。</summary>
    /// <param name="property">可空字段 Schema。</param>
    [Theory]
    [InlineData("{\"type\":\"null\"}")]
    [InlineData("{\"type\":[\"string\",\"null\"]}")]
    [InlineData("{\"const\":null}")]
    [InlineData("{\"enum\":[\"yes\",null]}")]
    [InlineData("{\"anyOf\":[{\"type\":\"string\"},{\"anyOf\":[{\"const\":null}]}]}")]
    public void MakeStrict_PreservesExistingNullability(string property)
    {
        var strict = ConstrainedSampling.MakeStrictJsonSchema(Schema(property));
        Assert.True(JsonElement.DeepEquals(Json(property), strict.GetProperty("properties").GetProperty("value")));
    }

    /// <summary>无 type 的标量约束与空 Schema 与上游一样可转换，不额外限制其类型声明。</summary>
    /// <param name="property">字段 Schema。</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enum\":[\"yes\",\"no\"]}")]
    [InlineData("{\"const\":7}")]
    [InlineData("{\"anyOf\":[{\"enum\":[1,2]},{\"type\":[\"string\",\"boolean\"]}]}")]
    public void MakeStrict_AcceptsUntypedScalarSchemas(string property)
    {
        var strict = ConstrainedSampling.MakeStrictJsonSchema(Schema(property));
        var union = strict.GetProperty("properties").GetProperty("value").GetProperty("anyOf");
        Assert.True(JsonElement.DeepEquals(Json(property), union[0]));
        Assert.Equal("null", union[1].GetProperty("type").GetString());
    }

    /// <summary>损坏或不支持的 Schema 在 prefer 下回退、require 下报告原因，原始定义保持不变。</summary>
    /// <param name="schemaText">完整参数 Schema。</param>
    /// <param name="reason">预期错误原因片段。</param>
    [Theory]
    [InlineData("true", "root schema must have type object")]
    [InlineData("[]", "root schema must have type object")]
    [InlineData("{\"type\":[\"object\"]}", "root schema must have type object")]
    [InlineData("{\"properties\":{}}", "properties require type object")]
    [InlineData("{\"type\":\"object\",\"properties\":null}", "object properties must be a schema map")]
    [InlineData("{\"type\":\"object\",\"properties\":[]}", "object properties must be a schema map")]
    [InlineData("{\"type\":\"object\",\"required\":null}", "object required must be a string array")]
    [InlineData("{\"type\":\"object\",\"required\":[7]}", "object required must be a string array")]
    [InlineData("{\"type\":\"object\",\"required\":[\"missing\"]}", "required contains an unknown property")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":true}", "additionalProperties is unsupported")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}", "additionalProperties is unsupported")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":null}", "additionalProperties is unsupported")]
    [InlineData("{\"type\":\"object\",\"anyOf\":[]}", "anyOf must contain at least one schema")]
    [InlineData("{\"type\":\"object\",\"anyOf\":{}}", "anyOf must contain at least one schema")]
    [InlineData("{\"type\":\"object\",\"anyOf\":null}", "anyOf must contain at least one schema")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":false}}", "boolean schemas are unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":null}}", "boolean schemas are unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"anyOf\":[{\"type\":\"object\"},{\"type\":\"null\"}]}}}", "object and array unions are unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"anyOf\":[{\"type\":[\"array\",\"null\"]}]}}}", "object and array unions are unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"anyOf\":[{\"properties\":{}}]}}}", "object and array unions are unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"items\":[]}}}", "tuple schemas are unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"items\":false}}}", "boolean schemas are unsupported")]
    public void Resolve_FallsBackOrRejectsUnsupportedSchema(string schemaText, string reason)
    {
        var tool = Tool(Json(schemaText), "prefer");
        Assert.Null(ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, true));
        Assert.Null(ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool.ParameterSchema, true));
        var error = Assert.Throws<InvalidOperationException>(() => ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool with { ConstrainedSampling = new() { Type = "json_schema", Strict = "require" } }, true));
        Assert.Contains("Tool \"lookup\" requires JSON-schema constrained sampling", error.Message);
        Assert.Contains(reason, error.Message);
        Assert.Equal(schemaText, tool.ParameterSchema.GetRawText());
    }

    /// <summary>通用不支持的关键字即使值为 null，出现在深层字段中也必须拒绝。</summary>
    /// <param name="keyword">不支持的 Schema 关键字。</param>
    [Theory]
    [InlineData("$ref")]
    [InlineData("$defs")]
    [InlineData("definitions")]
    [InlineData("allOf")]
    [InlineData("oneOf")]
    [InlineData("patternProperties")]
    [InlineData("dependentSchemas")]
    [InlineData("dependencies")]
    [InlineData("unevaluatedProperties")]
    [InlineData("propertyNames")]
    [InlineData("contains")]
    [InlineData("prefixItems")]
    [InlineData("not")]
    [InlineData("if")]
    [InlineData("then")]
    [InlineData("else")]
    public void Resolve_RejectsUnsupportedKeywordsRecursively(string keyword)
    {
        var tool = Tool(Schema("{\"type\":\"array\",\"items\":{\"" + keyword + "\":null}}"), "require");
        Assert.Contains(keyword + " schemas are unsupported", Assert.Throws<InvalidOperationException>(() => ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, true)).Message);
    }

    /// <summary>是否启用严格模式由显式 JSON Schema 配置和模型能力共同决定。</summary>
    /// <param name="type">配置类型，null 表示不配置。</param>
    /// <param name="policy">严格策略。</param>
    /// <param name="supports">模型能力。</param>
    /// <param name="expected">预期 strict 开关。</param>
    [Theory]
    [InlineData(null, null, true, null)]
    [InlineData("grammar", "require", true, null)]
    [InlineData("grammar", "require", false, null)]
    [InlineData("json_schema", "prefer", false, null)]
    [InlineData("json_schema", "prefer", true, true)]
    [InlineData("json_schema", "require", true, true)]
    [InlineData("json_schema", null, true, true)]
    public void Resolve_RequiresExplicitConfiguration(string? type, string? policy, bool supports, bool? expected)
    {
        var tool = Tool(Json("{\"type\":\"object\"}"), policy) with { ConstrainedSampling = type is null ? null : new() { Type = type, Strict = policy } };
        Assert.Equal(expected, ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supports));
    }

    /// <summary>require 在模型不支持时立即拒绝，未启用和 grammar 配置不检查 JSON Schema。</summary>
    [Fact]
    public void Resolve_RejectsMissingCapabilityAndSkipsUnconfiguredSchemas()
    {
        var tool = Tool(Json("false"), "require");
        Assert.Equal("Tool \"lookup\" requires JSON-schema constrained sampling, but strict tools are unsupported.", Assert.Throws<InvalidOperationException>(() => ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, false)).Message);
        Assert.Null(ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool with { ConstrainedSampling = null }, true));
        Assert.Null(ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool with { ConstrainedSampling = new() { Type = "grammar" } }, true));
    }

    /// <summary>供应商检查仅处理真实 Schema 节点，不误判同名属性及示例中的字段。</summary>
    [Fact]
    public void Resolve_ProviderPredicateIgnoresDataAndPropertyNames()
    {
        var tool = Tool(Json("""
            {"type":"object","properties":{"minimum":{"type":"number","default":{"minimum":1}},"format":{"type":"string"}},"examples":[{"minimum":1}],"description":"minimum"}
            """), "prefer");
        Assert.True(ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, true, (key, _) => key is "minimum" or "format"));
        var rejected = Tool(Schema("{\"type\":\"array\",\"items\":{\"type\":\"number\",\"minimum\":1}}"), "prefer");
        Assert.Null(ConstrainedSampling.ResolveJsonSchemaStrictSampling(rejected, true, (key, _) => key == "minimum"));
        Assert.True(ConstrainedSampling.ResolveJsonSchemaStrictSampling(rejected, true));
    }

    /// <summary>检查回调自身的错误不得被 prefer 策略吞掉。</summary>
    [Fact]
    public void Resolve_DoesNotSwallowPredicateExceptions()
    {
        var tool = Tool(Json("{\"type\":\"object\"}"), "prefer");
        var error = new ArgumentException("callback failed");
        Assert.Same(error, Assert.Throws<ArgumentException>(() => ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, true, (_, _) => throw error)));
        Assert.Same(error, Assert.Throws<ArgumentException>(() => ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool.ParameterSchema, true, (_, _) => throw error)));
    }

    /// <summary>获取非严格参数时保持原定义，只有严格模式生成规范化副本。</summary>
    [Fact]
    public void GetParameters_PreservesOriginalUnlessStrict()
    {
        var tool = Tool(Schema("{\"type\":\"string\"}"), "prefer");
        Assert.Equal(tool.ParameterSchema.GetRawText(), ConstrainedSampling.GetJsonSchemaToolParameters(tool, null).GetRawText());
        Assert.Equal(tool.ParameterSchema.GetRawText(), ConstrainedSampling.GetJsonSchemaToolParameters(tool, false).GetRawText());
        Assert.True(ConstrainedSampling.GetJsonSchemaToolParameters(tool, true).TryGetProperty("additionalProperties", out _));
        Assert.False(tool.ParameterSchema.TryGetProperty("additionalProperties", out _));
    }

    /// <summary>创建用于策略校验的工具。</summary>
    /// <param name="schema">参数 Schema。</param>
    /// <param name="policy">严格策略。</param>
    /// <returns>显式启用 JSON Schema 约束的工具。</returns>
    private static Tool Tool(JsonElement schema, string? policy) => new("lookup", "Lookup", schema) { ConstrainedSampling = new() { Type = "json_schema", Strict = policy } };

    /// <summary>把字段 Schema 放入对象参数中。</summary>
    /// <param name="property">字段 Schema 文本。</param>
    /// <returns>包含一个可选字段的对象 Schema。</returns>
    private static JsonElement Schema(string property) => Json("{\"type\":\"object\",\"properties\":{\"value\":" + property + "}}");

    /// <summary>解析并复制 JSON，避免文档释放影响测试。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>独立 JSON 值。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
