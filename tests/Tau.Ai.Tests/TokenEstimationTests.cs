// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;

namespace Tau.Ai.Tests;

/// <summary>【AI】【上下文估算回归】验证原始消息成本、有效用量和预算边界。</summary>
public sealed class TokenEstimationTests
{
    /// <summary>文本按 UTF-16 长度统一向上取整。</summary>
    /// <param name="text">输入文本。</param>
    /// <param name="expected">预期 token 数。</param>
    [Theory]
    [InlineData("", 0)]
    [InlineData("1234", 1)]
    [InlineData("12345", 2)]
    [InlineData("中文测试", 1)]
    [InlineData("😀😀😀", 2)]
    public void Text_UsesUtf16Characters(string text, long expected) => Assert.Equal(expected, TokenEstimation.EstimateTextTokens(text));

    /// <summary>总量优先，零或缺省总量回退到输入、输出与缓存，不重复计入细分。</summary>
    /// <param name="total">服务端总量。</param>
    /// <param name="expected">有效总量。</param>
    [Theory]
    [InlineData(null, 100)]
    [InlineData(0, 100)]
    [InlineData(81, 81)]
    public void Usage_PrefersReportedTotal(int? total, long expected) => Assert.Equal(expected,
        TokenEstimation.CalculateContextTokens(new(10, 20, 30, 40) { TotalTokens = total, ReasoningTokens = 5, CacheWrite1hTokens = 15 }));

    /// <summary>多块文本只取整一次，图片不按 base64 字节计费，签名不进入助手成本。</summary>
    [Fact]
    public void Message_CountsImagesThinkingAndToolArguments()
    {
        ContentBlock[] content = [new TextContent("a"), new TextContent("b"), new ImageContent("large-base64", "image/png")];
        Assert.Equal(1_201, TokenEstimation.EstimateMessageTokens(new UserMessage(content)));
        Assert.Equal(1_201, TokenEstimation.EstimateMessageTokens(new ToolResultMessage("call", content)));
        Assert.Equal(4, TokenEstimation.EstimateMessageTokens(new AssistantMessage([
            new TextContent("a") { TextSignature = new string('s', 100) },
            new ThinkingContent("b") { ThinkingSignature = new string('s', 100) },
            new ToolCallContent("id", "read", "{ \"x\" : 1 }")])));
    }

    /// <summary>工具参数按 JavaScript 紧凑 JSON 字符成本计数，支持中文、表情与指数边界。</summary>
    /// <param name="arguments">原始参数 JSON。</param>
    /// <param name="compact">上游 JSON.stringify 后的文本。</param>
    [Theory]
    [InlineData("{ \"x\" : \"中文😀<>&\" }", "{\"x\":\"中文😀<>&\"}")]
    [InlineData("{\"x\":1e20}", "{\"x\":100000000000000000000}")]
    [InlineData("{\"x\":1e21}", "{\"x\":1e+21}")]
    [InlineData("{\"x\":1e-6}", "{\"x\":0.000001}")]
    [InlineData("{\"x\":1e-7}", "{\"x\":1e-7}")]
    [InlineData("{\"x\":-0.0}", "{\"x\":0}")]
    [InlineData("{\"x\":[true,false,null,\"a\\nb\\t\\u0000\"]}", "{\"x\":[true,false,null,\"a\\nb\\t\\u0000\"]}")]
    [InlineData("invalid", "[unserializable]")]
    public void ToolArguments_UseCompactJson(string arguments, string compact) => Assert.Equal(
        TokenEstimation.EstimateTextTokens("tool" + compact),
        TokenEstimation.EstimateMessageTokens(new AssistantMessage([new ToolCallContent("id", "tool", arguments)])));

    /// <summary>系统增量分别计算正文、段落、新增工具与移除引用，不能提前合并而丢失成本。</summary>
    [Fact]
    public void SystemMessages_CountAddedAndRemovedDeclarations()
    {
        using var schema = JsonDocument.Parse("{ \"type\": \"object\" }");
        var tool = new Tool("读😀", "中文", schema.RootElement.Clone());
        var added = new SystemMessage("a")
        {
            Sections = new Dictionary<string, string?> { ["one"] = "b", ["deleted"] = null }, ToolsAdded = [tool]
        };
        var removed = new SystemMessage("") { ToolsRemoved = [new("读😀")] };
        var declarationTokens = TokenEstimation.EstimateTextTokens("[{\"name\":\"读😀\",\"description\":\"中文\",\"parameters\":{\"type\":\"object\"}}]");
        var removalTokens = TokenEstimation.EstimateTextTokens("[{\"name\":\"读😀\"}]");
        var estimate = TokenEstimation.EstimateContextTokens(new ChatMessage[] { added, removed });
        Assert.Equal(new ContextUsageEstimate(1 + declarationTokens + removalTokens, 0, 1 + declarationTokens + removalTokens, null), estimate);
        Assert.Equal(0, TokenEstimation.EstimateToolsTokens([]));
        Assert.Equal(0, TokenEstimation.EstimateToolReferencesTokens(null));
    }

    /// <summary>复现上游压缩后保留旧助手的样例，旧用量不能覆盖较新的摘要。</summary>
    [Fact]
    public void Context_IgnoresUsagePredatingSummary()
    {
        var context = Transcript.NormalizeContext(new LlmContext("system", [
            new UserMessage("summary") { Timestamp = Time(200) },
            new AssistantMessage([new TextContent("kept")]) { Timestamp = Time(100), Usage = new(9_500, 0) },
            new UserMessage(new string('x', 4_000)) { Timestamp = Time(300) }], null));
        Assert.Equal(new ContextUsageEstimate(1_005, 0, 1_005, null), TokenEstimation.EstimateContextTokens(context));
        Assert.Equal(4_899, SimpleTokenOptions.ClampMaxTokensToContext(Model(10_000), context, 8_000));
    }

    /// <summary>后续新回复恢复真实用量，只估算该回复后的尾部消息。</summary>
    [Fact]
    public void Context_UsesFreshUsageAfterStaleResponse()
    {
        ChatMessage[] messages = [
            new UserMessage("summary") { Timestamp = Time(200) },
            new AssistantMessage([new TextContent("kept")]) { Timestamp = Time(100), Usage = new(9_500, 0) },
            new UserMessage("new prompt") { Timestamp = Time(300) },
            new AssistantMessage([new TextContent("reply")]) { Timestamp = Time(400), Usage = new(2_000, 0) },
            new UserMessage("tail") { Timestamp = Time(500) }];
        Assert.Equal(new ContextUsageEstimate(2_001, 2_000, 1, 3), TokenEstimation.EstimateContextTokens(messages));
    }

    /// <summary>错误和取消用量不替代此前有效前缀，但对应内容仍进入尾部估算。</summary>
    /// <param name="reason">无效停止原因。</param>
    [Theory]
    [InlineData(StopReason.Error)]
    [InlineData(StopReason.Aborted)]
    public void Context_SkipsFailedUsage(StopReason reason)
    {
        ChatMessage[] messages = [new AssistantMessage { Usage = new(100, 0), Timestamp = Time(1) },
            new AssistantMessage([new TextContent("failed")]) { Usage = new(900, 0), Timestamp = Time(2), StopReason = reason }];
        Assert.Equal(new ContextUsageEstimate(102, 100, 2, 0), TokenEstimation.EstimateContextTokens(messages));
    }

    /// <summary>所有角色的时间均约束后续用量；旧式无时间戳消息按 Unix 起点兼容。</summary>
    [Fact]
    public void Context_TracksSystemAndToolTimestampsAndLegacyMessages()
    {
        ChatMessage[] messages = [new SystemMessage("") { Timestamp = Time(300) },
            new AssistantMessage { Timestamp = Time(200), Usage = new(900, 0) },
            new ToolResultMessage("id", []) { Timestamp = Time(500) },
            new AssistantMessage { Timestamp = Time(400), Usage = new(900, 0) }];
        Assert.Null(TokenEstimation.EstimateContextTokens(messages).LastUsageIndex);
        Assert.Equal(new ContextUsageEstimate(101, 100, 1, 1), TokenEstimation.EstimateContextTokens(
            new ChatMessage[] { new UserMessage("hi"), new AssistantMessage { Usage = new(100, 0) }, new UserMessage("tail") }));
        Assert.Equal(4_294_967_294L, TokenEstimation.CalculateContextTokens(new(int.MaxValue, int.MaxValue)));
    }

    /// <summary>思考分配区分缺省回答预算与显式预算，并在需要时缩减思考。</summary>
    /// <param name="baseTokens">显式回答预算。</param>
    /// <param name="modelTokens">模型上限。</param>
    /// <param name="thinking">初始思考预算。</param>
    /// <param name="expectedMax">预期总上限。</param>
    /// <param name="expectedThinking">预期思考预算。</param>
    [Theory]
    [InlineData(null, 4_096, 16_384, 4_096, 3_072)]
    [InlineData(null, 20_000, 16_384, 20_000, 16_384)]
    [InlineData(512, 4_096, 1_024, 1_536, 1_024)]
    [InlineData(100, 512, 1_024, 512, 0)]
    [InlineData(int.MaxValue, int.MaxValue, 16_384, int.MaxValue, 16_384)]
    public void Thinking_AdjustsWithinModel(int? baseTokens, int modelTokens, int thinking, int expectedMax, int expectedThinking) =>
        Assert.Equal((expectedMax, expectedThinking), SimpleTokenOptions.AdjustMaxTokensForThinking(baseTokens, modelTokens, thinking));

    /// <summary>未知窗口保留请求上限，已耗尽的窗口只留下最小输出。</summary>
    /// <param name="window">模型窗口。</param>
    /// <param name="maxTokens">请求上限。</param>
    /// <param name="expected">预期上限。</param>
    [Theory]
    [InlineData(null, 8_000, 8_000)]
    [InlineData(0, 0, 1)]
    [InlineData(-1, -10, 1)]
    [InlineData(4_096, 8_000, 1)]
    [InlineData(5_096, 8_000, 999)]
    public void ContextClamp_HandlesUnknownAndExhaustedWindows(int? window, int maxTokens, int expected) => Assert.Equal(expected,
        SimpleTokenOptions.ClampMaxTokensToContext(Model(window), new LlmContext { Messages = [new UserMessage("hi")] }, maxTokens));

    /// <summary>创建指定窗口的测试模型。</summary>
    /// <param name="window">模型上下文窗口。</param>
    /// <returns>测试模型。</returns>
    private static Model Model(int? window) => new() { Id = "test", Name = "test", Api = "anthropic-messages", Provider = "anthropic", ContextWindow = window };

    /// <summary>将测试毫秒数转为时间。</summary>
    /// <param name="milliseconds">Unix 毫秒。</param>
    /// <returns>消息时间。</returns>
    private static DateTimeOffset Time(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
}
