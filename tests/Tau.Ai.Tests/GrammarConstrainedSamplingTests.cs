// 作者：xxx
using System.Text;
using System.Text.Json;
using Tau.Ai.Utilities;

namespace Tau.Ai.Tests;

/// <summary>【AI】【语法采样回归】验证变体选择、输入属性推断和 JSON 字符串增量。</summary>
public sealed class GrammarConstrainedSamplingTests
{
    private const string Schema = """{"type":"object","properties":{"code":{"type":"string"},"optional":{"type":"integer"}},"required":["code"]}""";

    /// <summary>只有显式 grammar 类型且模型支持时才校验配置。</summary>
    /// <param name="type">配置类型。</param>
    /// <param name="supported">模型能力。</param>
    [Theory]
    [InlineData(null, true)]
    [InlineData("json_schema", true)]
    [InlineData("unknown", true)]
    [InlineData("grammar", false)]
    public void Resolve_SkipsUnconfiguredOrUnsupported(string? type, bool supported)
    {
        var tool = Tool("false") with { ConstrainedSampling = type is null ? null : new() { Type = type } };
        Assert.Null(ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, supported));
    }

    /// <summary>Lark 优先于 regex 和未知变体，同时保留定义中的前后空白。</summary>
    [Fact]
    public void Resolve_PrefersLarkRegardlessOfInsertionOrder()
    {
        var tool = Tool(Schema) with { ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string>
        {
            ["other_regex"] = "ignored", ["openai_regex"] = "[a-z]+", ["openai_lark"] = "  start: WORD\n"
        } } };
        var grammar = ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, true);
        Assert.Equal(new("lark", "  start: WORD\n", "code"), grammar);
        Assert.Equal(Schema, tool.ParameterSchema.GetRawText());
    }

    /// <summary>空白 Lark 被忽略，只有准确名称的 regex 变体可以回退。</summary>
    /// <param name="lark">空白 Lark。</param>
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u3000")]
    [InlineData("\uFEFF \n\uFEFF")]
    public void Resolve_FallsBackToRegex(string lark)
    {
        var tool = Tool(Schema) with { ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string>
        {
            ["openai_lark"] = lark, ["openai_regex"] = " ^[0-9]+$ "
        } } };
        Assert.Equal(new("regex", " ^[0-9]+$ ", "code"), ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, true));
    }

    /// <summary>JavaScript 不把 NEL 视为空白，不能沿用 .NET IsNullOrWhiteSpace 的回退结果。</summary>
    [Fact]
    public void Resolve_PreservesJavaScriptNonWhitespace()
    {
        var tool = Tool(Schema) with { ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string>
        {
            ["openai_lark"] = "\u0085", ["openai_regex"] = "fallback"
        } } };
        Assert.Equal(new("lark", "\u0085", "code"), ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, true));
    }

    /// <summary>缺失、未知或空白变体返回上游格式的具名错误。</summary>
    /// <param name="variant">变体名，空值表示缺失字典。</param>
    /// <param name="definition">变体文本。</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData("vendor_lark", "start: WORD")]
    [InlineData("OPENAI_LARK", "start: WORD")]
    [InlineData("openai_lark", " \n")]
    [InlineData("openai_regex", "")]
    public void Resolve_RejectsMissingSupportedVariant(string? variant, string? definition)
    {
        var tool = Tool(Schema) with { ConstrainedSampling = new() { Type = "grammar", Variants = variant is null ? null : new Dictionary<string, string> { [variant] = definition! } } };
        Assert.Equal("Tool \"execute\" cannot use grammar constrained sampling: no supported grammar variant was provided.",
            Assert.Throws<InvalidOperationException>(() => ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, true)).Message);
    }

    /// <summary>损坏 Schema 按对象、唯一必填、属性存在和字符串类型的顺序返回原因。</summary>
    /// <param name="schema">待校验 Schema。</param>
    /// <param name="reason">预期原因。</param>
    [Theory]
    [InlineData("false", "grammar constrained sampling requires an object parameter schema")]
    [InlineData("{}", "grammar constrained sampling requires an object parameter schema")]
    [InlineData("{\"type\":[\"object\"]}", "grammar constrained sampling requires an object parameter schema")]
    [InlineData("{\"type\":\"object\"}", "grammar constrained sampling requires exactly one required string property")]
    [InlineData("{\"type\":\"object\",\"required\":[]}", "grammar constrained sampling requires exactly one required string property")]
    [InlineData("{\"type\":\"object\",\"required\":[\"a\",\"b\"]}", "grammar constrained sampling requires exactly one required string property")]
    [InlineData("{\"type\":\"object\",\"required\":[1]}", "grammar constrained sampling requires exactly one required string property")]
    [InlineData("{\"type\":\"object\",\"required\":[\"code\"]}", "grammar constrained sampling requires a properties entry for code")]
    [InlineData("{\"type\":\"object\",\"required\":[\"code\"],\"properties\":{\"code\":null}}", "grammar constrained sampling requires a properties entry for code")]
    [InlineData("{\"type\":\"object\",\"required\":[\"code\"],\"properties\":{\"code\":0}}", "grammar constrained sampling requires a properties entry for code")]
    [InlineData("{\"type\":\"object\",\"required\":[\"code\"],\"properties\":{\"code\":\"\"}}", "grammar constrained sampling requires a properties entry for code")]
    [InlineData("{\"type\":\"object\",\"required\":[\"code\"],\"properties\":{\"code\":{}}}", "grammar constrained sampling property code must have type string")]
    [InlineData("{\"type\":\"object\",\"required\":[\"code\"],\"properties\":{\"code\":{\"type\":\"number\"}}}", "grammar constrained sampling property code must have type string")]
    [InlineData("{\"type\":\"object\",\"required\":[\"code\"],\"properties\":{\"code\":{\"type\":[\"string\",\"null\"]}}}", "grammar constrained sampling property code must have type string")]
    public void Resolve_ReportsSchemaReason(string schema, string reason) => Assert.Equal(
        $"Tool \"execute\" cannot use grammar constrained sampling: {reason}.",
        Assert.Throws<InvalidOperationException>(() => ConstrainedSampling.ResolveGrammarConstrainedSampling(Tool(schema), true)).Message);

    /// <summary>重复工具名使用最后的属性定义，未配置或不支持的工具不进入映射。</summary>
    [Fact]
    public void Mapping_UsesLastDeclarationAndAllowsEmptyPropertyName()
    {
        var first = Tool(Schema);
        var second = Tool("{\"type\":\"object\",\"properties\":{\"\":{\"type\":\"string\"}},\"required\":[\"\"]}");
        var mapping = ConstrainedSampling.CreateGrammarToolInputProperties([first, first with { Name = "plain", ConstrainedSampling = null }, second], true);
        Assert.Equal("", Assert.Single(mapping).Value);
        Assert.Equal("execute", Assert.Single(mapping).Key);
        Assert.Empty(ConstrainedSampling.CreateGrammarToolInputProperties([Tool("false")], false));
        Assert.Empty(ConstrainedSampling.CreateGrammarToolInputProperties(null, true));
    }

    /// <summary>输入读取接受普通字符串与 JSON 字符串节点，错误类型保持具名错误。</summary>
    [Fact]
    public void Input_RequiresStringProperty()
    {
        Assert.Equal("", ConstrainedSampling.GetGrammarToolInput("execute", new Dictionary<string, object?> { ["code"] = "" }, "code"));
        Assert.Equal("print('中文')", ConstrainedSampling.GetGrammarToolInput("execute", new Dictionary<string, object?> { ["code"] = Json("\"print('中文')\"") }, "code"));
        foreach (var value in new object?[] { null, 42, false, Json("false"), Json("null") })
        {
            Assert.Equal("Grammar tool call \"execute\" requires argument \"code\" to be a string.", Assert.Throws<InvalidOperationException>(() =>
                ConstrainedSampling.GetGrammarToolInput("execute", new Dictionary<string, object?> { ["code"] = value }, "code")).Message);
        }
        Assert.Throws<InvalidOperationException>(() => ConstrainedSampling.GetGrammarToolInput("execute", new Dictionary<string, object?>(), "code"));
    }

    /// <summary>多次输入增量拼接为完整 JSON，Unicode 与必要转义保持上游文本。</summary>
    [Fact]
    public void Delta_ProducesExactJsonAndClosesOnce()
    {
        var buffer = new ConstrainedSampling.GrammarToolInputBuffer();
        var combined = new StringBuilder();
        Assert.Null(ConstrainedSampling.AppendGrammarToolInputJsonDelta(buffer, "代\"码", "", false));
        Assert.False(buffer.Started);
        var first = ConstrainedSampling.AppendGrammarToolInputJsonDelta(buffer, "代\"码", "中文😀<>&", false);
        Assert.Equal("{\"代\\\"码\":\"中文😀<>&", first);
        combined.Append(first);
        Assert.Null(ConstrainedSampling.AppendGrammarToolInputJsonDelta(buffer, "代\"码", "中文😀<>&", false));
        var next = "中文😀<>&\n\t\r\b\f\0\"\\";
        var last = ConstrainedSampling.AppendGrammarToolInputJsonDelta(buffer, "代\"码", next, true);
        Assert.Equal("\\n\\t\\r\\b\\f\\u0000\\\"\\\\\"}", last);
        combined.Append(last);
        Assert.Equal(next, Json(combined.ToString()).GetProperty("代\"码").GetString());
        Assert.True(buffer.Closed);
        Assert.Null(ConstrainedSampling.AppendGrammarToolInputJsonDelta(buffer, "代\"码", next, true));
    }

    /// <summary>空输入也能闭合，孤立代理字符按 JSON.stringify 保真转义。</summary>
    [Fact]
    public void Delta_HandlesEmptyInputAndSplitSurrogatePair()
    {
        Assert.Equal("{\"input\":\"\"}", ConstrainedSampling.AppendGrammarToolInputJsonDelta(new(), "input", "", true));
        var buffer = new ConstrainedSampling.GrammarToolInputBuffer();
        var high = ConstrainedSampling.AppendGrammarToolInputJsonDelta(buffer, "input", "\uD83D", false);
        var low = ConstrainedSampling.AppendGrammarToolInputJsonDelta(buffer, "input", "\uD83D\uDE00", true);
        Assert.Equal("{\"input\":\"\\ud83d", high);
        Assert.Equal("\\ude00\"}", low);
        Assert.Equal("😀", Json(high + low).GetProperty("input").GetString());
    }

    /// <summary>非单调或闭合后修改输入时抛出上游错误，缓冲区保持原状态。</summary>
    /// <param name="closed">初始是否已经闭合。</param>
    /// <param name="next">非法的新输入。</param>
    /// <param name="close">本次是否要求闭合。</param>
    [Theory]
    [InlineData(false, "a", false)]
    [InlineData(false, "other", true)]
    [InlineData(true, "abc", false)]
    [InlineData(true, "abcd", true)]
    public void Delta_RejectsInvalidTransitions(bool closed, string next, bool close)
    {
        var buffer = new ConstrainedSampling.GrammarToolInputBuffer { Input = "abc", Started = true, Closed = closed };
        var reason = closed ? "changed after it was closed" : "changed non-monotonically";
        Assert.Equal($"grammar tool input for property \"code\" {reason}", Assert.Throws<InvalidOperationException>(() =>
            ConstrainedSampling.AppendGrammarToolInputJsonDelta(buffer, "code", next, close)).Message);
        Assert.Equal("abc", buffer.Input);
        Assert.Equal(closed, buffer.Closed);
    }

    /// <summary>创建具有有效 Lark 定义的工具。</summary>
    /// <param name="schema">参数 Schema。</param>
    /// <returns>语法工具。</returns>
    private static Tool Tool(string schema) => new("execute", "Execute", Json(schema))
    {
        ConstrainedSampling = new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_lark"] = "start: WORD" } }
    };

    /// <summary>创建独立持有内存的 JSON 值。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>JSON 副本。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
