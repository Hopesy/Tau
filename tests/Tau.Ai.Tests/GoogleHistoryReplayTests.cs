// 作者：xxx
using System.Net;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Google;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【Google】【历史回归】在三种真实 HTTP 请求路径检查签名、图片和工具配对。</summary>
public sealed partial class GoogleHistoryReplayTests
{
    /// <summary>【Google】【来源隔离】验证同模型签名与跨模型、提供方、协议的降级处理。</summary>
    /// <param name="api">传输协议。</param><param name="source">来源差异。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language", "same")]
    [InlineData("google-generative-language", "model")]
    [InlineData("google-generative-language", "provider")]
    [InlineData("google-generative-language", "api")]
    [InlineData("google-vertex", "same")]
    [InlineData("google-vertex", "model")]
    [InlineData("google-vertex", "provider")]
    [InlineData("google-vertex", "api")]
    [InlineData("google-gemini-cli", "same")]
    [InlineData("google-gemini-cli", "model")]
    [InlineData("google-gemini-cli", "provider")]
    [InlineData("google-gemini-cli", "api")]
    public async Task IdentityControlsSignaturesAndCallIds(string api, string source)
    {
        var model = Model(api);
        var id = "call|" + new string('a', 80);
        var history = History(model, new ThinkingContent("plan") { ThinkingSignature = "cGxhbg==" },
            new TextContent("") { TextSignature = "dGV4dA==" }, new ThinkingContent(" ") { ThinkingSignature = "YQ==" },
            new TextContent("answer") { TextSignature = "YmJi" }, new ToolCallContent(id, "read", "{}") { ThoughtSignature = "dG9vbA==" });
        history = source switch
        {
            "model" => history with { Model = "other" }, "provider" => history with { Provider = "other" },
            "api" => history with { Api = "anthropic-messages" }, _ => history
        };
        var body = await SendAsync(model, new() { Messages = [history, new ToolResultMessage(id, [new TextContent("ok")])] });
        var contents = body.GetProperty("contents");
        var parts = contents[0].GetProperty("parts");
        var same = source == "same";
        Assert.Equal(same ? 5 : 3, parts.GetArrayLength());
        Assert.Equal(same, parts[0].TryGetProperty("thought", out _));
        Assert.Equal("plan", parts[0].GetProperty("text").GetString());
        if (same)
        {
            Assert.Equal("dGV4dA==", parts[1].GetProperty("thoughtSignature").GetString());
            Assert.Equal("", parts[1].GetProperty("text").GetString());
            Assert.Equal(" ", parts[2].GetProperty("text").GetString());
            Assert.Equal("YQ==", parts[2].GetProperty("thoughtSignature").GetString());
        }
        else Assert.All(parts.EnumerateArray(), part => Assert.False(part.TryGetProperty("thoughtSignature", out _)));
        var call = parts[parts.GetArrayLength() - 1].GetProperty("functionCall");
        var result = contents[1].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal(same ? id : "call_" + new string('a', 59), call.GetProperty("id").GetString());
        Assert.Equal(call.GetProperty("id").GetString(), result.GetProperty("id").GetString());
        Assert.Equal("read", result.GetProperty("name").GetString());
        Assert.Equal("ok", result.GetProperty("response").GetProperty("output").GetString());
        Assert.Equal(id, history.Content.OfType<ToolCallContent>().Single().Id);
    }

    /// <summary>【Google】【签名验证】仅保留主线认可的 Base64，空白签名内容不可被无条件过滤。</summary>
    /// <param name="api">传输协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task SignatureValidationPreservesOnlyValidEmptyParts(string api)
    {
        var model = Model(api);
        foreach (var signature in new string?[] { null, "", "abc", "bad_", "YQ==\n", "====", "YQ==", "abcd", "AAA=" })
        {
            var valid = signature is "YQ==" or "abcd" or "AAA=";
            var body = await SendAsync(model, new() { Messages = [History(model,
                new TextContent("") { TextSignature = signature }, new ThinkingContent("\n") { ThinkingSignature = signature },
                new TextContent("answer") { TextSignature = signature })] });
            var parts = body.GetProperty("contents")[0].GetProperty("parts");
            Assert.Equal(valid ? 3 : 1, parts.GetArrayLength());
            Assert.All(parts.EnumerateArray(), part => Assert.Equal(valid, part.TryGetProperty("thoughtSignature", out _)));
        }
    }

    /// <summary>【Google】【图片矩阵】检查 Gemini 版本与其他后端的调用 ID、图片位置和完整错误正文。</summary>
    /// <param name="api">传输协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task ToolImagesFollowModelCapabilities(string api)
    {
        foreach (var (id, hasId, inline) in new[] { ("gemini-2.5-flash", false, false), ("gemini-live-2.5-flash", false, false),
            ("GEMINI-3.1-flash", true, true), ("gemini-4-pro", true, true), ("claude-sonnet-4", true, true),
            ("gpt-oss-120b", true, true), ("CLAUDE-sonnet", false, true), ("gemma-4", false, true) })
        foreach (var vision in new[] { true, false })
        {
            var model = Model(api) with { Id = id, InputModalities = vision ? ["text", "image"] : ["text"] };
            var body = await SendAsync(model, new() { Messages = [History(model,
                new ToolCallContent("first", "inspect", "{}"), new ToolCallContent("second", "read", "{}")),
                new ToolResultMessage("first", [new TextContent("a"), new ImageContent("YQ==", "image/png"), new TextContent("b")], true),
                new ToolResultMessage("second", []) { ToolName = "override_name" }] });
            var contents = body.GetProperty("contents");
            var function = contents[0].GetProperty("parts")[0].GetProperty("functionCall");
            Assert.Equal(hasId, function.TryGetProperty("id", out _));
            var first = contents[1].GetProperty("parts")[0].GetProperty("functionResponse");
            Assert.Equal(hasId, first.TryGetProperty("id", out _));
            Assert.Equal("inspect", first.GetProperty("name").GetString());
            Assert.Equal(vision ? "a\nb" : "a\n(tool image omitted: model does not support images)\nb", first.GetProperty("response").GetProperty("error").GetString());
            Assert.Equal(vision && inline, first.TryGetProperty("parts", out var images));
            var separate = vision && !inline;
            Assert.Equal(separate ? 4 : 2, contents.GetArrayLength());
            if (vision)
            {
                if (separate)
                {
                    Assert.Equal("Tool result image:", contents[2].GetProperty("parts")[0].GetProperty("text").GetString());
                    images = contents[2].GetProperty("parts");
                }
                var image = images[separate ? 1 : 0].GetProperty("inlineData");
                Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
                Assert.Equal("YQ==", image.GetProperty("data").GetString());
            }
            var last = contents[separate ? 3 : 1].GetProperty("parts")[separate ? 0 : 1].GetProperty("functionResponse");
            Assert.Equal("override_name", last.GetProperty("name").GetString());
            Assert.Equal("", last.GetProperty("response").GetProperty("output").GetString());
        }
    }

    /// <summary>【Google】【历史边界】过滤无内容及失败消息，补齐缺失结果，并折叠直接入口的系统增量。</summary>
    /// <param name="api">传输协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("google-generative-language")]
    [InlineData("google-vertex")]
    [InlineData("google-gemini-cli")]
    public async Task EmptyFailedAndUnfinishedHistoryHasConsistentBoundaries(string api)
    {
        var model = Model(api);
        var body = await SendAsync(model, new() { SystemPrompt = "base", Messages = [new UserMessage((IReadOnlyList<ContentBlock>)null!),
            History(model, new TextContent(" \n")), History(model) with { Content = null! }, new UserMessage(""),
            History(model, new ToolCallContent("failed", "read", "{}")) with { StopReason = StopReason.Error },
            History(model, new ToolCallContent("aborted", "read", "{}")) with { StopReason = StopReason.Aborted },
            History(model, new ToolCallContent("missing", "read", "null")), new SystemMessage("later"), new UserMessage("next")] });
        var contents = body.GetProperty("contents");
        Assert.Equal(4, contents.GetArrayLength());
        Assert.Equal("", contents[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Empty(contents[1].GetProperty("parts")[0].GetProperty("functionCall").GetProperty("args").EnumerateObject());
        var result = contents[2].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("missing", result.GetProperty("id").GetString());
        Assert.Equal("read", result.GetProperty("name").GetString());
        Assert.Equal("No result provided", result.GetProperty("response").GetProperty("error").GetString());
        Assert.Equal("base\n\nlater", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    /// <summary>【Google】【测试模型】创建使用内存 HTTP 的视觉模型。</summary>
    /// <param name="api">传输协议。</param><returns>模型。</returns>
    private static Model Model(string api) => new() { Id = "gemini-3-flash", Name = "Google fixture", Api = api,
        Provider = api, BaseUrl = "https://google.example/v1", InputModalities = ["text", "image"] };

    /// <summary>【Google】【身份夹具】构造同模型的已完成助手历史。</summary>
    /// <param name="model">来源模型。</param><param name="content">内容块。</param><returns>助手消息。</returns>
    private static AssistantMessage History(Model model, params ContentBlock[] content) => new(content)
    { Provider = model.Provider, Api = model.Api, Model = model.Id, StopReason = StopReason.EndTurn };

    /// <summary>【Google】【请求捕获】通过实际提供方捕获报文，CLI 返回内层 request 便于统一断言。</summary>
    /// <param name="model">目标模型。</param><param name="context">测试会话。</param><param name="options">可选原生或简化选项。</param>
    /// <param name="expectedError">预期发送前错误，未设置时必须发生 HTTP 请求。</param>
    /// <param name="configuration">可选统一入口配置。</param><returns>独立请求 JSON。</returns>
    private static async Task<JsonElement> SendAsync(Model model, LlmContext context, StreamOptions? options = null, string? expectedError = null,
        ModelConfigurationStore? configuration = null)
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("fixture") });
        using var client = new HttpClient(handler);
        IStreamProvider provider = model.Api switch
        {
            "google-vertex" => new GoogleVertexProvider(client), "google-gemini-cli" => new GoogleGeminiCliProvider(client),
            _ => new GoogleProvider(client)
        };
        options = (options ?? new StreamOptions()) with
        { ApiKey = model.Api == "google-gemini-cli" ? """{"token":"synthetic","projectId":"fixture"}""" : "synthetic", MaxRetries = 0 };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        var events = await OpenAiResponsesProviderTests.CollectAsync(options is SimpleStreamOptions simple
            ? configuration is null ? provider.StreamSimple(model, context, simple) : StreamFunctions.StreamSimple(registry, model, context, simple, configuration)
            : provider.Stream(model, context, options));
        if (expectedError is not null)
        {
            Assert.Empty(handler.Requests);
            Assert.Contains(expectedError, Assert.Single(events.OfType<ErrorEvent>()).Error);
            return default;
        }
        Assert.True(handler.Requests.Count == 1, string.Join("; ", events.OfType<ErrorEvent>().Select(error => error.Error)));
        using var document = JsonDocument.Parse(handler.CapturedBody);
        return (model.Api == "google-gemini-cli" ? document.RootElement.GetProperty("request") : document.RootElement).Clone();
    }
}
