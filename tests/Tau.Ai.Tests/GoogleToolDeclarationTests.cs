// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers.Google;

namespace Tau.Ai.Tests;

public sealed partial class GoogleHistoryReplayTests
{
    /// <summary>【Google】【严格采样】验证三个入口对工具约束及显式调用模式的优先级。</summary>
    /// <param name="api">协议。</param><param name="simple">是否使用直接简化入口。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language", false)]
    [InlineData("google-generative-language", true)]
    [InlineData("google-vertex", false)]
    [InlineData("google-vertex", true)]
    [InlineData("google-gemini-cli", false)]
    [InlineData("google-gemini-cli", true)]
    public async Task StrictToolSchemaAndModeHonorExplicitChoice(string api, bool simple)
    {
        foreach (var strict in new string?[] { null, "prefer", "require" })
        foreach (var choice in new string?[] { null, "auto", "any", "none", "unexpected" })
        {
            var tool = SchemaTool(strict);
            var body = await SendAsync(Model(api), new() { Tools = [tool], Messages = [new UserMessage("hi")] }, ToolOptions(api, choice, simple));
            var declaration = body.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
            Assert.False(declaration.TryGetProperty("parameters", out _));
            Assert.False(declaration.TryGetProperty("strict", out _));
            var schema = declaration.GetProperty("parametersJsonSchema");
            if (strict is not null)
            {
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                Assert.Contains(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "path");
            }
            else Assert.True(JsonElement.DeepEquals(tool.ParameterSchema, schema));
            var expected = choice switch { "none" => "NONE", "any" => "ANY", _ when strict is not null => "VALIDATED", null => null, _ => "AUTO" };
            Assert.Equal(expected is not null, body.TryGetProperty("toolConfig", out var config));
            if (expected is not null) Assert.Equal(expected, config.GetProperty("functionCallingConfig").GetProperty("mode").GetString());
            Assert.False(tool.ParameterSchema.TryGetProperty("additionalProperties", out _));
        }
    }

    /// <summary>【Google】【能力回退】旧模型与不兼容 Schema 的 prefer 回退，require 在发包前失败。</summary>
    /// <param name="api">协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task UnsupportedStrictToolsFailOnlyWhenRequired(string api)
    {
        foreach (var id in new[] { "gemini-2.5-flash", "gemma-4", "claude-sonnet-4" })
        {
            var model = Model(api) with { Id = id };
            var body = await SendAsync(model, new() { Messages = [], Tools = [SchemaTool("prefer")] });
            Assert.False(body.TryGetProperty("toolConfig", out _));
            await SendAsync(model, new() { Messages = [], Tools = [SchemaTool("require")] }, expectedError: "strict tools are unsupported");
        }
        using var document = JsonDocument.Parse("""{"type":"object","properties":{"path":{"$ref":"#/$defs/path"}},"$defs":{"path":{"type":"string"}}}""");
        var unsupported = new Tool("read", "read", document.RootElement.Clone()) { ConstrainedSampling = new() { Type = "json_schema", Strict = "prefer" } };
        var fallback = await SendAsync(Model(api), new() { Messages = [], Tools = [unsupported] });
        Assert.False(fallback.TryGetProperty("toolConfig", out _));
        Assert.True(JsonElement.DeepEquals(unsupported.ParameterSchema, fallback.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parametersJsonSchema")));
        await SendAsync(Model(api), new() { Messages = [], Tools = [unsupported with { ConstrainedSampling = new() { Type = "json_schema", Strict = "require" } }] }, expectedError: "requires JSON-schema constrained sampling");
    }

    /// <summary>【Google】【原始 Schema】现代字段保留完整声明，旧 Claude 路径只清理对象元声明。</summary>
    /// <param name="api">协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task SchemaFormatPreservesValidationKeywords(string api)
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","$schema":"schema","$id":"root","$anchor":"a","$dynamicAnchor":"d","$vocabulary":{},"$comment":"c","$defs":{},"definitions":{},
            "additionalProperties":false,"properties":{"path":{"type":"string","$id":"nested","const":"README"}},
            "anyOf":[{"$comment":"array declaration retained","required":["path"]}]}
            """);
        var tool = new Tool("read", "read", schema.RootElement.Clone());
        var model = Model(api) with { Id = "claude-sonnet-4" };
        var body = await SendAsync(model, new() { Messages = [], Tools = [tool] });
        var declaration = body.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
        var legacy = api == "google-gemini-cli";
        var parameters = declaration.GetProperty(legacy ? "parameters" : "parametersJsonSchema");
        if (!legacy) Assert.True(JsonElement.DeepEquals(tool.ParameterSchema, parameters));
        else
        {
            foreach (var key in new[] { "$schema", "$id", "$anchor", "$dynamicAnchor", "$vocabulary", "$comment", "$defs", "definitions" })
                Assert.False(parameters.TryGetProperty(key, out _));
            Assert.False(parameters.GetProperty("properties").GetProperty("path").TryGetProperty("$id", out _));
            Assert.Equal("array declaration retained", parameters.GetProperty("anyOf")[0].GetProperty("$comment").GetString());
        }
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("README", parameters.GetProperty("properties").GetProperty("path").GetProperty("const").GetString());
    }

    /// <summary>【Google】【空工具】没有当前工具时不发送工具声明和调用模式。</summary>
    /// <param name="api">协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task NoToolsOmitsChoiceConfiguration(string api)
    {
        var body = await SendAsync(Model(api), new() { Messages = [], Tools = [] }, ToolOptions(api, "any", false));
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.False(body.TryGetProperty("toolConfig", out _));
    }

    /// <summary>【Google】【工具夹具】创建具有可选参数的工具以观察严格化结果。</summary>
    /// <param name="strict">约束策略。</param><returns>工具。</returns>
    private static Tool SchemaTool(string? strict)
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""");
        return new("read", "read a file", document.RootElement.Clone())
        { ConstrainedSampling = strict is null ? null : new() { Type = "json_schema", Strict = strict } };
    }

    /// <summary>【Google】【选项夹具】为原生和简化入口构造相同工具选择。</summary>
    /// <param name="api">协议。</param><param name="choice">工具选择。</param><param name="simple">是否使用简化入口。</param>
    /// <returns>对应选项。</returns>
    private static StreamOptions ToolOptions(string api, string? choice, bool simple) => simple ? new SimpleStreamOptions { ToolChoice = choice } : api switch
    {
        "google-vertex" => new GoogleVertexOptions { ToolChoice = choice }, "google-gemini-cli" => new GoogleGeminiCliOptions { ToolChoice = choice },
        _ => new GoogleOptions { ToolChoice = choice }
    };
}
