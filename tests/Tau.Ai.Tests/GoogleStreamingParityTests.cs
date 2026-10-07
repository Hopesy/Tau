// 作者：xxx
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Google;

namespace Tau.Ai.Tests;

public sealed partial class GoogleHistoryReplayTests
{
    /// <summary>【Google】【签名生命周期】签名更新只影响当前块，完整工具调用立即结束并保留终态字段。</summary>
    /// <param name="api">协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task StreamRetainsLatestSignatureAndIndependentBlocks(string api)
    {
        var events = await StreamAsync(api,
        [
            """{"responseId":"resp-first","candidates":[{"content":{"parts":[{"text":"a","thought":true,"thoughtSignature":"YQ=="}]}}]}""",
            """{"responseId":"resp-later","candidates":[{"content":{"parts":[{"text":"b","thought":true,"thoughtSignature":"Yg=="},{"text":"","thought":true,"thoughtSignature":""},{"text":"c","thought":true}]}}]}""",
            """{"candidates":[{"content":{"parts":[{"text":"answer","thoughtSignature":"Yw=="},{"text":"!","thoughtSignature":"ZA=="},{"functionCall":{"id":"tool","name":"read","args":null},"thoughtSignature":"dA=="}]}}]}""",
            """{"usageMetadata":{"promptTokenCount":10,"cachedContentTokenCount":3,"candidatesTokenCount":4,"thoughtsTokenCount":2,"totalTokenCount":99},"candidates":[{"finishReason":"STOP"}]}"""
        ]);
        var done = Assert.Single(events.OfType<DoneEvent>()).Message;
        Assert.Equal("resp-first", done.ResponseId);
        Assert.Equal("abc", Assert.IsType<ThinkingContent>(done.Content[0]).Thinking);
        Assert.Equal("Yg==", Assert.Single(events.OfType<ThinkingEndEvent>()).ContentSignature);
        var end = Assert.Single(events.OfType<TextEndEvent>());
        Assert.Equal("answer!", end.Content);
        Assert.Equal("ZA==", end.ContentSignature);
        var tool = Assert.Single(events.OfType<ToolCallEndEvent>());
        Assert.Equal("{}", tool.ToolCall!.Arguments);
        Assert.Equal("dA==", tool.ToolCall.ThoughtSignature);
        Assert.Equal(0, tool.Partial.Usage!.Value.InputTokens);
        Assert.Equal(StopReason.ToolUse, done.StopReason);
        Assert.Equal(7, done.Usage!.Value.InputTokens);
        Assert.Equal(6, done.Usage.Value.OutputTokens);
        Assert.Equal(2, done.Usage.Value.ReasoningTokens);
        Assert.Equal(99, done.Usage.Value.TotalTokens);
        Assert.Equal(0.000028m, done.Usage.Value.Cost!.Value.Total);
        Assert.Empty(Assert.Single(events.OfType<StartEvent>()).Partial.Content);
        var replay = await SendAsync(Model(api), new() { Messages = [done] });
        Assert.Equal("Yg==", replay.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("ZA==", replay.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("thoughtSignature").GetString());
    }

    /// <summary>【Google】【终态优先级】只有正常结束可转换为工具调用，错误和长度上限保留原意并读取尾部用量。</summary>
    /// <param name="api">协议。</param><param name="tool">是否包含工具调用。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language", false)]
    [InlineData("google-generative-language", true)]
    [InlineData("google-vertex", false)]
    [InlineData("google-vertex", true)]
    [InlineData("google-gemini-cli", false)]
    [InlineData("google-gemini-cli", true)]
    public async Task StreamFinishReasonDoesNotHideFailures(string api, bool tool)
    {
        foreach (var reason in new string?[] { "STOP", "MAX_TOKENS", "SAFETY", "MALFORMED_FUNCTION_CALL", null, "" })
        {
            var part = tool ? """{"functionCall":{"id":"call","name":"read","args":{}}}""" : """{"text":"partial"}""";
            var finish = reason is null ? "" : ",\"finishReason\":\"" + reason + "\"";
            var events = await StreamAsync(api, ["{\"candidates\":[{\"content\":{\"parts\":[" + part + "]}" + finish + "}]}",
                """{"usageMetadata":{"promptTokenCount":20,"cachedContentTokenCount":4,"candidatesTokenCount":2,"totalTokenCount":123}}"""]);
            var failure = reason is not ("STOP" or "MAX_TOKENS");
            var message = failure ? Assert.Single(events.OfType<ErrorEvent>()).Message! : Assert.Single(events.OfType<DoneEvent>()).Message;
            Assert.Equal(16, message.Usage!.Value.InputTokens);
            Assert.Equal(4, message.Usage.Value.CacheReadTokens);
            Assert.Equal(123, message.Usage.Value.TotalTokens);
            Assert.Equal(failure ? StopReason.Error : reason == "MAX_TOKENS" ? StopReason.MaxTokens : tool ? StopReason.ToolUse : StopReason.EndTurn, message.StopReason);
            Assert.Equal(!failure && !tool && reason == "STOP", message.EndTurn);
            if (failure)
            {
                Assert.Empty(events.OfType<DoneEvent>());
                if (string.IsNullOrEmpty(reason)) Assert.Contains("stream ended without a finish reason", message.ErrorMessage);
                else Assert.Equal("Provider stopped with: " + reason, message.ErrorMessage);
            }
        }
    }

    /// <summary>【Google】【调用去重】缺失和重复 ID 全局生成唯一标识，合法空白 ID 按主线原样保留。</summary>
    /// <param name="api">协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task StreamToolIdsAreUniqueAcrossRequests(string api)
    {
        const string chunk = """{"candidates":[{"content":{"parts":[{"functionCall":{"id":"same","name":"read","args":{}}},{"functionCall":{"id":"same","name":"read","args":{}}},{"functionCall":{"name":"read","args":{}}},{"functionCall":{"id":" ","name":"read","args":{}}}]},"finishReason":"STOP"}]}""";
        var generated = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 2; index++)
        {
            var events = await StreamAsync(api, [chunk]);
            var calls = Assert.Single(events.OfType<DoneEvent>()).Message.Content.OfType<ToolCallContent>().ToArray();
            Assert.Equal("same", calls[0].Id);
            Assert.Equal(" ", calls[3].Id);
            foreach (var call in calls.Skip(1).Take(2))
            {
                Assert.Matches("^read_[0-9]+_[0-9]+$", call.Id);
                Assert.True(generated.Add(call.Id));
            }
            Assert.Equal(4, events.OfType<ToolCallEndEvent>().Count());
        }
    }

    /// <summary>【Google】【中断快照】取消、观察器异常及损坏 JSON 都保留已经接收的正文和用量。</summary>
    /// <param name="api">协议。</param><param name="failure">失败类型。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language", "cancel")]
    [InlineData("google-generative-language", "observer")]
    [InlineData("google-generative-language", "json")]
    [InlineData("google-vertex", "cancel")]
    [InlineData("google-vertex", "observer")]
    [InlineData("google-vertex", "json")]
    [InlineData("google-gemini-cli", "cancel")]
    [InlineData("google-gemini-cli", "observer")]
    [InlineData("google-gemini-cli", "json")]
    public async Task StreamFailuresPreservePartialMessage(string api, string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var observed = 0;
        var options = new StreamOptions { Signal = cancellation.Token, OnProviderStreamEvent = (_, _) =>
        {
            if (++observed == 2)
            {
                if (failure == "cancel") cancellation.Cancel();
                if (failure == "observer") throw new InvalidOperationException("observer fixture");
            }
            return ValueTask.CompletedTask;
        } };
        var events = await StreamAsync(api, ["""{"responseId":"partial-id","candidates":[{"content":{"parts":[{"text":"kept"}]}}],"usageMetadata":{"promptTokenCount":5,"totalTokenCount":5}}""",
            failure == "json" ? "{bad-json}" : """{"candidates":[{"finishReason":"STOP"}]}"""], options);
        var error = Assert.Single(events.OfType<ErrorEvent>()).Message!;
        Assert.Empty(events.OfType<DoneEvent>());
        Assert.Equal("kept", Assert.IsType<TextContent>(Assert.Single(error.Content)).Text);
        Assert.Equal("partial-id", error.ResponseId);
        Assert.Equal(5, error.Usage!.Value.InputTokens);
        Assert.Equal(Model(api).Id, error.Model);
        Assert.Equal(api, error.Api);
        Assert.Equal(failure == "cancel" ? StopReason.Aborted : StopReason.Error, error.StopReason);
        Assert.False(error.EndTurn);
    }

    /// <summary>【Google】【流夹具】通过实际三种 HTTP 提供方读取合成 SSE，CLI 自动包装响应。</summary>
    /// <param name="api">协议。</param><param name="chunks">原生 JSON 分片。</param><param name="options">可选回调与取消选项。</param>
    /// <returns>完整事件序列。</returns>
    private static async Task<List<StreamEvent>> StreamAsync(string api, IReadOnlyList<string> chunks, StreamOptions? options = null)
    {
        var sse = string.Join("", chunks.Select(chunk => "data: " + (api == "google-gemini-cli" ? "{\"response\":" + chunk + "}" : chunk) + "\n\n"));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(sse));
        using var client = new HttpClient(handler);
        IStreamProvider provider = api switch { "google-vertex" => new GoogleVertexProvider(client), "google-gemini-cli" => new GoogleGeminiCliProvider(client), _ => new GoogleProvider(client) };
        var model = Model(api) with { Cost = new ModelCost(1m, 2m, 3m, 4m) };
        return await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(model, new() { Messages = [new UserMessage("hello")] }, (options ?? new StreamOptions()) with
        { ApiKey = api == "google-gemini-cli" ? """{"token":"synthetic","projectId":"fixture"}""" : "synthetic" }));
    }
}
