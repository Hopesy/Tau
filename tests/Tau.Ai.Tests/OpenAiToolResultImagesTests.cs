// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;

namespace Tau.Ai.Tests;

/// <summary>【AI】【工具图片】确保工具配对、文本分隔及视觉附件均进入实际请求。</summary>
public sealed class OpenAiToolResultImagesTests
{
    /// <summary>【AI】【结果分组】两个连续工具结果中的图片集中发送，助手过渡只在必要位置出现。</summary>
    /// <param name="compatible">兼容别名实现。</param><param name="vision">目标支持图片。</param>
    /// <param name="bridge">是否需要助手过渡。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ConsecutiveToolImagesFollowPairedResults(bool compatible, bool vision, bool bridge)
    {
        var first = new ToolResultMessage("a", [new TextContent("one"), new TextContent("two"), new ImageContent("YQ==", "image/png")]) { ToolName = "first" };
        var second = new ToolResultMessage("b", [new ImageContent("Yg==", "image/jpeg")]) { ToolName = "second" };
        var context = new LlmContext
        {
            Messages =
            [
                new AssistantMessage([new ToolCallContent("a", "first", "{}"), new ToolCallContent("b", "second", "{}")]),
                first, second, new UserMessage("next")
            ]
        };
        using var body = await CaptureAsync(context, compatible, vision, bridge);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("assistant", messages[0].GetProperty("role").GetString());
        Assert.Equal("tool", messages[1].GetProperty("role").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("a", messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("b", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("first", messages[1].GetProperty("name").GetString());
        Assert.Equal("second", messages[2].GetProperty("name").GetString());
        Assert.Equal(vision ? "one\ntwo" : "one\ntwo\n(tool image omitted: model does not support images)", messages[1].GetProperty("content").GetString());
        Assert.Equal(vision ? "(see attached image)" : "(tool image omitted: model does not support images)", messages[2].GetProperty("content").GetString());
        var cursor = 3;
        if (bridge)
        {
            Assert.Equal("assistant", messages[cursor].GetProperty("role").GetString());
            Assert.Equal("I have processed the tool results.", messages[cursor++].GetProperty("content").GetString());
        }
        if (vision)
        {
            Assert.Equal("user", messages[cursor].GetProperty("role").GetString());
            var parts = messages[cursor++].GetProperty("content");
            Assert.Equal(3, parts.GetArrayLength());
            Assert.Equal("Attached image(s) from tool result:", parts[0].GetProperty("text").GetString());
            Assert.Equal("data:image/png;base64,YQ==", parts[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("data:image/jpeg;base64,Yg==", parts[2].GetProperty("image_url").GetProperty("url").GetString());
        }
        Assert.Equal("next", messages[cursor].GetProperty("content").GetString());
        Assert.Equal(cursor + 1, messages.GetArrayLength());
        Assert.Single(first.Content.OfType<ImageContent>());
        Assert.Single(second.Content.OfType<ImageContent>());
    }

    /// <summary>【AI】【空结果】空正文使用明确占位；无图片时不添加用户附件消息。</summary>
    /// <param name="compatible">兼容别名实现。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyToolResultHasPlaceholderWithoutAttachment(bool compatible)
    {
        using var body = await CaptureAsync(new()
        {
            Messages = [new AssistantMessage([new ToolCallContent("a", "read", "{}")]), new ToolResultMessage("a", [])]
        }, compatible, true, false);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("(no tool output)", messages[1].GetProperty("content").GetString());
    }

    /// <summary>【AI】【独立分组】两轮工具结果的图片各自附在所属结果后，不会移到整段会话末尾。</summary>
    [Fact]
    public async Task ToolImageGroupsRemainAtTheirConversationPosition()
    {
        using var body = await CaptureAsync(new()
        {
            Messages =
            [
                new AssistantMessage([new ToolCallContent("a", "read", "{}")]),
                new ToolResultMessage("a", [new ImageContent("YQ==", "image/png")]),
                new AssistantMessage([new ToolCallContent("b", "read", "{}")]),
                new ToolResultMessage("b", [new ImageContent("Yg==", "image/png")])
            ]
        }, false, true, false);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(["assistant", "tool", "user", "assistant", "tool", "user"],
            messages.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        Assert.Equal("data:image/png;base64,YQ==", messages[2].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("data:image/png;base64,Yg==", messages[5].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    /// <summary>【AI】【报文捕获】通过真实请求转换器验证文本和附件。</summary>
    /// <param name="context">原始会话。</param><param name="compatible">兼容别名。</param>
    /// <param name="vision">支持图片。</param><param name="bridge">需要助手过渡。</param><returns>待释放请求 JSON。</returns>
    private static async Task<JsonDocument> CaptureAsync(LlmContext context, bool compatible, bool vision, bool bridge)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = compatible ? new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client) : new OpenAiProvider(client);
        var model = new Model
        {
            Id = "model", Name = "Model", Provider = "local", Api = "openai-chat-completions", BaseUrl = "https://example.invalid/v1",
            InputModalities = vision ? ["text", "image"] : ["text"],
            Compat = new() { RequiresAssistantAfterToolResult = bridge, RequiresToolResultName = true }
        };
        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(model, context,
            new() { ApiKey = "synthetic", CacheRetention = CacheRetention.None, MaxRetries = 0 }));
        Assert.True(handler.Requests.Count == 1, string.Join("; ", events.OfType<ErrorEvent>().Select(item => item.Error)));
        return JsonDocument.Parse(handler.CapturedBody);
    }
}
