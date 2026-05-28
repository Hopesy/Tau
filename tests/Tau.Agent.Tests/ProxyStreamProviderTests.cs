using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tau.Agent.Proxy;
using Tau.Ai;
using Tau.Ai.Streaming;

namespace Tau.Agent.Tests;

public sealed class ProxyStreamProviderTests
{
    [Fact]
    public async Task StreamSimple_PostsProxyRequestAndReconstructsStreamEvents()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                BuildSse(
                    """{"type":"start"}""",
                    """{"type":"text_start","contentIndex":0}""",
                    """{"type":"text_delta","contentIndex":0,"delta":"Hel"}""",
                    """{"type":"text_delta","contentIndex":0,"delta":"lo"}""",
                    """{"type":"text_end","contentIndex":0,"contentSignature":"sig-text"}""",
                    """{"type":"toolcall_start","contentIndex":1,"id":"call-1","toolName":"lookup"}""",
                    """{"type":"toolcall_delta","contentIndex":1,"delta":"{\"query\":\"par"}""",
                    """{"type":"toolcall_delta","contentIndex":1,"delta":"is\"}"}""",
                    """{"type":"toolcall_end","contentIndex":1}""",
                    """{"type":"done","reason":"toolUse","usage":{"input":11,"output":4,"cacheRead":2,"cacheWrite":1,"serviceTier":"flex"}}"""),
                Encoding.UTF8,
                "text/event-stream")
        });

        var provider = new ProxyStreamProvider(new HttpClient(handler), api: "proxy-stream");
        var model = new Model
        {
            Id = "test-model",
            Name = "Test Model",
            Api = provider.Api,
            Provider = "test-provider",
            BaseUrl = "https://proxy.example",
            InputModalities = ["text", "image"]
        };
        var context = new LlmContext(
            "system prompt",
            [
                new UserMessage("hello"),
                new AssistantMessage([new TextContent("ack")])
            ],
            [
                new Tool("lookup", "Look up a record.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone())
            ]);

        var stream = provider.StreamSimple(
            model,
            context,
            new ProxyStreamOptions
            {
                ApiKey = "proxy-token",
                Temperature = 0.2f,
                MaxTokens = 128,
                Reasoning = ThinkingLevel.High
            });

        var events = await CollectAsync(stream);

        Assert.Collection(
            events,
            evt => Assert.IsType<StartEvent>(evt),
            evt => Assert.IsType<TextStartEvent>(evt),
            evt => Assert.IsType<TextDeltaEvent>(evt),
            evt => Assert.IsType<TextDeltaEvent>(evt),
            evt => Assert.IsType<TextEndEvent>(evt),
            evt => Assert.IsType<ToolCallStartEvent>(evt),
            evt => Assert.IsType<ToolCallDeltaEvent>(evt),
            evt => Assert.IsType<ToolCallDeltaEvent>(evt),
            evt => Assert.IsType<ToolCallEndEvent>(evt),
            evt => Assert.IsType<DoneEvent>(evt));

        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Message.StopReason);
        Assert.Equal(11, done.Message.Usage?.InputTokens);
        Assert.Equal(4, done.Message.Usage?.OutputTokens);
        Assert.Equal(2, done.Message.Usage?.CacheReadTokens);
        Assert.Equal(1, done.Message.Usage?.CacheWriteTokens);
        Assert.Equal("flex", done.Message.Usage?.ServiceTier);

        var text = Assert.Single(done.Message.Content.OfType<TextContent>());
        Assert.Equal("Hello", text.Text);
        Assert.Equal("sig-text", text.TextSignature);

        var toolCall = Assert.Single(done.Message.Content.OfType<ToolCallContent>());
        Assert.Equal("call-1", toolCall.Id);
        Assert.Equal("lookup", toolCall.Name);
        Assert.Equal("""{"query":"paris"}""", toolCall.Arguments);

        Assert.Equal("https://proxy.example/api/stream", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer proxy-token", handler.LastRequest?.Headers.Authorization?.ToString());

        using var body = JsonDocument.Parse(handler.LastBody ?? throw new Xunit.Sdk.XunitException("Request body missing."));
        var modelJson = body.RootElement.GetProperty("model");
        Assert.Equal("test-model", modelJson.GetProperty("id").GetString());
        Assert.Equal("test-provider", modelJson.GetProperty("provider").GetString());
        Assert.Equal("proxy-stream", modelJson.GetProperty("api").GetString());
        Assert.Equal("https://proxy.example", modelJson.GetProperty("baseUrl").GetString());

        var contextJson = body.RootElement.GetProperty("context");
        Assert.Equal("system prompt", contextJson.GetProperty("systemPrompt").GetString());
        var messages = contextJson.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("hello", messages[0].GetProperty("content").EnumerateArray().First().GetProperty("text").GetString());
        Assert.Equal("ack", messages[1].GetProperty("content").EnumerateArray().First().GetProperty("text").GetString());
        Assert.Equal("lookup", contextJson.GetProperty("tools").EnumerateArray().First().GetProperty("name").GetString());

        var optionsJson = body.RootElement.GetProperty("options");
        Assert.Equal(0.2, optionsJson.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(128, optionsJson.GetProperty("maxTokens").GetInt32());
        Assert.Equal("high", optionsJson.GetProperty("reasoning").GetString());
    }

    [Fact]
    public async Task StreamSimple_WhenProxyReturnsErrorYieldsErrorEvent()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"error":{"message":"auth unavailable"}}""", Encoding.UTF8, "application/json")
        });

        var provider = new ProxyStreamProvider(new HttpClient(handler), api: "proxy-stream");
        var model = new Model
        {
            Id = "test-model",
            Name = "Test Model",
            Api = provider.Api,
            Provider = "test-provider",
            BaseUrl = "https://proxy.example"
        };

        var stream = provider.StreamSimple(
            model,
            new LlmContext(null, [new UserMessage("hello")], null),
            new ProxyStreamOptions { ApiKey = "secret-token" });

        var events = await CollectAsync(stream);
        var error = Assert.Single(events);
        var errorEvent = Assert.IsType<ErrorEvent>(error);
        Assert.Equal("Proxy error: auth unavailable", errorEvent.Error);
        Assert.DoesNotContain("secret-token", errorEvent.Error, StringComparison.Ordinal);

        var result = await stream.ResultAsync;
        Assert.Equal("Proxy error: auth unavailable", result.ErrorMessage);
        Assert.Equal("secret-token", handler.LastRequest?.Headers.Authorization?.Parameter);
    }

    private static async Task<List<StreamEvent>> CollectAsync(AssistantMessageStream stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    private static string BuildSse(params string[] events) =>
        string.Join("\n\n", events.Select(evt => $"data: {evt}")) + "\n\n";

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return responder(request);
        }
    }
}
