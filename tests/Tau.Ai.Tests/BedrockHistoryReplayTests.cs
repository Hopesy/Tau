// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Bedrock 回放】通过真实请求序列化验证来源隔离和内容有效性。</summary>
[Collection("BedrockEnvironment")]
public sealed class BedrockHistoryReplayTests
{
    /// <summary>【AI】【来源隔离】同模型标识不改写，跨模型签名清除且工具调用与结果同步规范化。</summary>
    /// <param name="source">身份差异。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("same")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("api")]
    public async Task IdentityControlsThinkingAndIds(string source)
    {
        var model = Model();
        var id = "call|" + new string('a', 80);
        var assistant = History(model, new ThinkingContent("signed") { ThinkingSignature = "opaque" }, new ThinkingContent("unsigned"),
            new ThinkingContent("") { Redacted = true, ThinkingSignature = "aGlkZGVu" }, new ToolCallContent(id, "read", "{}"));
        assistant = source switch
        {
            "model" => assistant with { Model = "other" }, "provider" => assistant with { Provider = "other" },
            "api" => assistant with { Api = "anthropic-messages" }, _ => assistant
        };
        var messages = await SendAsync(model, [assistant, new ToolResultMessage(id, [new TextContent("done")])]);
        var content = messages[0].GetProperty("content");
        var same = source == "same";
        Assert.Equal(same ? 4 : 3, content.GetArrayLength());
        if (same)
        {
            Assert.Equal("opaque", content[0].GetProperty("reasoningContent").GetProperty("reasoningText").GetProperty("signature").GetString());
            Assert.Equal("aGlkZGVu", content[2].GetProperty("reasoningContent").GetProperty("redactedContent").GetString());
        }
        else Assert.Equal("signed", content[0].GetProperty("text").GetString());
        Assert.Equal("unsigned", content[1].GetProperty("text").GetString());
        var normalizedId = same ? id : "call_" + new string('a', 59);
        Assert.Equal(normalizedId, content[content.GetArrayLength() - 1].GetProperty("toolUse").GetProperty("toolUseId").GetString());
        Assert.Equal(normalizedId, messages[1].GetProperty("content")[0].GetProperty("toolResult").GetProperty("toolUseId").GetString());
        Assert.Equal(id, assistant.Content.OfType<ToolCallContent>().Single().Id);
    }

    /// <summary>【AI】【失败过滤】未完成助手及其调用不会产生回放项和伪造结果。</summary>
    /// <param name="reason">终止原因。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(StopReason.Error)]
    [InlineData(StopReason.Aborted)]
    public async Task FailedAssistantsAreSkipped(StopReason reason)
    {
        var model = Model();
        var messages = await SendAsync(model, [new UserMessage("hi"), History(model, new ToolCallContent("partial", "read", "{")) with { StopReason = reason }, new UserMessage("retry")]);
        Assert.Equal(2, messages.GetArrayLength());
        Assert.All(messages.EnumerateArray(), item => Assert.Equal("user", item.GetProperty("role").GetString()));
    }

    /// <summary>【AI】【空内容】用户和工具空内容使用必需占位，空助手被过滤。</summary>
    /// <param name="kind">空内容来源。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("surrogate")]
    public async Task EmptyContentUsesRequiredPlaceholders(string kind)
    {
        var model = Model();
        IReadOnlyList<ContentBlock> content = kind switch
        {
            "null" => null!, "empty" => [], "blank" => [new TextContent(" \n")], _ => [new TextContent(new string((char)0xD800, 1))]
        };
        var messages = await SendAsync(model, [new UserMessage(content), History(model) with { Content = content },
            History(model, new ToolCallContent("call", "read", "{}")), new ToolResultMessage("call", content)]);
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("<empty>", messages[0].GetProperty("content")[0].GetProperty("text").GetString());
        var output = messages[2].GetProperty("content")[0].GetProperty("toolResult").GetProperty("content");
        Assert.Equal("<empty>", Assert.Single(output.EnumerateArray()).GetProperty("text").GetString());
    }

    /// <summary>【AI】【图文过滤】移除图文之间空白块，保留有效文字与原图字节。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ImagesAndNonblankTextKeepTheirOrder()
    {
        var model = Model() with { InputModalities = ["text", "image"] };
        ContentBlock[] content = [new TextContent(" "), new ImageContent("aW1n", "image/png"), new TextContent("ok"), new TextContent("")];
        var messages = await SendAsync(model, [new UserMessage(content), History(model, new ToolCallContent("call", "read", "{}")), new ToolResultMessage("call", content)]);
        foreach (var blocks in new[] { messages[0].GetProperty("content"), messages[2].GetProperty("content")[0].GetProperty("toolResult").GetProperty("content") })
        {
            Assert.Equal(2, blocks.GetArrayLength());
            Assert.Equal("aW1n", blocks[0].GetProperty("image").GetProperty("source").GetProperty("bytes").GetString());
            Assert.Equal("ok", blocks[1].GetProperty("text").GetString());
        }
    }

    /// <summary>【AI】【参数文档】递归移除空键，同时保留空白键、数组、标量和 null。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ArgumentsRemoveEmptyKeysRecursively()
    {
        var model = Model();
        const string arguments = """{"":0," ":1,"nested":{"":2,"ok":3},"array":[{"":4,"keep":true},null,5]}""";
        var messages = await SendAsync(model, [History(model, new ToolCallContent("call", "read", arguments))]);
        var input = messages[0].GetProperty("content")[0].GetProperty("toolUse").GetProperty("input");
        using var expected = JsonDocument.Parse("""{" ":1,"nested":{"ok":3},"array":[{"keep":true},null,5]}""");
        Assert.True(JsonElement.DeepEquals(expected.RootElement, input));
        Assert.Equal("No result provided", messages[1].GetProperty("content")[0].GetProperty("toolResult").GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>【AI】【工具收尾】连续真实及补齐结果合并为一轮，不重复已完成调用。</summary>
    /// <param name="answered">是否存在真实结果。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingResultsCloseBeforeNextUser(bool answered)
    {
        var model = Model();
        var history = new List<ChatMessage> { History(model, new ToolCallContent("one", "read", "{}"), new ToolCallContent("two", "read", "{}")) };
        if (answered) history.Add(new ToolResultMessage("one", [new TextContent("ok")]));
        history.Add(new UserMessage("next"));
        var messages = await SendAsync(model, history);
        Assert.Equal(3, messages.GetArrayLength());
        var results = messages[1].GetProperty("content");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal(answered ? "success" : "error", results[0].GetProperty("toolResult").GetProperty("status").GetString());
        Assert.Equal("error", results[1].GetProperty("toolResult").GetProperty("status").GetString());
    }

    /// <summary>【AI】【模型夹具】创建固定 Claude Bedrock 模型。</summary>
    /// <returns>目标模型。</returns>
    private static Model Model() => new() { Id = "anthropic.claude-sonnet-4", Name = "Model", Provider = "amazon-bedrock", Api = "bedrock-converse-stream", BaseUrl = "https://example.invalid" };

    /// <summary>【AI】【历史夹具】创建同模型成功响应。</summary>
    /// <param name="model">模型。</param><param name="content">内容。</param><returns>助手消息。</returns>
    private static AssistantMessage History(Model model, params ContentBlock[] content) => new(content)
    { Provider = model.Provider, Api = model.Api, Model = model.Id, StopReason = StopReason.EndTurn };

    /// <summary>【AI】【请求夹具】完整序列化 Bedrock 请求，拦截传输后返回消息数组。</summary>
    /// <param name="model">模型。</param><param name="history">会话历史。</param><returns>独立的 messages JSON。</returns>
    private static async Task<JsonElement> SendAsync(Model model, IReadOnlyList<ChatMessage> history)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("captured") });
        using var client = new HttpClient(handler);
        var stream = new BedrockProvider(client).Stream(model, new() { Messages = history }, new BedrockOptions
        { BearerToken = "synthetic", Region = "us-east-1", MaxRetries = 0, CacheRetention = CacheRetention.None });
        await OpenAiResponsesProviderTests.CollectAsync(stream);
        Assert.True(handler.Requests.Count == 1, (await stream.ResultAsync).ErrorMessage);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        return body.RootElement.GetProperty("messages").Clone();
    }
}
