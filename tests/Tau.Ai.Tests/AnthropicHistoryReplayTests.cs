// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers.Anthropic;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Anthropic 回放】验证模型来源、工具配对和完整图文结果。</summary>
public sealed class AnthropicHistoryReplayTests
{
    /// <summary>【AI】【来源签名】签名只在同模型保留，跨模型工具 ID 与结果同步规范化。</summary>
    /// <param name="source">身份差异。</param><param name="allowEmpty">是否允许空签名。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("same", false)]
    [InlineData("same", true)]
    [InlineData("model", false)]
    [InlineData("model", true)]
    [InlineData("provider", false)]
    [InlineData("provider", true)]
    [InlineData("api", false)]
    [InlineData("api", true)]
    public async Task SourceIdentityControlsThinkingAndToolIds(string source, bool allowEmpty)
    {
        var model = Model() with { Compat = new() { AllowEmptySignature = allowEmpty } };
        var id = "call|" + new string('a', 80);
        var assistant = History(model,
            new ThinkingContent("visible") { ThinkingSignature = "signed" },
            new ThinkingContent("") { Redacted = true, ThinkingSignature = "opaque" },
            new ThinkingContent("unsigned"), new ToolCallContent(id, "read", "{}"));
        assistant = source switch
        {
            "model" => assistant with { Model = "other" }, "provider" => assistant with { Provider = "other" },
            "api" => assistant with { Api = "openai-responses" }, _ => assistant
        };
        var body = await SendAsync(model, [assistant, new ToolResultMessage(id, [new TextContent("done")])]);
        var messages = body.GetProperty("messages");
        var content = messages[0].GetProperty("content").EnumerateArray().ToArray();
        var same = source == "same";
        Assert.Equal(same ? "thinking" : "text", content[0].GetProperty("type").GetString());
        Assert.Equal(same, content[0].TryGetProperty("signature", out _));
        Assert.Equal(same ? 1 : 0, content.Count(item => item.GetProperty("type").GetString() == "redacted_thinking"));
        Assert.Equal(same && allowEmpty ? "thinking" : "text", content[^2].GetProperty("type").GetString());
        var normalizedId = same ? id : "call_" + new string('a', 59);
        Assert.Equal(normalizedId, content[^1].GetProperty("id").GetString());
        Assert.Equal(normalizedId, messages[1].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.Equal(id, assistant.Content.OfType<ToolCallContent>().Single().Id);
    }

    /// <summary>【AI】【中断过滤】不回放失败或取消助手中的文本与未完成调用。</summary>
    /// <param name="reason">失败原因。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(StopReason.Error)]
    [InlineData(StopReason.Aborted)]
    public async Task InterruptedAssistantsDoNotReplay(StopReason reason)
    {
        var model = Model();
        var failed = History(model, new TextContent("partial"), new ToolCallContent("unfinished", "read", "{")) with { StopReason = reason };
        var body = await SendAsync(model, [new UserMessage("hi"), failed, new UserMessage("retry")]);
        var messages = body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.All(messages.EnumerateArray(), item => Assert.Equal("user", item.GetProperty("role").GetString()));
    }

    /// <summary>【AI】【工具闭合】系统更新不拆散结果组，缺失结果在下一轮前补齐。</summary>
    /// <param name="answered">第一个调用是否已有真实结果。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemsAndMissingResultsPreserveToolGroup(bool answered)
    {
        var model = Model() with { Compat = new() { SupportsMidConvoSystemMessages = true } };
        var history = new List<ChatMessage>
        {
            new SystemMessage("base"), History(model, new ToolCallContent("one", "read", "{}"), new ToolCallContent("two", "read", "{}")),
            new SystemMessage("update")
        };
        if (answered) history.Add(new ToolResultMessage("one", [new TextContent("done")]));
        history.Add(new UserMessage("next"));
        var messages = (await SendAsync(model, history)).GetProperty("messages");
        Assert.Equal(["assistant", "user", "user", "system"], messages.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        var results = messages[1].GetProperty("content");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal(answered ? "done" : "No result provided", results[0].GetProperty("content").GetString());
        Assert.Equal(!answered, results[0].GetProperty("is_error").GetBoolean());
        Assert.Equal("two", results[1].GetProperty("tool_use_id").GetString());
        Assert.True(results[1].GetProperty("is_error").GetBoolean());
        Assert.Equal("update", messages[3].GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>【AI】【用户过滤】null、空数组与纯空白用户被过滤，图文用户保留有效内容。</summary>
    /// <param name="vision">是否支持图片。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserMessagesFilterBlankParts(bool vision)
    {
        var model = Model() with { InputModalities = vision ? ["text", "image"] : ["text"] };
        var body = await SendAsync(model, [new UserMessage("x") with { Content = null! }, new UserMessage([]), new UserMessage(" \n"),
            new UserMessage([new TextContent(" "), new ImageContent("aW1n", "image/png"), new TextContent("last")])]);
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        var content = message.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal(vision ? "image" : "text", content[0].GetProperty("type").GetString());
        Assert.Equal("last", content[1].GetProperty("text").GetString());
    }

    /// <summary>【AI】【工具图文】保留所有文本、图像和空文本位置，非视觉模型使用共享图片占位。</summary>
    /// <param name="vision">图片能力。</param><param name="image">是否包含图片。</param><param name="error">错误标记。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ToolResultsKeepEveryContentBlock(bool vision, bool image, bool error)
    {
        var model = Model() with { InputModalities = vision ? ["text", "image"] : ["text"] };
        var content = new List<ContentBlock> { new TextContent("first"), new TextContent("") };
        if (image) content.Add(new ImageContent("aW1n", "image/png"));
        content.Add(new TextContent("last"));
        var body = await SendAsync(model, [History(model, new ToolCallContent("call", "read", "{}")), new ToolResultMessage("call", content, error)]);
        var result = body.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal(error, result.GetProperty("is_error").GetBoolean());
        var output = result.GetProperty("content");
        if (image && vision)
        {
            Assert.Equal(4, output.GetArrayLength());
            Assert.Equal("first", output[0].GetProperty("text").GetString());
            Assert.Equal("", output[1].GetProperty("text").GetString());
            Assert.Equal("image/png", output[2].GetProperty("source").GetProperty("media_type").GetString());
            Assert.Equal("aW1n", output[2].GetProperty("source").GetProperty("data").GetString());
            Assert.Equal("last", output[3].GetProperty("text").GetString());
        }
        else Assert.Equal(image ? "first\n\n(tool image omitted: model does not support images)\nlast" : "first\n\nlast", output.GetString());
    }

    /// <summary>【AI】【空工具结果】缺省内容归一为空字符串，调用与结果仍保持配对。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NullToolContentUsesEmptyString()
    {
        var model = Model();
        var body = await SendAsync(model, [History(model, new ToolCallContent("call", "read", "{}")), new ToolResultMessage("call", null!)]);
        Assert.Equal("", body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("content").GetString());
    }

    /// <summary>【AI】【模型夹具】使用固定身份和内存 HTTP 目标。</summary>
    /// <returns>测试模型。</returns>
    private static Model Model() => new() { Id = "model", Name = "Model", Provider = "test", Api = "anthropic-messages", BaseUrl = "https://example.invalid" };

    /// <summary>【AI】【历史夹具】创建同模型成功响应。</summary>
    /// <param name="model">模型身份。</param><param name="content">内容。</param><returns>助手消息。</returns>
    private static AssistantMessage History(Model model, params ContentBlock[] content) => new(content)
    { Provider = model.Provider, Api = model.Api, Model = model.Id, StopReason = StopReason.EndTurn };

    /// <summary>【AI】【请求夹具】捕获实际 Anthropic 请求并返回独立 JSON。</summary>
    /// <param name="model">模型。</param><param name="messages">历史。</param><returns>请求 JSON。</returns>
    private static async Task<JsonElement> SendAsync(Model model, IReadOnlyList<ChatMessage> messages)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("captured") });
        using var client = new HttpClient(handler);
        var stream = new AnthropicProvider(client).Stream(model, new() { Messages = messages }, new() { ApiKey = "synthetic", MaxRetries = 0 });
        await OpenAiResponsesProviderTests.CollectAsync(stream);
        Assert.True(handler.Requests.Count == 1, (await stream.ResultAsync).ErrorMessage);
        using var document = JsonDocument.Parse(handler.CapturedBody);
        return document.RootElement.Clone();
    }
}
