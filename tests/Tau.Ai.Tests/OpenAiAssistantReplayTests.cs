// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;

namespace Tau.Ai.Tests;

/// <summary>【AI】【助手重放】验证正文格式、原始思考字段及结构化签名优先级。</summary>
public sealed class OpenAiAssistantReplayTests
{
    /// <summary>【AI】【原始思考字段】同模型保留三个已知字段，并兼容 opencode-go 的字段别名。</summary>
    /// <param name="provider">提供方。</param><param name="signature">源字段签名。</param><param name="expected">目标字段名。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("local", "reasoning", "reasoning")]
    [InlineData("local", "reasoning_content", "reasoning_content")]
    [InlineData("local", "reasoning_text", "reasoning_text")]
    [InlineData("opencode-go", "reasoning", "reasoning_content")]
    public async Task RawReasoningFieldsSurviveReplay(string provider, string signature, string expected)
    {
        foreach (var compatible in new[] { false, true })
        {
            using var body = await CaptureAsync(provider, compatible,
                [Assistant([new ThinkingContent("first") { ThinkingSignature = signature }, new ThinkingContent("second"), new TextContent("answer")], provider)],
                new() { RequiresReasoningContentOnAssistantMessages = true });
            var assistant = body.RootElement.GetProperty("messages")[0];
            Assert.Equal("answer", assistant.GetProperty("content").GetString());
            Assert.Equal("first\nsecond", assistant.GetProperty(expected).GetString());
            if (expected != "reasoning_content") Assert.Equal("", assistant.GetProperty("reasoning_content").GetString());
            Assert.False(assistant.TryGetProperty("reasoning_details", out _));
        }
    }

    /// <summary>【AI】【思考转正文】多个思考块以双换行汇总，助手文本仍保留各自块边界并移除纯空白。</summary>
    /// <param name="compatible">兼容别名。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThinkingAsTextPreservesContentBlocks(bool compatible)
    {
        using var body = await CaptureAsync("local", compatible,
            [Assistant([new ThinkingContent("first"), new ThinkingContent("second"), new TextContent(" "),
                new TextContent("answer-1"), new TextContent("answer-2")])], new() { RequiresThinkingAsText = true });
        var assistant = body.RootElement.GetProperty("messages")[0];
        var parts = assistant.GetProperty("content");
        Assert.Equal(["first\n\nsecond", "answer-1", "answer-2"], parts.EnumerateArray().Select(item => item.GetProperty("text").GetString()));
        Assert.All(parts.EnumerateArray(), part => Assert.Equal("text", part.GetProperty("type").GetString()));
        Assert.False(assistant.TryGetProperty("reasoning", out _));
    }

    /// <summary>【AI】【明细优先级】有效思考明细优先于工具旧签名及原始字段。</summary>
    [Fact]
    public async Task StructuredDetailsOverrideLegacyAndRawReasoning()
    {
        const string details = """[{"type":"reasoning.summary","summary":"summary","id":null,"index":1,"future":true}]""";
        using var body = await CaptureAsync("local", false, [
            Assistant([
                new ThinkingContent("first") { ThinkingSignature = "reasoning" },
                new ThinkingContent("") { ThinkingSignature = details },
                new ToolCallContent("a", "read", "{}") { ThoughtSignature = """{"type":"reasoning.encrypted","id":"legacy","data":"cipher"}""" },
                new TextContent("answer")
            ]), new ToolResultMessage("a", [new TextContent("done")])
        ]);
        var assistant = body.RootElement.GetProperty("messages")[0];
        using var expected = JsonDocument.Parse(details);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, assistant.GetProperty("reasoning_details")));
        Assert.False(assistant.TryGetProperty("reasoning", out _));
    }

    /// <summary>【AI】【旧签名】旧工具签名只接受非空 ID 和密文的 encrypted 明细。</summary>
    /// <param name="signature">签名 JSON。</param><param name="valid">是否应重放。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("""{"type":"reasoning.encrypted","id":"a","data":"cipher"}""", true)]
    [InlineData("""{"type":"reasoning.encrypted","id":"","data":"cipher"}""", false)]
    [InlineData("""{"type":"reasoning.encrypted","id":"a","data":""}""", false)]
    [InlineData("""{"type":"reasoning.encrypted","data":"cipher"}""", false)]
    [InlineData("""{"type":"reasoning.text","id":"a","text":"text"}""", false)]
    [InlineData("broken-json", false)]
    public async Task LegacyToolSignaturesRequireUsableEncryptedDetail(string signature, bool valid)
    {
        using var body = await CaptureAsync("local", true, [
            Assistant([new ToolCallContent("a", "read", "{}") { ThoughtSignature = signature }]),
            new ToolResultMessage("a", [new TextContent("done")])
        ]);
        var assistant = body.RootElement.GetProperty("messages")[0];
        Assert.Equal(valid, assistant.TryGetProperty("reasoning_details", out var details));
        if (valid) Assert.Equal("cipher", details[0].GetProperty("data").GetString());
    }

    /// <summary>【AI】【结构校验】无效数组、字段类型及单对象不能作为思考签名回放。</summary>
    /// <param name="signature">思考签名 JSON。</param><param name="valid">是否有效。</param>
    [Theory]
    [InlineData("""[{"type":"reasoning.text","text":"x","signature":null}]""", true)]
    [InlineData("""[{"type":"reasoning.summary","summary":"","id":null,"format":"x","index":0.5}]""", true)]
    [InlineData("""[{"type":"reasoning.encrypted","data":""}]""", true)]
    [InlineData("[]", false)]
    [InlineData("""{"type":"reasoning.text","text":"x"}""", false)]
    [InlineData("""[{"type":"unknown","text":"x"}]""", false)]
    [InlineData("""[{"type":"reasoning.text","text":null}]""", false)]
    [InlineData("""[{"type":"reasoning.text","text":"x","signature":123}]""", false)]
    [InlineData("""[{"type":"reasoning.text","text":"x","id":123}]""", false)]
    [InlineData("""[{"type":"reasoning.text","text":"x","format":null}]""", false)]
    [InlineData("""[{"type":"reasoning.text","text":"x","index":"1"}]""", false)]
    [InlineData("""[{"type":"reasoning.text","text":"x"},{}]""", false)]
    public void ReasoningArraysRequireEveryDetailToBeValid(string signature, bool valid) =>
        Assert.Equal(valid, OpenAiReasoningDetails.ParseArray(signature).HasValue);

    /// <summary>【AI】【空助手】无正文且无工具调用的助手省略，工具调用保留协议要求的 null 或空正文。</summary>
    /// <param name="bridge">是否要求空字符串正文。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAssistantsAreSkippedAndToolCallsKeepContentShape(bool bridge)
    {
        using var body = await CaptureAsync("local", false, [
            new UserMessage([]), new UserMessage("start"), Assistant([]), Assistant([new TextContent(" \n")]),
            Assistant([new ThinkingContent("only reasoning") { ThinkingSignature = "reasoning" }]),
            Assistant([new ToolCallContent("a", "read", "{}")]), new ToolResultMessage("a", [])
        ], new() { RequiresAssistantAfterToolResult = bridge });
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(["user", "assistant", "tool"], messages.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        var content = messages[1].GetProperty("content");
        if (bridge) Assert.Equal("", content.GetString());
        else Assert.Equal(JsonValueKind.Null, content.ValueKind);
    }

    /// <summary>【AI】【助手夹具】为同模型历史设置完整来源身份，确保真正验证签名回放。</summary>
    /// <param name="content">历史内容。</param><param name="provider">提供方。</param><returns>来源明确的助手消息。</returns>
    private static AssistantMessage Assistant(IReadOnlyList<ContentBlock> content, string provider = "local") => new(content)
    {
        Provider = provider, Api = "openai-chat-completions", Model = "model"
    };

    /// <summary>【AI】【报文捕获】捕获真实提供方的助手历史转换结果。</summary>
    /// <param name="providerName">目标提供方。</param><param name="compatible">兼容别名。</param>
    /// <param name="messages">历史消息。</param><param name="compat">可选兼容设置。</param><returns>待释放请求 JSON。</returns>
    private static async Task<JsonDocument> CaptureAsync(string providerName, bool compatible, IReadOnlyList<ChatMessage> messages, ModelCompatibility? compat = null)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = compatible ? new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client) : new OpenAiProvider(client);
        var model = new Model
        {
            Id = "model", Name = "Model", Provider = providerName, Api = "openai-chat-completions", Reasoning = true,
            BaseUrl = "https://example.invalid/v1", Compat = compat
        };
        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(model, new() { Messages = messages },
            new() { ApiKey = "synthetic", CacheRetention = CacheRetention.None, MaxRetries = 0 }));
        Assert.True(handler.Requests.Count == 1, string.Join("; ", events.OfType<ErrorEvent>().Select(item => item.Error)));
        return JsonDocument.Parse(handler.CapturedBody);
    }
}
