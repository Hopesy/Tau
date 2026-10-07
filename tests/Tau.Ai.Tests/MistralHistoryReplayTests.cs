// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers.Mistral;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Mistral 回放】验证结构化思考、跨模型 ID 与图文结果。</summary>
public sealed class MistralHistoryReplayTests
{
    /// <summary>【AI】【历史来源】同模型思考保持结构，跨模型转为正文且规范化 ID。</summary>
    /// <param name="source">来源差异。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("same")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("api")]
    public async Task IdentityControlsThinkingAndIds(string source)
    {
        var model = Model();
        var history = History(model, new ThinkingContent("thought"), new TextContent("answer"), new ToolCallContent("call|item", "read", "{}"));
        history = source switch
        {
            "model" => history with { Model = "other" }, "provider" => history with { Provider = "other" },
            "api" => history with { Api = "openai-responses" }, _ => history
        };
        var messages = (await SendAsync(model, [history, new ToolResultMessage("call|item", [new TextContent("result")])])).GetProperty("messages");
        var assistant = messages[0];
        Assert.False(assistant.GetProperty("prefix").GetBoolean());
        var first = assistant.GetProperty("content")[0];
        Assert.Equal(source == "same" ? "thinking" : "text", first.GetProperty("type").GetString());
        Assert.Equal("thought", source == "same" ? first.GetProperty("thinking")[0].GetProperty("text").GetString() : first.GetProperty("text").GetString());
        Assert.Equal("answer", assistant.GetProperty("content")[1].GetProperty("text").GetString());
        var call = assistant.GetProperty("tool_calls")[0];
        var id = source == "same" ? "call|item" : ShortHash.Compute("callitem")[..9];
        Assert.Equal(id, call.GetProperty("id").GetString());
        Assert.Equal(0, call.GetProperty("index").GetInt32());
        Assert.Equal(id, messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("read", messages[1].GetProperty("name").GetString());
    }

    /// <summary>【AI】【碰撞处理】九字符规范化碰撞使用规范种子的下一次摘要，结果保持映射。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ForeignIdCollisionsUseDeterministicRetrySeed()
    {
        var model = Model();
        var assistant = History(model, new ToolCallContent("123456789", "one", "{}"), new ToolCallContent("123-456789", "two", "{}")) with { Provider = "foreign" };
        var messages = (await SendAsync(model, [assistant, new ToolResultMessage("123456789", []), new ToolResultMessage("123-456789", [])])).GetProperty("messages");
        var calls = messages[0].GetProperty("tool_calls");
        Assert.Equal("123456789", calls[0].GetProperty("id").GetString());
        var second = ShortHash.Compute("123456789:1")[..9];
        Assert.Equal(second, calls[1].GetProperty("id").GetString());
        Assert.Equal(second, messages[2].GetProperty("tool_call_id").GetString());
    }

    /// <summary>【AI】【工具完整性】图片能力和错误状态共同决定摘要，全部图像保留在结果数组内。</summary>
    /// <param name="vision">图片能力。</param><param name="error">工具错误。</param><param name="mixed">是否附带多段文本。</param>
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
    public async Task ToolImagesAndTextAreRetained(bool vision, bool error, bool mixed)
    {
        var model = Model() with { InputModalities = vision ? ["text", "image"] : ["text"] };
        var content = new List<ContentBlock>();
        if (mixed) content.Add(new TextContent(" first "));
        content.Add(new ImageContent("aW1n", "image/png"));
        if (mixed) content.Add(new TextContent(" last "));
        var body = await SendAsync(model, [new UserMessage(content), History(model, new ToolCallContent("call", "read", "{}")), new ToolResultMessage("call", content, error)]);
        var messages = body.GetProperty("messages");
        var user = messages[0].GetProperty("content");
        if (vision) Assert.Contains(user.EnumerateArray(), part => part.GetProperty("type").GetString() == "image_url");
        var output = messages[2].GetProperty("content");
        Assert.Equal(vision ? 2 : 1, output.GetArrayLength());
        var omitted = "(tool image omitted: model does not support images)";
        var expected = mixed ? vision ? "first \n last" : "first \n" + omitted + "\n last" : vision ? "(see attached image)" : omitted;
        Assert.Equal((error ? "[tool error] " : "") + expected, output[0].GetProperty("text").GetString());
        if (vision) Assert.Equal("data:image/png;base64,aW1n", output[1].GetProperty("image_url").GetString());
    }

    /// <summary>【AI】【中断与补齐】失败助手被过滤，前一有效助手的未完成调用在系统更新前补齐。</summary>
    /// <param name="reason">失败原因。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(StopReason.Error)]
    [InlineData(StopReason.Aborted)]
    public async Task MissingResultsAndFailureFilteringPreserveOrder(StopReason reason)
    {
        var model = Model() with { Compat = new() { SupportsMidConvoSystemMessages = true } };
        var messages = (await SendAsync(model, [History(model, new ToolCallContent("call", "read", "{}")), new SystemMessage("update"),
            History(model, new TextContent("partial")) with { StopReason = reason }, new UserMessage("next")])).GetProperty("messages");
        Assert.Equal(["assistant", "tool", "system", "user"], messages.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        Assert.Equal("[tool error] No result provided", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>【AI】【空历史】null 用户及助手跳过，工具 null 结果转换为占位数组。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task NullContentUsesToolPlaceholder()
    {
        var model = Model();
        var messages = (await SendAsync(model, [new UserMessage("x") with { Content = null! }, History(model) with { Content = null! },
            History(model, new ToolCallContent("call", "read", "{}")), new ToolResultMessage("call", null!)])).GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("(no tool output)", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>【AI】【工具严格采样】按工具约束解析严格模式并处理参数结构。</summary>
    /// <param name="strict">工具严格模式。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolStrictSamplingIsApplied(bool strict)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""");
        var tool = new Tool("read", "Read", schema.RootElement.Clone())
        { ConstrainedSampling = strict ? new() { Type = "json_schema", Strict = "require" } : null };
        var body = await SendAsync(Model(), [new UserMessage("run")], [tool]);
        var function = body.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal(strict, function.GetProperty("strict").GetBoolean());
        if (strict) Assert.False(function.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
    }

    /// <summary>【AI】【模型夹具】提供固定 Mistral 身份。</summary>
    /// <returns>目标模型。</returns>
    private static Model Model() => new() { Id = "model", Name = "Model", Provider = "mistral", Api = "mistral-conversations", BaseUrl = "https://example.invalid" };

    /// <summary>【AI】【历史夹具】创建同模型成功助手。</summary>
    /// <param name="model">模型。</param><param name="content">内容。</param><returns>助手。</returns>
    private static AssistantMessage History(Model model, params ContentBlock[] content) => new(content)
    { Provider = model.Provider, Api = model.Api, Model = model.Id, StopReason = StopReason.EndTurn };

    /// <summary>【AI】【HTTP 夹具】通过完整序列化捕获请求。</summary>
    /// <param name="model">模型。</param><param name="messages">历史。</param><param name="tools">工具。</param><returns>独立请求 JSON。</returns>
    private static async Task<JsonElement> SendAsync(Model model, IReadOnlyList<ChatMessage> messages, IReadOnlyList<Tool>? tools = null)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("captured") });
        using var client = new HttpClient(handler);
        var stream = new MistralProvider(client).Stream(model, new() { Messages = messages, Tools = tools }, new() { ApiKey = "synthetic", MaxRetries = 0 });
        await OpenAiResponsesProviderTests.CollectAsync(stream);
        Assert.True(handler.Requests.Count == 1, (await stream.ResultAsync).ErrorMessage);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        return body.RootElement.Clone();
    }
}
