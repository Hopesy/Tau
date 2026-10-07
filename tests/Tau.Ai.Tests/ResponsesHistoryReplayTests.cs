// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

/// <summary>【AI】【Responses 回放】通过三种真实请求组装路径验证历史消息转换。</summary>
public sealed class ResponsesHistoryReplayTests
{
    private static readonly string[] Apis = ["openai-responses", "azure-openai-responses", "openai-codex-responses"];

    /// <summary>【AI】【来源边界】同模型保留签名，换模型转换思考并按提供方关系处理调用 item ID。</summary>
    /// <param name="source">来源身份差异。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("same")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("api")]
    public async Task IdentityControlsSignaturesAndItemIds(string source)
    {
        foreach (var api in Apis)
        {
            var model = CreateModel(api);
            var assistant = History(model,
                new ThinkingContent("visible") { ThinkingSignature = """{"type":"reasoning","id":"rs_visible","summary":[]}""" },
                new ThinkingContent("") { Redacted = true, ThinkingSignature = """{"type":"reasoning","id":"rs_hidden","encrypted_content":"opaque","summary":[]}""" },
                new TextContent("answer") { TextSignature = """{"v":1,"id":"msg_saved","phase":"final_answer"}""" },
                new ToolCallContent("call_1|fc_item", "read", "{}"));
            assistant = source switch
            {
                "model" => assistant with { Model = "other" },
                "provider" => assistant with { Provider = "other" },
                "api" => assistant with { Api = "anthropic-messages" },
                _ => assistant
            };
            var body = await SendAsync(model, [assistant, new ToolResultMessage("call_1|fc_item", [new TextContent("result")]), new UserMessage("next")]);
            var input = body.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal(source == "same" ? 2 : 0, input.Count(item => Type(item) == "reasoning"));
            var texts = input.Where(item => Type(item) == "message").ToArray();
            Assert.Equal(source == "same" ? 1 : 2, texts.Length);
            var answer = texts[^1];
            Assert.Equal(source == "same" ? "msg_saved" : "msg_pi_0_1", answer.GetProperty("id").GetString());
            Assert.Equal(source == "same", answer.TryGetProperty("phase", out _));
            if (source != "same") Assert.Equal("visible", texts[0].GetProperty("content")[0].GetProperty("text").GetString());
            var call = Assert.Single(input, item => Type(item) == "function_call");
            Assert.Equal(source != "model", call.TryGetProperty("id", out var itemId));
            if (source != "model") Assert.Equal(source == "same" ? "fc_item" : "fc_" + ShortHash.Compute("fc_item"), itemId.GetString());
            Assert.Equal("call_1", call.GetProperty("call_id").GetString());
            var result = Assert.Single(input, item => Type(item) == "function_call_output");
            Assert.Equal("call_1", result.GetProperty("call_id").GetString());
            Assert.Equal("result", result.GetProperty("output").GetString());
            Assert.Equal("fc_item", assistant.Content.OfType<ToolCallContent>().Single().Id.Split('|')[1]);
        }
    }

    /// <summary>【AI】【中断历史】错误或取消的未完成助手不得进入后续请求。</summary>
    /// <param name="reason">结束原因。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(StopReason.Error)]
    [InlineData(StopReason.Aborted)]
    public async Task IncompleteAssistantsAreRemoved(StopReason reason)
    {
        foreach (var api in Apis)
        {
            var model = CreateModel(api);
            var failed = History(model, new ThinkingContent("") { ThinkingSignature = "malformed" }, new ToolCallContent("partial", "read", "{")) with { StopReason = reason };
            var body = await SendAsync(model, [new UserMessage("hi"), failed, new UserMessage("retry")]);
            var input = body.GetProperty("input");
            Assert.Equal(2, input.GetArrayLength());
            Assert.All(input.EnumerateArray(), item => Assert.Equal("user", item.GetProperty("role").GetString()));
        }
    }

    /// <summary>【AI】【系统排序】将工具期间系统更新放在真实及补齐结果之后，保留工具加载种子。</summary>
    /// <param name="answered">第一个调用是否有真实结果。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemUpdatesFollowPairedResults(bool answered)
    {
        foreach (var api in Apis)
        {
            var model = CreateModel(api) with { Compat = new() { SupportsMidConvoSystemMessages = true, SupportsToolSearch = true } };
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
            var added = new Tool("added", "Added", schema.RootElement.Clone());
            var history = new List<ChatMessage>
            {
                new SystemMessage("base"),
                History(model, new ToolCallContent("one|fc_one", "read", "{}"), new ToolCallContent("two|fc_two", "read", "{}")),
                new SystemMessage("updated") { ToolsAdded = [added] }
            };
            if (answered) history.Add(new ToolResultMessage("one|fc_one", [new TextContent("ok")], IsError: true));
            history.Add(new UserMessage("next"));
            var input = (await SendAsync(model, history)).GetProperty("input").EnumerateArray().ToArray();
            var results = input.Where(item => Type(item) == "function_call_output").ToArray();
            Assert.Equal(2, results.Length);
            Assert.Equal(answered ? "ok" : "No result provided", results[0].GetProperty("output").GetString());
            Assert.Equal("No result provided", results[1].GetProperty("output").GetString());
            Assert.All(results, item => Assert.False(item.TryGetProperty("status", out _)));
            var search = Array.FindIndex(input, item => Type(item) == "tool_search_call");
            Assert.True(search > Array.FindLastIndex(input, item => Type(item) == "function_call_output"));
            Assert.Equal("pi_tool_load_" + ShortHash.Compute("system:3:added"), input[search].GetProperty("call_id").GetString());
            Assert.Contains("updated", input[search + 2].GetProperty("content").GetString());
        }
    }

    /// <summary>【AI】【稳定标识】忽略空消息和无签名思考，按转换后消息及文本块位置生成 ID。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task TextIdsUseMessageAndBlockIndexes()
    {
        foreach (var api in Apis)
        {
            var model = CreateModel(api);
            var body = await SendAsync(model,
            [
                new SystemMessage("base"), new UserMessage("hi"), new UserMessage([]), History(model),
                History(model, new ThinkingContent("unsigned")),
                History(model, new TextContent("a") { TextSignature = "saved" }, new TextContent("b"), new TextContent("c")),
                new UserMessage("next"), History(model, new TextContent("d"))
            ]);
            var texts = body.GetProperty("input").EnumerateArray().Where(item => Type(item) == "message").ToArray();
            Assert.Equal(["saved", "msg_pi_1_1", "msg_pi_1_2", "msg_pi_3"], texts.Select(item => item.GetProperty("id").GetString()));
        }
    }

    /// <summary>【AI】【文本签名】版本、ID 和 phase 的宽容解析须与主线旧格式兼容。</summary>
    /// <param name="signature">历史签名。</param><param name="id">期望 ID。</param><param name="phase">期望阶段。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("", "msg_pi_0", null)]
    [InlineData(" ", " ", null)]
    [InlineData("legacy", "legacy", null)]
    [InlineData("{broken", "{broken", null)]
    [InlineData("{\"v\":1,\"id\":\"\",\"phase\":\"commentary\"}", "msg_pi_0", "commentary")]
    [InlineData("{\"v\":1.0,\"id\":\"id\",\"phase\":\"final_answer\"}", "id", "final_answer")]
    [InlineData("{\"v\":1,\"id\":\"id\",\"phase\":\"unknown\"}", "id", null)]
    [InlineData("{\"v\":\"1\",\"id\":\"id\"}", "{\"v\":\"1\",\"id\":\"id\"}", null)]
    [InlineData("{\"v\":1.5,\"id\":\"id\"}", "{\"v\":1.5,\"id\":\"id\"}", null)]
    [InlineData("{\"v\":1,\"id\":42}", "{\"v\":1,\"id\":42}", null)]
    public async Task TextSignaturesRetainLegacyFallbacks(string signature, string id, string? phase)
    {
        foreach (var api in Apis)
        {
            var model = CreateModel(api);
            var body = await SendAsync(model, [History(model, new TextContent("answer") { TextSignature = signature })]);
            var text = body.GetProperty("input")[0];
            Assert.Equal(id, text.GetProperty("id").GetString());
            Assert.Equal(phase is not null, text.TryGetProperty("phase", out var value));
            if (phase is not null) Assert.Equal(phase, value.GetString());
        }
    }

    /// <summary>【AI】【长标识】长文本 ID 使用共享主线摘要缩短。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task LongTextIdsUseUpstreamHash()
    {
        var signature = string.Concat(Enumerable.Repeat("长🙂", 30));
        foreach (var api in Apis)
        {
            var model = CreateModel(api);
            var body = await SendAsync(model, [History(model, new TextContent("answer") { TextSignature = signature })]);
            Assert.Equal("msg_" + ShortHash.Compute(signature), body.GetProperty("input")[0].GetProperty("id").GetString());
        }
    }

    /// <summary>【AI】【结果正文】空结果使用占位，图片前的空白正文仍保留，错误不增加非协议 status。</summary>
    /// <param name="text">结果正文。</param><param name="image">是否包含图片。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData(" \n", true)]
    [InlineData(" \n", false)]
    public async Task ToolOutputsPreserveWhitespaceAndEmptyPlaceholder(string text, bool image)
    {
        foreach (var api in Apis)
        {
            var model = CreateModel(api);
            var content = new List<ContentBlock> { new TextContent(text) };
            if (image) content.Add(new ImageContent("aW1hZ2U=", "image/png"));
            var body = await SendAsync(model, [History(model, new ToolCallContent("call", "read", "{}")), new ToolResultMessage("call", content, IsError: true)]);
            var result = body.GetProperty("input")[1];
            Assert.False(result.TryGetProperty("status", out _));
            var output = result.GetProperty("output");
            if (!image) Assert.Equal(text.Length == 0 ? "(no tool output)" : text, output.GetString());
            else
            {
                Assert.Equal(text.Length == 0 ? 1 : 2, output.GetArrayLength());
                if (text.Length > 0) Assert.Equal(text, output[0].GetProperty("text").GetString());
                Assert.Equal("data:image/png;base64,aW1hZ2U=", output[output.GetArrayLength() - 1].GetProperty("image_url").GetString());
            }
        }
    }

    /// <summary>【AI】【坏签名】同模型非法思考签名必须在 HTTP 发送前失败，避免悄悄丢失历史。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task InvalidSameModelReasoningFailsBeforeTransport()
    {
        foreach (var api in Apis)
        {
            var model = CreateModel(api);
            using var handler = Handler();
            using var client = new HttpClient(handler);
            var provider = Provider(api, client);
            using var lifetime = provider as IDisposable;
            var stream = provider.Stream(model, new() { Messages = [History(model, new ThinkingContent("x") { ThinkingSignature = "broken" })] }, Options(api));
            await OpenAiResponsesProviderTests.CollectAsync(stream);
            Assert.Equal(StopReason.Error, (await stream.ResultAsync).StopReason);
            Assert.Empty(handler.Requests);
        }
    }

    /// <summary>【AI】【旧历史】null 内容归一后空用户和助手跳过，空工具结果仍提供占位。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task MissingContentIsNormalizedBeforeConversion()
    {
        foreach (var api in Apis)
        {
            var model = CreateModel(api);
            var body = await SendAsync(model, [new UserMessage("x") with { Content = null! }, History(model) with { Content = null! },
                History(model, new ToolCallContent("call", "read", "{}")), new ToolResultMessage("call", null!)]);
            Assert.Equal(2, body.GetProperty("input").GetArrayLength());
            Assert.Equal("(no tool output)", body.GetProperty("input")[1].GetProperty("output").GetString());
        }
    }

    /// <summary>【AI】【Azure 标识】Azure 提供方的复合 ID 仅在 Azure 协议中使用允许列表。</summary>
    [Fact]
    public void AzureProviderAllowlistDependsOnProtocol()
    {
        var source = new AssistantMessage { Provider = "foreign", Api = "other", Model = "other", Content = [] };
        var azure = CreateModel("azure-openai-responses");
        Assert.Equal("call|fc_" + ShortHash.Compute("item"), OpenAiResponsesShared.NormalizeToolCallId("call|item|ignored", azure, source));
        Assert.Equal("call_item_ignored", OpenAiResponsesShared.NormalizeToolCallId("call|item|ignored", azure with { Api = "openai-responses" }, source));
    }

    /// <summary>【AI】【历史夹具】创建与目标身份一致的成功助手。</summary>
    /// <param name="model">目标身份。</param><param name="content">内容块。</param><returns>助手消息。</returns>
    private static AssistantMessage History(Model model, params ContentBlock[] content) => new(content)
    { Provider = model.Provider, Api = model.Api, Model = model.Id, StopReason = StopReason.EndTurn };

    /// <summary>【AI】【模型夹具】提供三种协议的纯内存 HTTP 模型。</summary>
    /// <param name="api">协议。</param><returns>目标模型。</returns>
    private static Model CreateModel(string api) => new()
    {
        Id = "model", Name = "Replay", Api = api, BaseUrl = "https://example.invalid/v1", InputModalities = ["text", "image"],
        Provider = api switch { "azure-openai-responses" => "azure-openai-responses", "openai-codex-responses" => "openai-codex", _ => "openai" }
    };

    /// <summary>【AI】【请求夹具】经完整提供方序列化后解析所捕获的请求。</summary>
    /// <param name="model">目标模型。</param><param name="messages">历史。</param><returns>独立请求 JSON。</returns>
    private static async Task<JsonElement> SendAsync(Model model, IReadOnlyList<ChatMessage> messages)
    {
        using var handler = Handler();
        using var client = new HttpClient(handler);
        var provider = Provider(model.Api, client);
        using var lifetime = provider as IDisposable;
        var stream = provider.Stream(model, new() { Messages = messages }, Options(model.Api));
        await OpenAiResponsesProviderTests.CollectAsync(stream);
        Assert.True(handler.Requests.Count == 1, (await stream.ResultAsync).ErrorMessage);
        using var body = JsonDocument.Parse(handler.CapturedBody);
        return body.RootElement.Clone();
    }

    /// <summary>【AI】【协议夹具】创建真实提供方实现。</summary>
    /// <param name="api">协议。</param><param name="client">内存传输客户端。</param><returns>提供方。</returns>
    private static IStreamProvider Provider(string api, HttpClient client) => api switch
    {
        "azure-openai-responses" => new AzureOpenAiResponsesProvider(client),
        "openai-codex-responses" => new OpenAiCodexResponsesProvider(client),
        _ => new OpenAiResponsesProvider(client)
    };

    /// <summary>【AI】【请求选项】固定使用合成凭据并关闭重试。</summary>
    /// <param name="api">协议。</param><returns>请求选项。</returns>
    private static StreamOptions Options(string api) => new()
    { ApiKey = api == "openai-codex-responses" ? OpenAiResponsesSharedTests.BuildFakeJwt("test") : "synthetic", MaxRetries = 0 };

    /// <summary>【AI】【传输夹具】捕获请求后返回固定错误，不访问网络。</summary>
    /// <returns>内存请求处理器。</returns>
    private static OpenAiResponsesProviderTests.StubHandler Handler() => new(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("captured") });

    /// <summary>【AI】【项类型】读取可选协议类型。</summary>
    /// <param name="item">协议项。</param><returns>类型或 null。</returns>
    private static string? Type(JsonElement item) => item.TryGetProperty("type", out var type) ? type.GetString() : null;
}
