// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;

namespace Tau.Ai.Tests;

/// <summary>【AI】【思考流式】验证 SSE 增量去重、稳定内容索引和签名再请求。</summary>
public sealed class OpenAiReasoningStreamTests
{
    /// <summary>【AI】【字段选择】只读取第一个非空兼容思考字段，并保存准确回放名称。</summary>
    /// <param name="provider">提供方名。</param><param name="delta">供应商分片。</param>
    /// <param name="signature">预期字段名。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("local", """{"reasoning_content":"thought","reasoning":"duplicate","reasoning_text":"duplicate"}""", "reasoning_content")]
    [InlineData("local", """{"reasoning_content":"","reasoning":"thought","reasoning_text":"duplicate"}""", "reasoning")]
    [InlineData("local", """{"reasoning_content":null,"reasoning":"","reasoning_text":"thought"}""", "reasoning_text")]
    [InlineData("opencode-go", """{"reasoning":"thought"}""", "reasoning_content")]
    public async Task FirstNonemptyReasoningFieldWinsAndRoundTrips(string provider, string delta, string signature)
    {
        var (message, events) = await ReadAsync(provider, false, delta, """{"content":"answer"}""");
        var thinking = Assert.Single(message.Content.OfType<ThinkingContent>());
        Assert.Equal("thought", thinking.Thinking);
        Assert.Equal(signature, thinking.ThinkingSignature);
        Assert.Single(events.OfType<ThinkingDeltaEvent>());
        Assert.Equal(signature, Assert.Single(events.OfType<ThinkingEndEvent>()).ContentSignature);
        using var body = await ReplayAsync(provider, message);
        var assistant = body.RootElement.GetProperty("messages")[0];
        Assert.Equal("thought", assistant.GetProperty(signature).GetString());
        Assert.Equal("answer", assistant.GetProperty("content").GetString());
    }

    /// <summary>【AI】【交错索引】同一分片正文先于思考，后续增量复用原块，工具参数仍按调用索引累积。</summary>
    /// <param name="compatible">兼容别名实现。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterleavedContentUsesOneStableBlockPerTextType(bool compatible)
    {
        var (message, events) = await ReadAsync("local", compatible,
            """{"content":"a","reasoning":"x"}""",
            """{"tool_calls":[{"index":0,"id":"call","type":"function","function":{"name":"read","arguments":"{"}}]}""",
            """{"content":"b","reasoning":"y"}""",
            """{"tool_calls":[{"index":0,"function":{"arguments":"}"}}]}""");
        Assert.Equal(["text", "thinking", "toolCall"], message.Content.Select(item => item.Type));
        Assert.Equal("ab", Assert.IsType<TextContent>(message.Content[0]).Text);
        Assert.Equal("xy", Assert.IsType<ThinkingContent>(message.Content[1]).Thinking);
        Assert.Equal("{}", Assert.IsType<ToolCallContent>(message.Content[2]).Arguments);
        Assert.Single(events.OfType<TextStartEvent>());
        Assert.Single(events.OfType<ThinkingStartEvent>());
        Assert.Single(events.OfType<TextEndEvent>());
        Assert.Single(events.OfType<ThinkingEndEvent>());
        Assert.All(events.OfType<TextDeltaEvent>(), item => Assert.Equal(0, item.ContentIndex));
        Assert.All(events.OfType<ThinkingDeltaEvent>(), item => Assert.Equal(1, item.ContentIndex));
    }

    /// <summary>【AI】【结构分片】连续 summary/text 合并并补齐缺省公共字段，encrypted 始终保持独立。</summary>
    /// <param name="compatible">兼容别名实现。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredDetailsMergeWithoutDuplicatingVisibleThinking(bool compatible)
    {
        var (message, events) = await ReadAsync("local", compatible,
            """{"reasoning_details":[{"type":"reasoning.summary","summary":"a","id":null,"format":""}]}""",
            """{"reasoning_details":[{"type":"reasoning.summary","summary":"b","id":"s","format":"provider","index":0}]}""",
            """{"reasoning":"visible","reasoning_details":[{"type":"reasoning.summary","summary":"c","id":"later","format":"later","index":2},{"type":"reasoning.text","text":"x","signature":"","id":null}]}""",
            """{"reasoning_details":[{"type":"reasoning.text","text":"y","signature":"sig","id":"t","format":"f","index":1},{"type":"reasoning.encrypted","id":"c","data":"one"},{"type":"reasoning.encrypted","id":"d","data":"two"},{},{"type":"reasoning.summary","summary":123}]}""",
            """{"content":"answer"}""");
        var thinking = Assert.Single(message.Content.OfType<ThinkingContent>());
        Assert.Equal("visible", thinking.Thinking);
        Assert.Single(events.OfType<ThinkingStartEvent>());
        Assert.Single(events.OfType<ThinkingDeltaEvent>());
        using var details = JsonDocument.Parse(thinking.ThinkingSignature!);
        var array = details.RootElement;
        Assert.Equal(4, array.GetArrayLength());
        Assert.Equal("abc", array[0].GetProperty("summary").GetString());
        Assert.Equal("s", array[0].GetProperty("id").GetString());
        Assert.Equal("provider", array[0].GetProperty("format").GetString());
        Assert.Equal(0, array[0].GetProperty("index").GetInt32());
        Assert.Equal("xy", array[1].GetProperty("text").GetString());
        Assert.Equal("sig", array[1].GetProperty("signature").GetString());
        Assert.Equal("t", array[1].GetProperty("id").GetString());
        Assert.Equal("f", array[1].GetProperty("format").GetString());
        Assert.Equal(1, array[1].GetProperty("index").GetInt32());
        Assert.Equal("one", array[2].GetProperty("data").GetString());
        Assert.Equal("two", array[3].GetProperty("data").GetString());
        Assert.Equal(thinking.ThinkingSignature, Assert.Single(events.OfType<ThinkingEndEvent>()).ContentSignature);
        using var request = await ReplayAsync("local", message);
        var assistant = request.RootElement.GetProperty("messages")[0];
        Assert.True(JsonElement.DeepEquals(array, assistant.GetProperty("reasoning_details")));
        Assert.False(assistant.TryGetProperty("reasoning", out _));
    }

    /// <summary>【AI】【空分片】空文本、空思考和无效明细不会创建无意义内容块或生命周期事件。</summary>
    [Fact]
    public async Task EmptyAndInvalidDeltasDoNotCreateBlocks()
    {
        var (message, events) = await ReadAsync("local", false,
            """{"content":"","reasoning_content":"","reasoning":"","reasoning_text":"","reasoning_details":[null,{},{"type":"reasoning.text","text":123}]}""");
        Assert.Empty(message.Content);
        Assert.Empty(events.OfType<TextStartEvent>());
        Assert.Empty(events.OfType<ThinkingStartEvent>());
        Assert.Empty(events.OfType<ThinkingDeltaEvent>());
        Assert.Equal(StopReason.EndTurn, message.StopReason);
    }

    /// <summary>【AI】【流夹具】将固定增量包装为 SSE，经过真实提供方获取最终消息和事件。</summary>
    /// <param name="providerName">提供方名。</param><param name="compatible">兼容别名。</param><param name="deltas">delta JSON 序列。</param>
    /// <returns>最终消息与全部事件。</returns>
    private static async Task<(AssistantMessage Message, List<StreamEvent> Events)> ReadAsync(string providerName, bool compatible, params string[] deltas)
    {
        var sse = string.Concat(deltas.Select(delta => "data: {\"choices\":[{\"delta\":" + delta + "}]}\n\n")) +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(sse));
        using var client = new HttpClient(handler);
        IStreamProvider provider = compatible ? new OpenAiCompatibleProvider("openai-chat-completions", "https://example.invalid/v1", httpClient: client) : new OpenAiProvider(client);
        var stream = provider.Stream(Model(providerName), new() { Messages = [new UserMessage("hi")] }, new() { ApiKey = "synthetic" });
        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);
        return (await stream.ResultAsync, events);
    }

    /// <summary>【AI】【再请求】将刚收到的真实助手消息发送到同模型，检查回放签名的协议字段。</summary>
    /// <param name="providerName">同模型提供方。</param><param name="message">已完成响应。</param><returns>待释放请求 JSON。</returns>
    private static async Task<JsonDocument> ReplayAsync(string providerName, AssistantMessage message)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        await OpenAiResponsesProviderTests.CollectAsync(new OpenAiProvider(client).Stream(Model(providerName),
            new() { Messages = [message, new UserMessage("continue")] }, new() { ApiKey = "synthetic", MaxRetries = 0 }));
        Assert.Single(handler.Requests);
        return JsonDocument.Parse(handler.CapturedBody);
    }

    /// <summary>【AI】【模型夹具】保证首轮和重放使用相同身份。</summary>
    /// <param name="provider">提供方名。</param><returns>固定模型。</returns>
    private static Model Model(string provider) => new()
    {
        Id = "model", Name = "Model", Provider = provider, Api = "openai-chat-completions", BaseUrl = "https://example.invalid/v1"
    };
}
