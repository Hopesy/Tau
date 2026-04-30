using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Registry;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class OpenAiResponsesProviderTests
{
    [Fact]
    public void RegisterAll_UsesDedicatedResponsesProviders()
    {
        var registry = new ProviderRegistry();

        BuiltInProviders.RegisterAll(registry);

        Assert.IsType<OpenAiResponsesProvider>(registry.Get("openai-responses"));
        Assert.IsType<OpenAiCodexResponsesProvider>(registry.Get("openai-codex-responses"));
    }

    [Fact]
    public async Task Stream_PostsResponsesInputAndTranslatesTextEvents()
    {
        using var handler = new StubHandler(_ => SseResponse(
            """
            data: {"type":"response.created","response":{"id":"resp_1"}}

            data: {"type":"response.output_item.added","item":{"id":"msg_1","type":"message"}}

            data: {"type":"response.output_text.delta","item_id":"msg_1","delta":"hello"}

            data: {"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"output_text","text":"hello","annotations":[]}]}}

            data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":3,"output_tokens":4,"input_tokens_details":{"cached_tokens":1}}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);
        var model = BuildResponsesModel();
        var context = new LlmContext { Messages = [new UserMessage("hi")] };

        var events = await CollectAsync(provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" }));

        Assert.Equal("/v1/responses", handler.RequestUri!.AbsolutePath);
        Assert.Contains("\"input\"", handler.CapturedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"messages\"", handler.CapturedBody, StringComparison.Ordinal);
        Assert.Contains(events, evt => evt is StartEvent);
        Assert.Contains(events, evt => evt is TextStartEvent);
        Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "hello" });
        Assert.Contains(events, evt => evt is TextEndEvent);
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("hello", Assert.IsType<TextContent>(Assert.Single(done.Message.Content)).Text);
        Assert.Equal(new Usage(3, 4, 1), done.Message.Usage);
        Assert.Equal(StopReason.EndTurn, done.Message.StopReason);
    }

    [Fact]
    public async Task Stream_TranslatesToolCallEvents()
    {
        using var handler = new StubHandler(_ => SseResponse(
            """
            data: {"type":"response.output_item.added","item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"read_file","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"path\""}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":":\"README.md\"}"}

            data: {"type":"response.output_item.done","item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"README.md\"}"}}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":1,"output_tokens":2}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);

        var events = await CollectAsync(provider.Stream(BuildResponsesModel(), new LlmContext { Messages = [new UserMessage("read")] }, new StreamOptions { ApiKey = "test-key" }));

        Assert.Contains(events, evt => evt is ToolCallStartEvent);
        Assert.Contains(events, evt => evt is ToolCallDeltaEvent);
        Assert.Contains(events, evt => evt is ToolCallEndEvent);
        var done = Assert.Single(events.OfType<DoneEvent>());
        var toolCall = Assert.IsType<ToolCallContent>(Assert.Single(done.Message.Content));
        Assert.Equal("call_1|fc_1", toolCall.Id);
        Assert.Equal("read_file", toolCall.Name);
        Assert.Equal("""{"path":"README.md"}""", toolCall.Arguments);
        Assert.Equal(StopReason.ToolUse, done.Message.StopReason);
    }

    [Fact]
    public async Task Stream_AppliesServiceTierFromRequestAndResponseToUsageCost()
    {
        using var handler = new StubHandler(_ => SseResponse(
            """
            data: {"type":"response.completed","response":{"status":"completed","service_tier":"priority","usage":{"input_tokens":500000,"output_tokens":250000,"input_tokens_details":{"cached_tokens":100000},"prompt_cache_miss_tokens":50000}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);
        var model = BuildResponsesModel() with
        {
            Cost = new ModelCost(2m, 8m, 0.5m, 1m)
        };

        var events = await CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hi")] },
            new OpenAiResponsesOptions
            {
                ApiKey = "test-key",
                ServiceTier = "priority"
            }));

        using var body = JsonDocument.Parse(handler.CapturedBody);
        Assert.Equal("priority", body.RootElement.GetProperty("service_tier").GetString());
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("priority", done.Message.Usage!.Value.ServiceTier);
        Assert.Equal(6.20m, ModelCatalog.CalculateCost(model, done.Message.Usage.Value).Total);
    }

    [Fact]
    public async Task Stream_AddsGitHubCopilotStaticAndDynamicHeaders_ForUserInitiatedTurn()
    {
        using var handler = new StubHandler(_ => SseResponse(
            """
            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);
        var model = new ModelCatalog().GetModel("github-copilot", "gpt-4o");
        var context = new LlmContext
        {
            Messages =
            [
                new AssistantMessage([new TextContent("older")]),
                new UserMessage("latest")
            ]
        };

        _ = await CollectAsync(provider.Stream(model, context, new StreamOptions { ApiKey = "copilot-key" }));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("user", request.Headers.GetValues("X-Initiator").Single());
        Assert.Equal("conversation-edits", request.Headers.GetValues("Openai-Intent").Single());
        Assert.Equal("GitHubCopilotChat/0.35.0", request.Headers.GetValues("User-Agent").Single());
        Assert.Equal("vscode/1.107.0", request.Headers.GetValues("Editor-Version").Single());
        Assert.Equal("copilot-chat/0.35.0", request.Headers.GetValues("Editor-Plugin-Version").Single());
        Assert.Equal("vscode-chat", request.Headers.GetValues("Copilot-Integration-Id").Single());
        Assert.False(request.Headers.Contains("Copilot-Vision-Request"));
    }

    [Fact]
    public async Task Stream_AddsCopilotVisionHeader_AndPreservesToolResultImages()
    {
        using var handler = new StubHandler(_ => SseResponse(
            """
            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);
        var model = new ModelCatalog().GetModel("github-copilot", "gpt-4o");
        var context = new LlmContext
        {
            Messages =
            [
                new AssistantMessage([new ToolCallContent("call_img", "inspect_image", "{\"path\":\"image.png\"}")]),
                new ToolResultMessage("call_img", [new ImageContent("dGVzdA==", "image/png")])
            ]
        };

        _ = await CollectAsync(provider.Stream(model, context, new StreamOptions { ApiKey = "copilot-key" }));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("agent", request.Headers.GetValues("X-Initiator").Single());
        Assert.Equal("true", request.Headers.GetValues("Copilot-Vision-Request").Single());

        using var body = JsonDocument.Parse(handler.CapturedBody);
        var input = body.RootElement.GetProperty("input");
        var toolResult = input.EnumerateArray().Single(item =>
            item.GetProperty("type").GetString() == "function_call_output");
        var output = toolResult.GetProperty("output");
        Assert.Equal(JsonValueKind.Array, output.ValueKind);
        var image = output.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_image");
        Assert.Equal("auto", image.GetProperty("detail").GetString());
        Assert.Equal("data:image/png;base64,dGVzdA==", image.GetProperty("image_url").GetString());
    }

    internal static Model BuildResponsesModel() => new()
    {
        Id = "gpt-5.4",
        Name = "GPT-5.4",
        Api = "openai-responses",
        Provider = "openai",
        BaseUrl = "https://example.invalid/v1",
        Reasoning = true
    };

    internal static async Task<List<StreamEvent>> CollectAsync(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    internal static HttpResponseMessage SseResponse(string sse) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
    };

    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public string CapturedBody { get; private set; } = string.Empty;
        public Uri? RequestUri { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Requests.Add(request);
            CapturedBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return _responseFactory(request);
        }
    }
}
