// 作者：xxx
using System.Text.Json.Nodes;

namespace Tau.Ai.Tests;

public sealed partial class OpenAiThinkingTemplateTests
{
    /// <summary>【AI】【格式矩阵】使用固定协议报文验证各格式开关，不依赖被测实现计算预期值。</summary>
    /// <returns>实现、入口、格式、启用状态及预期思考字段。</returns>
    public static TheoryData<bool, bool, string, bool, string> ReasoningCases()
    {
        var data = new TheoryData<bool, bool, string, bool, string>();
        var formats = new (string Format, string On, string Off)[]
        {
            ("openai", """{"reasoning_effort":"high"}""", "{}"),
            ("zai", """{"thinking":{"type":"enabled","clear_thinking":false},"reasoning_effort":"high"}""", """{"thinking":{"type":"disabled"}}"""),
            ("qwen", """{"enable_thinking":true,"reasoning_effort":"high"}""", """{"enable_thinking":false}"""),
            ("qwen-chat-template", """{"chat_template_kwargs":{"enable_thinking":true,"preserve_thinking":true}}""", """{"chat_template_kwargs":{"enable_thinking":false,"preserve_thinking":true}}"""),
            ("deepseek", """{"thinking":{"type":"enabled"},"reasoning_effort":"high"}""", """{"thinking":{"type":"disabled"}}"""),
            ("openrouter", """{"reasoning":{"effort":"high"}}""", """{"reasoning":{"effort":"none"}}"""),
            ("ant-ling", "{}", "{}"),
            ("together", """{"reasoning":{"enabled":true},"reasoning_effort":"high"}""", """{"reasoning":{"enabled":false}}"""),
            ("string-thinking", """{"thinking":"high"}""", """{"thinking":"none"}""")
        };
        foreach (var compatible in new[] { false, true })
        foreach (var simple in new[] { false, true })
        foreach (var (format, on, off) in formats)
        {
            data.Add(compatible, simple, format, true, on);
            data.Add(compatible, simple, format, false, off);
        }
        return data;
    }

    /// <summary>【AI】【思考格式】普通与简化入口、原生与别名提供方发送相同的格式报文。</summary>
    /// <param name="compatible">兼容实现。</param><param name="simple">简化入口。</param><param name="format">思考格式。</param>
    /// <param name="enabled">是否启用。</param><param name="expected">思考字段 JSON。</param><returns>异步测试任务。</returns>
    [Theory]
    [MemberData(nameof(ReasoningCases))]
    public async Task ReasoningFormatsMatchReference(bool compatible, bool simple, string format, bool enabled, string expected)
    {
        var model = CreateModel(format) with { Compat = new() { ThinkingFormat = format, SupportsReasoningEffort = true } };
        using var body = await CaptureAsync(model, compatible, simple, enabled, 4096);
        var actual = new JsonObject();
        foreach (var name in new[] { "thinking", "enable_thinking", "reasoning", "reasoning_effort", "chat_template_kwargs", "chat_template_args" })
            if (body.RootElement.TryGetProperty(name, out var value)) actual[name] = JsonNode.Parse(value.GetRawText());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), "Expected: " + expected + "; Actual: " + actual.ToJsonString());
    }

    /// <summary>【AI】【映射语义】直接请求保留显式 null 与缺省映射的协议差异。</summary>
    /// <param name="format">思考格式。</param><param name="enabled">是否启用。</param><param name="mapped">映射值。</param>
    /// <param name="expected">应存在的思考字段。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("zai", true, null, """{"thinking":{"type":"enabled","clear_thinking":false}}""")]
    [InlineData("qwen", true, null, """{"enable_thinking":true,"reasoning_effort":"high"}""")]
    [InlineData("openai", true, null, """{"reasoning_effort":"high"}""")]
    [InlineData("deepseek", false, null, "{}")]
    [InlineData("openrouter", false, null, "{}")]
    [InlineData("string-thinking", false, null, "{}")]
    [InlineData("openai", false, "none", """{"reasoning_effort":"none"}""")]
    [InlineData("openrouter", false, "disabled", """{"reasoning":{"effort":"disabled"}}""")]
    [InlineData("ant-ling", true, "max", """{"reasoning":{"effort":"max"}}""")]
    [InlineData("ant-ling", true, null, "{}")]
    public async Task ReasoningMappingPreservesProtocolNullRules(string format, bool enabled, string? mapped, string expected)
    {
        var model = CreateModel(format) with
        {
            Compat = new() { ThinkingFormat = format, SupportsReasoningEffort = true },
            ThinkingLevelMap = new Dictionary<string, string?> { [enabled ? "high" : "off"] = mapped }
        };
        foreach (var compatible in new[] { false, true })
        {
            using var body = await CaptureAsync(model, compatible, false, enabled, 4096);
            var actual = new JsonObject();
            foreach (var name in new[] { "thinking", "enable_thinking", "reasoning", "reasoning_effort" })
                if (body.RootElement.TryGetProperty(name, out var value)) actual[name] = JsonNode.Parse(value.GetRawText());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());
        }
    }

    /// <summary>【AI】【能力开关】关闭 effort 能力时保留协议启用开关；非思考模型不发送任何思考字段。</summary>
    /// <param name="reasoning">模型是否支持思考。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReasoningAndEffortCapabilitiesAreIndependent(bool reasoning)
    {
        var model = CreateModel("qwen") with
        {
            Reasoning = reasoning,
            Compat = new() { ThinkingFormat = "qwen", SupportsReasoningEffort = false, SupportsThinkingTokenBudget = true }
        };
        foreach (var compatible in new[] { false, true })
        {
            using var body = await CaptureAsync(model, compatible, true, true, 4096);
            Assert.Equal(reasoning, body.RootElement.TryGetProperty("enable_thinking", out _));
            Assert.Equal(reasoning, body.RootElement.TryGetProperty("thinking_token_budget", out _));
            Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        }
    }
}
