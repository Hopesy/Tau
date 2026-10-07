// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;
using Tau.Ai.Utilities;

namespace Tau.Ai.Tests;

/// <summary>【AI】【跨模型重放】验证签名、工具标识和未完成调用的协议边界。</summary>
public sealed class MessageReplayTests
{
    /// <summary>【AI】【签名归属】相同模型保留签名；提供方、API 或模型变化后去掉不兼容签名。</summary>
    /// <param name="changed">发生变化的身份字段。</param>
    [Theory]
    [InlineData("same")]
    [InlineData("provider")]
    [InlineData("api")]
    [InlineData("model")]
    public void SignaturesBelongToExactSourceModel(string changed)
    {
        var source = new AssistantMessage([
            new ThinkingContent("visible") { ThinkingSignature = "signed" },
            new ThinkingContent("") { ThinkingSignature = "encrypted" },
            new ThinkingContent("redacted-data") { Redacted = true, ThinkingSignature = "opaque" },
            new ThinkingContent(" "),
            new TextContent("answer") { TextSignature = "text-id" },
            new ToolCallContent("call", "read", "{}") { ThoughtSignature = "tool-signature" }
        ])
        {
            Provider = changed == "provider" ? "other" : "openai",
            Api = changed == "api" ? "other" : "openai-completions",
            Model = changed == "model" ? "other" : "model"
        };
        var result = MessageTransformer.TransformMessages([source, new ToolResultMessage("call", [new TextContent("done")])], Model());
        var assistant = Assert.IsType<AssistantMessage>(result[0]);
        if (changed == "same")
        {
            Assert.Equal(5, assistant.Content.Count);
            Assert.Equal(3, assistant.Content.OfType<ThinkingContent>().Count());
            Assert.Equal("text-id", Assert.Single(assistant.Content.OfType<TextContent>()).TextSignature);
            Assert.Equal("tool-signature", Assert.Single(assistant.Content.OfType<ToolCallContent>()).ThoughtSignature);
        }
        else
        {
            Assert.Equal(3, assistant.Content.Count);
            Assert.Empty(assistant.Content.OfType<ThinkingContent>());
            Assert.Equal(["visible", "answer"], assistant.Content.OfType<TextContent>().Select(item => item.Text));
            Assert.All(assistant.Content.OfType<TextContent>(), item => Assert.Null(item.TextSignature));
            Assert.Null(Assert.Single(assistant.Content.OfType<ToolCallContent>()).ThoughtSignature);
        }
        Assert.Equal(6, source.Content.Count);
        Assert.Equal("tool-signature", Assert.Single(source.Content.OfType<ToolCallContent>()).ThoughtSignature);
    }

    /// <summary>【AI】【工具配对】系统更新暂存到完整结果之后，已返回的调用不会重复补结果。</summary>
    [Fact]
    public void InterleavedSystemMessagesWaitForRealAndSyntheticResults()
    {
        var first = new SystemMessage("update-1");
        var second = new SystemMessage("update-2");
        var input = new ChatMessage[]
        {
            new AssistantMessage([new ToolCallContent("a", "one", "{}"), new ToolCallContent("b", "two", "{}")]),
            first, new ToolResultMessage("a", [new TextContent("real")]), second, new UserMessage("next")
        };
        var replay = MessageTransformer.TransformMessages(input, Model());
        Assert.Equal(["assistant", "toolResult", "toolResult", "system", "system", "user"], replay.Select(item => item.Role));
        Assert.Equal("a", Assert.IsType<ToolResultMessage>(replay[1]).ToolCallId);
        var synthesized = Assert.IsType<ToolResultMessage>(replay[2]);
        Assert.Equal("b", synthesized.ToolCallId);
        Assert.Equal("two", synthesized.ToolName);
        Assert.True(synthesized.IsError);
        Assert.Equal("No result provided", Assert.IsType<TextContent>(Assert.Single(synthesized.Content)).Text);
        Assert.Same(first, replay[3]);
        Assert.Same(second, replay[4]);
        Assert.Equal(5, input.Length);
    }

    /// <summary>【AI】【失败历史】失败或取消的助手不重放，前一轮缺失结果仍在下一助手边界补齐。</summary>
    /// <param name="reason">失败原因。</param>
    [Theory]
    [InlineData(StopReason.Error)]
    [InlineData(StopReason.Aborted)]
    public void FailedAssistantsAreSkippedAfterClosingPreviousCalls(StopReason reason)
    {
        var replay = MessageTransformer.TransformMessages([
            new AssistantMessage([new ToolCallContent("a", "read", "{}")]),
            new AssistantMessage([new ToolCallContent("failed", "write", "{")]) { StopReason = reason },
            new UserMessage("retry")
        ], Model());
        Assert.Equal(["assistant", "toolResult", "user"], replay.Select(item => item.Role));
        Assert.Equal("a", Assert.IsType<ToolResultMessage>(replay[1]).ToolCallId);
    }

    /// <summary>【AI】【结束边界】历史结束时补齐调用，系统消息在合成结果之后输出。</summary>
    [Fact]
    public void EndOfHistoryClosesPendingCalls()
    {
        var replay = MessageTransformer.TransformMessages([
            new AssistantMessage([new ToolCallContent("a", "read", "{}")]), new SystemMessage("update")
        ], Model());
        Assert.Equal(["assistant", "toolResult", "system"], replay.Select(item => item.Role));
    }

    /// <summary>【AI】【空内容】不可信旧历史的 null 内容转为空集合，支持和不支持图片的模型行为一致。</summary>
    /// <param name="images">模型是否支持图片。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullContentDoesNotBreakReplay(bool images)
    {
        var replay = MessageTransformer.TransformMessages([
            new UserMessage("x") { Content = null! }, new AssistantMessage { Content = null! },
            new ToolResultMessage("a", null!)
        ], Model() with { InputModalities = images ? ["text", "image"] : ["text"] });
        Assert.Empty(Assert.IsType<UserMessage>(replay[0]).Content);
        Assert.Empty(Assert.IsType<AssistantMessage>(replay[1]).Content);
        Assert.Empty(Assert.IsType<ToolResultMessage>(replay[2]).Content);
    }

    /// <summary>【AI】【工具ID报文】复合 ID 规范化后保持调用、结果一一对应，长 ID 的不同尾部不会碰撞。</summary>
    /// <param name="compatible">是否使用兼容别名。</param><param name="sameModel">是否精确同模型重放。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HttpReplayPreservesDistinctCallResultPairs(bool compatible, bool sameModel)
    {
        var ids = new[] { "call|item+1", "call_" + new string('a', 50) + "|" + new string('b', 200) + "1",
            "call_" + new string('a', 50) + "|" + new string('b', 200) + "2" };
        var source = new AssistantMessage(ids.Select(id => (ContentBlock)new ToolCallContent(id, "read", "{}")).ToArray())
        {
            Provider = sameModel ? "openai" : "other",
            Api = sameModel ? "openai-chat-completions" : "openai-responses", Model = "model"
        };
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = compatible ? new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client) : new OpenAiProvider(client);
        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(Model(),
            new() { Messages = [source, .. ids.Select(id => new ToolResultMessage(id, [new TextContent("done")]))] },
            new() { ApiKey = "synthetic", CacheRetention = CacheRetention.None, MaxRetries = 0 }));
        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        var messages = body.RootElement.GetProperty("messages");
        var calls = messages[0].GetProperty("tool_calls");
        var actual = calls.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(3, actual.Distinct().Count());
        for (var index = 0; index < actual.Length; index++)
        {
            Assert.Equal(actual[index], messages[index + 1].GetProperty("tool_call_id").GetString());
            if (sameModel) Assert.Equal(ids[index], actual[index]);
            else
            {
                Assert.True(actual[index].Length <= 40);
                Assert.Matches("^[a-zA-Z0-9_-]+$", actual[index]);
            }
        }
        if (!sameModel) Assert.Equal("call_item_1", actual[0]);
        Assert.Equal(ids, source.Content.OfType<ToolCallContent>().Select(item => item.Id));
    }

    /// <summary>【AI】【普通ID】只有 OpenAI 目标会裁剪不带复合分隔符的长标识。</summary>
    /// <param name="provider">目标提供方。</param><param name="expectedLength">预期长度。</param>
    [Theory]
    [InlineData("openai", 40)]
    [InlineData("other", 80)]
    public void PlainIdsRespectTargetProvider(string provider, int expectedLength)
    {
        var id = new string('a', 80);
        var result = OpenAiToolCallIds.Normalize(id, Model() with { Provider = provider }, new AssistantMessage());
        Assert.Equal(expectedLength, result.Length);
    }

    /// <summary>【AI】【模型夹具】创建可用于原生与兼容请求的固定身份。</summary>
    /// <returns>测试模型。</returns>
    private static Model Model() => new()
    {
        Id = "model", Name = "Model", Provider = "openai", Api = "openai-chat-completions", BaseUrl = "https://example.invalid/v1"
    };
}
