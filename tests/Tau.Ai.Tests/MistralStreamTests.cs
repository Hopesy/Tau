// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers.Mistral;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Mistral 流式】验证专用内容数组、交错调用及错误保留。</summary>
public sealed class MistralStreamTests
{
    /// <summary>【AI】【交错事件】空文本不切割思考，工具按槽位累积，最终分片完成所有块。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task StructuredContentAndInterleavedToolsKeepLifecycles()
    {
        var capture = await ReadAsync([
            """{"id":"","choices":[{"delta":{"content":""}}]}""",
            """{"id":"response","choices":[{"delta":{"content":[{"type":"thinking","thinking":[{"text":"a"},{"text":""},{"text":"b"}]}]}}]}""",
            """{"id":"later","choices":[{"delta":{"content":["",{"type":"text","text":""},{"type":"thinking","thinking":[{"text":"c"}]}]}}]}""",
            """{"choices":[{"delta":{"content":["x",{"type":"text","text":"y"}]}}]}""",
            """{"choices":[{"delta":{"content":[{"type":"thinking","thinking":[{"text":"d"}]}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"null","function":{"name":"first","arguments":"{\"a\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"second","function":{"name":"second","arguments":{"b":2}}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"ignored","function":{"arguments":"1}"}}]}}]}""",
            """{"choices":[{"delta":{"content":"after"},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":3,"prompt_tokens_details":{"cached_tokens":4}}}"""
        ]);
        var result = capture.Message;
        Assert.Equal(StopReason.ToolUse, result.StopReason);
        Assert.Equal("response", result.ResponseId);
        Assert.Equal(["thinking", "text", "thinking", "toolCall", "toolCall", "text"], result.Content.Select(block => block.Type));
        Assert.Equal("abc", Assert.IsType<ThinkingContent>(result.Content[0]).Thinking);
        Assert.Equal("xy", Assert.IsType<TextContent>(result.Content[1]).Text);
        Assert.Equal("d", Assert.IsType<ThinkingContent>(result.Content[2]).Thinking);
        var first = Assert.IsType<ToolCallContent>(result.Content[3]);
        Assert.Equal("toolcall0", first.Id);
        Assert.Equal("{\"a\":1}", first.Arguments);
        Assert.Equal("{\"b\":2}", Assert.IsType<ToolCallContent>(result.Content[4]).Arguments);
        Assert.Equal("after", Assert.IsType<TextContent>(result.Content[5]).Text);
        Assert.Equal(2, capture.Events.OfType<ThinkingStartEvent>().Count());
        Assert.Equal(2, capture.Events.OfType<ThinkingEndEvent>().Count());
        Assert.Equal(2, capture.Events.OfType<TextStartEvent>().Count());
        Assert.Equal(2, capture.Events.OfType<TextEndEvent>().Count());
        Assert.Equal([3, 4], capture.Events.OfType<ToolCallEndEvent>().Select(item => item.ContentIndex));
        Assert.Equal([3, 4, 3], capture.Events.OfType<ToolCallDeltaEvent>().Select(item => item.ContentIndex));
        Assert.Equal("abc", Assert.IsType<ThinkingContent>(capture.Events.OfType<ThinkingEndEvent>().First().Partial.Content[0]).Thinking);
        Assert.Equal(6, result.Usage!.Value.InputTokens);
        Assert.Equal(4, result.Usage.Value.CacheReadTokens);
        Assert.Equal(13, result.Usage.Value.TotalTokens);
        Assert.Single(capture.Events.OfType<DoneEvent>());
    }

    /// <summary>【AI】【缓存用量】兼容六种字段及优先级，非法计数回退零，缓存计数限定在输入范围内。</summary>
    /// <param name="fields">用量附加字段。</param><param name="cached">期望缓存数。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("\"promptTokensDetails\":{\"cachedTokens\":4}", 4)]
    [InlineData("\"prompt_tokens_details\":{\"cached_tokens\":4}", 4)]
    [InlineData("\"promptTokenDetails\":{\"cachedTokens\":4}", 4)]
    [InlineData("\"prompt_token_details\":{\"cached_tokens\":4}", 4)]
    [InlineData("\"numCachedTokens\":4", 4)]
    [InlineData("\"num_cached_tokens\":4", 4)]
    [InlineData("\"numCachedTokens\":40", 10)]
    [InlineData("\"numCachedTokens\":-3", 0)]
    [InlineData("\"promptTokensDetails\":{\"cachedTokens\":2},\"numCachedTokens\":8", 2)]
    [InlineData("\"promptTokensDetails\":{\"cachedTokens\":null},\"numCachedTokens\":8", 8)]
    [InlineData("\"promptTokensDetails\":{\"cachedTokens\":\"bad\"},\"numCachedTokens\":8", 0)]
    public async Task TrailingUsageKeepsCacheCountsAndCost(string fields, int cached)
    {
        var capture = await ReadAsync([
            """{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""",
            "{\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3,\"total_tokens\":99," + fields + "}}"
        ]);
        var usage = capture.Message.Usage!.Value;
        Assert.Equal(10 - cached, usage.InputTokens);
        Assert.Equal(cached, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(99, usage.TotalTokens);
        Assert.Equal(((10 - cached) * 2m + cached + 9m) / 1_000_000m, usage.Cost!.Value.Total);
        Assert.Equal(2, capture.Observed);
    }

    /// <summary>【AI】【结束原因】正常与长度终止成功，未知或缺少结束原因必须失败并保留内容。</summary>
    /// <param name="reason">供应商结束原因。</param><param name="expected">期望状态。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("stop", StopReason.EndTurn)]
    [InlineData("length", StopReason.MaxTokens)]
    [InlineData("model_length", StopReason.MaxTokens)]
    [InlineData("tool_calls", StopReason.ToolUse)]
    [InlineData("error", StopReason.Error)]
    [InlineData("content_filter", StopReason.Error)]
    [InlineData("unknown", StopReason.Error)]
    [InlineData(null, StopReason.Error)]
    public async Task FinishReasonControlsTerminalEvent(string? reason, StopReason expected)
    {
        var finish = reason is null ? "" : ",\"finish_reason\":\"" + reason + "\"";
        var capture = await ReadAsync(["{\"choices\":[{\"delta\":{\"content\":\"partial\"}" + finish + "}]}"]);
        Assert.Equal(expected, capture.Message.StopReason);
        Assert.Equal(reason, capture.Message.RawStopReason);
        Assert.Equal("partial", Assert.IsType<TextContent>(Assert.Single(capture.Message.Content)).Text);
        Assert.Equal(expected == StopReason.Error ? 1 : 0, capture.Events.OfType<ErrorEvent>().Count());
        Assert.Equal(expected == StopReason.Error ? 0 : 1, capture.Events.OfType<DoneEvent>().Count());
        if (expected == StopReason.Error) Assert.Contains(reason is null ? "without a finish reason" : "Provider stopped with: " + reason, capture.Message.ErrorMessage);
    }

    /// <summary>【AI】【取消快照】原始事件观察器取消后保留之前的正文、标识和用量，禁止成功终态。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CancellationPreservesPartialMessage()
    {
        using var cancellation = new CancellationTokenSource();
        var capture = await ReadAsync([
            """{"id":"response","choices":[{"delta":{"content":"partial"}}],"usage":{"prompt_tokens":4,"completion_tokens":2}}""",
            """{"choices":[{"delta":{"content":"ignored"},"finish_reason":"stop"}]}"""
        ], cancellation, index => { if (index == 2) cancellation.Cancel(); });
        Assert.Equal(StopReason.Aborted, capture.Message.StopReason);
        Assert.Equal("response", capture.Message.ResponseId);
        Assert.Equal("partial", Assert.IsType<TextContent>(Assert.Single(capture.Message.Content)).Text);
        Assert.Equal(4, capture.Message.Usage!.Value.InputTokens);
        Assert.Single(capture.Events.OfType<ErrorEvent>());
        Assert.Empty(capture.Events.OfType<DoneEvent>());
    }

    /// <summary>【AI】【损坏流】JSON 解析失败保留前面已接收的思考和部分工具，未完成工具不发送结束事件。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BrokenChunkPreservesEarlierBlocks()
    {
        var capture = await ReadAsync([
            """{"choices":[{"delta":{"content":[{"type":"thinking","thinking":[{"text":"plan"}]}],"tool_calls":[{"index":null,"function":{"name":"read","arguments":"{\"path\":\"par"}}]}}]}""",
            "{broken"
        ]);
        Assert.Equal(StopReason.Error, capture.Message.StopReason);
        Assert.Equal("plan", Assert.IsType<ThinkingContent>(capture.Message.Content[0]).Thinking);
        Assert.Equal("toolcall0", Assert.IsType<ToolCallContent>(capture.Message.Content[1]).Id);
        Assert.Empty(capture.Events.OfType<ToolCallEndEvent>());
        Assert.Single(capture.Events.OfType<ErrorEvent>());
    }

    /// <summary>【AI】【SSE 夹具】经真实 HTTP/SSE 解析，记录原始事件观察次数和最终快照。</summary>
    /// <param name="chunks">JSON 分片。</param><param name="cancellation">取消源。</param><param name="observe">可选观察回调。</param>
    /// <returns>消息、事件和观察数。</returns>
    private static async Task<(AssistantMessage Message, List<StreamEvent> Events, int Observed)> ReadAsync(string[] chunks,
        CancellationTokenSource? cancellation = null, Action<int>? observe = null)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(string.Concat(chunks.Select(chunk => "data: " + chunk + "\n\n")) + "data: [DONE]\n\n"));
        using var client = new HttpClient(handler);
        var observed = 0;
        var model = new Model { Id = "model", Name = "Model", Provider = "mistral", Api = "mistral-conversations", BaseUrl = "https://example.invalid", Cost = new(2m, 3m, 1m) };
        var stream = new MistralProvider(client).Stream(model, new() { Messages = [new UserMessage("hi")] }, new()
        {
            ApiKey = "synthetic", Signal = cancellation?.Token ?? default,
            OnProviderStreamEvent = (_, _) => { observed++; observe?.Invoke(observed); return ValueTask.CompletedTask; }
        });
        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);
        return (await stream.ResultAsync, events, observed);
    }
}
