// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiCompat;

namespace Tau.Ai.Tests;

/// <summary>【AI】【工具边界】验证直接入口的工具选择与工具结果缓存标记。</summary>
public sealed class OpenAiRequestBoundaryTests
{
    /// <summary>【AI】【工具策略】没有工具声明时也保留显式策略，不因走简化入口而丢失。</summary>
    /// <param name="compatible">兼容别名实现。</param><param name="choice">JSON 工具选择。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, "\"auto\"")]
    [InlineData(false, "\"none\"")]
    [InlineData(false, "\"required\"")]
    [InlineData(false, """{"type":"function","function":{"name":"read_file"}}""")]
    [InlineData(true, "\"auto\"")]
    [InlineData(true, "\"none\"")]
    [InlineData(true, "\"required\"")]
    [InlineData(true, """{"type":"function","function":{"name":"read_file"}}""")]
    public async Task SimpleStreamPreservesToolChoice(bool compatible, string choice)
    {
        using var declared = JsonDocument.Parse(choice);
        object value = declared.RootElement.ValueKind == JsonValueKind.String
            ? declared.RootElement.GetString()! : declared.RootElement.Clone();
        using var body = await CaptureAsync(compatible, new() { Messages = [new UserMessage("hi")] },
            new() { ApiKey = "synthetic", MaxRetries = 0, ToolChoice = value });
        Assert.True(JsonElement.DeepEquals(declared.RootElement, body.RootElement.GetProperty("tool_choice")));
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    /// <summary>【AI】【工具缓存】工具结果或空结果占位文字成为最后缓存点。</summary>
    /// <param name="compatible">兼容别名实现。</param><param name="toolText">工具正文。</param>
    /// <param name="retention">显式缓存策略。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, "result", CacheRetention.Short)]
    [InlineData(false, "", CacheRetention.Short)]
    [InlineData(false, "result", CacheRetention.Long)]
    [InlineData(false, "result", CacheRetention.None)]
    [InlineData(true, "result", CacheRetention.Short)]
    [InlineData(true, "", CacheRetention.Short)]
    [InlineData(true, "result", CacheRetention.Long)]
    [InlineData(true, "result", CacheRetention.None)]
    public async Task ToolResultsReceiveConversationCacheMarker(bool compatible, string toolText, CacheRetention retention)
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var context = new LlmContext
        {
            SystemPrompt = "instructions",
            Messages =
            [
                new UserMessage("question"),
                new AssistantMessage([new ToolCallContent("call", "read_file", "{}")]),
                new ToolResultMessage("call", [new TextContent(toolText)]) { ToolName = "read_file" }
            ],
            Tools = [new Tool("read_file", "Read", schema.RootElement.Clone())]
        };
        using var body = await CaptureAsync(compatible, context, new() { ApiKey = "synthetic", MaxRetries = 0, CacheRetention = retention });
        var messages = body.RootElement.GetProperty("messages");
        var last = messages[3];
        Assert.Equal("tool", last.GetProperty("role").GetString());
        if (retention == CacheRetention.None)
        {
            Assert.Equal(toolText.Length > 0 ? toolText : "(no tool output)", last.GetProperty("content").GetString());
            Assert.DoesNotContain("cache_control", body.RootElement.GetRawText());
        }
        else
        {
            var marked = last;
            var marker = marked.GetProperty("content")[0].GetProperty("cache_control");
            Assert.Equal("ephemeral", marker.GetProperty("type").GetString());
            Assert.Equal(retention == CacheRetention.Long, marker.TryGetProperty("ttl", out var ttl));
            if (retention == CacheRetention.Long) Assert.Equal("1h", ttl.GetString());
            Assert.Equal("ephemeral", messages[0].GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
            Assert.Equal("ephemeral", body.RootElement.GetProperty("tools")[0].GetProperty("cache_control").GetProperty("type").GetString());
            Assert.Equal(toolText.Length > 0 ? toolText : "(no tool output)", marked.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("question", messages[1].GetProperty("content").GetString());
        }
        Assert.Equal(toolText, Assert.IsType<TextContent>(Assert.IsType<ToolResultMessage>(context.Messages[2]).Content[0]).Text);
    }

    /// <summary>【AI】【请求捕获】真实请求组装后在内存传输层截获。</summary>
    /// <param name="compatible">兼容别名实现。</param><param name="context">会话内容。</param><param name="options">简化请求选项。</param>
    /// <returns>由调用方释放的请求 JSON。</returns>
    private static async Task<JsonDocument> CaptureAsync(bool compatible, LlmContext context, SimpleStreamOptions options)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("stop") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = compatible ? new OpenAiCompatibleProvider("compatible", "https://example.invalid/v1", httpClient: client) : new OpenAiProvider(client);
        var model = new Model
        {
            Id = "anthropic/claude", Name = "Claude", Api = "openai-completions", Provider = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1", Compat = new() { CacheControlFormat = "anthropic" }
        };
        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(model, context, options));
        Assert.True(handler.Requests.Count == 1, string.Join("; ", events.OfType<ErrorEvent>().Select(item => item.Error)));
        return JsonDocument.Parse(handler.CapturedBody);
    }
}
