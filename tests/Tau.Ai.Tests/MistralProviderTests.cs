using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Mistral;

namespace Tau.Ai.Tests;

public sealed class MistralProviderTests
{
    [Fact]
    public void RegisterAll_UsesDedicatedMistralProvider()
    {
        var registry = new ProviderRegistry();

        BuiltInProviders.RegisterAll(registry);

        Assert.IsType<MistralProvider>(registry.Get("mistral-conversations"));
    }

    [Fact]
    public async Task Stream_SendsNativeMistralPayloadAndAffinityHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            capturedRequest = request;
            return OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"id":"cmpl_1","choices":[{"delta":{"content":"bonjour"},"finish_reason":null}]}

                data: {"id":"cmpl_1","choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3}}

                """);
        });
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);
        var model = BuildModel();
        var context = new LlmContext
        {
            SystemPrompt = "be concise",
            Messages = [new UserMessage("hello")]
        };

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            context,
            new StreamOptions
            {
                ApiKey = "mistral-key",
                SessionId = "session-1",
                MaxTokens = 42
            }));

        Assert.Equal("/v1/chat/completions", handler.RequestUri!.AbsolutePath);
        Assert.NotNull(capturedRequest);
        Assert.Equal("session-1", capturedRequest!.Headers.GetValues("x-affinity").Single());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("mistral-small-latest", root.GetProperty("model").GetString());
        Assert.Equal(42, root.GetProperty("max_tokens").GetInt32());
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("bonjour", Assert.IsType<TextContent>(Assert.Single(done.Message.Content)).Text);
        Assert.Equal(new Usage(2, 3), done.Message.Usage);
        Assert.Equal(StopReason.EndTurn, done.Message.StopReason);
    }

    [Fact]
    public async Task Stream_ParsesPartialStreamingToolArguments()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            data: {"id":"cmpl_1","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"README"}}]},"finish_reason":null}]}

            data: {"id":"cmpl_1","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":".md\"}"}}]},"finish_reason":null}]}

            data: {"id":"cmpl_1","choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}

            """));
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("read")] },
            new StreamOptions { ApiKey = "mistral-key" }));

        var firstDelta = Assert.IsType<ToolCallDeltaEvent>(events.First(evt => evt is ToolCallDeltaEvent));
        var partialToolCall = Assert.IsType<ToolCallContent>(Assert.Single(firstDelta.Partial.Content));
        Assert.Equal("""{"path":"README"}""", partialToolCall.Arguments);

        var done = Assert.Single(events.OfType<DoneEvent>());
        var toolCall = Assert.IsType<ToolCallContent>(Assert.Single(done.Message.Content));
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("read_file", toolCall.Name);
        Assert.Equal("""{"path":"README.md"}""", toolCall.Arguments);
    }

    [Fact]
    public async Task Stream_AddsToolChoicePromptModeAndReasoningEffortOptions()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")] },
            new MistralOptions
            {
                ApiKey = "mistral-key",
                ToolChoice = "required",
                PromptMode = "reasoning",
                ReasoningEffort = "high"
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.Equal("reasoning", root.GetProperty("prompt_mode").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Stream_AddsFunctionToolChoiceOption()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("use tool")] },
            new MistralOptions
            {
                ApiKey = "mistral-key",
                ToolChoice = MistralToolChoice.Function("read_file")
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var toolChoice = doc.RootElement.GetProperty("tool_choice");
        Assert.Equal("function", toolChoice.GetProperty("type").GetString());
        Assert.Equal("read_file", toolChoice.GetProperty("function").GetProperty("name").GetString());
    }


    [Fact]
    public async Task Stream_NormalizesAssistantToolCallIdsToNineAlphanumericCharacters()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);
        var context = new LlmContext
        {
            Messages =
            [
                new AssistantMessage([new ToolCallContent("call-id-with-symbols|fc_very_long", "read_file", """{"path":"README.md"}""")]),
                new ToolResultMessage("call-id-with-symbols|fc_very_long", [new TextContent("ok")])
            ]
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(BuildModel(), context, new StreamOptions { ApiKey = "mistral-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var toolCallId = messages[0].GetProperty("tool_calls")[0].GetProperty("id").GetString();
        var toolResultId = messages[1].GetProperty("tool_call_id").GetString();
        Assert.NotNull(toolCallId);
        Assert.Matches("^[a-z0-9]{9}$", toolCallId!);
        Assert.Equal(toolCallId, toolResultId);
    }

    [Fact]
    public async Task Stream_DowngradesToolResultImagesForNonVisionModels()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);
        var context = new LlmContext
        {
            Messages =
            [
                new AssistantMessage([new ToolCallContent("call_image", "inspect_image", """{"path":"image.png"}""")]),
                new ToolResultMessage("call_image", [new ImageContent("dGVzdA==", "image/png")])
            ]
        };

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(BuildModel(), context, new StreamOptions { ApiKey = "mistral-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(
            "(tool image omitted: model does not support images)",
            messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamSimple_AddsMistralReasoningEffortForSmallModels()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions { ApiKey = "mistral-key", Reasoning = ThinkingLevel.High }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        Assert.Equal("high", doc.RootElement.GetProperty("reasoning_effort").GetString());
    }

    private static Model BuildModel(bool reasoning = false) => new()
    {
        Id = "mistral-small-latest",
        Name = "Mistral Small",
        Api = "mistral-conversations",
        Provider = "mistral",
        BaseUrl = "https://api.mistral.ai/v1",
        Reasoning = reasoning
    };
}
