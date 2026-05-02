using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers.OpenAi;

namespace Tau.Ai.Tests;

public sealed class OpenAiProviderSerializationTests
{
    [Fact]
    public async Task Stream_WithToolSchema_DoesNotFailOnListObjectSerialization()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-chat-completions",
            Provider = "openai",
            BaseUrl = "https://example.invalid/v1"
        };

        var toolSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" }
          },
          "required": ["path"]
        }
        """;

        var context = new LlmContext
        {
            Messages =
            [
                new UserMessage([new TextContent("hello")])
            ],
            Tools =
            [
                new Tool("read_file", "Read a file", JsonDocument.Parse(toolSchema).RootElement.Clone())
            ]
        };

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });

        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Contains("stubbed-openai-error", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unable to cast object of type", error.Error, StringComparison.Ordinal);
        Assert.NotNull(handler.CapturedBody);
        Assert.Contains("\"tools\"", handler.CapturedBody, StringComparison.Ordinal);
        Assert.Contains("\"function\"", handler.CapturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamSimple_WithOpenAiCompatibility_AdjustsRequestBody()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "glm-4.6",
            Name = "GLM-4.6",
            Api = "openai-chat-completions",
            Provider = "zai",
            BaseUrl = "https://api.z.ai/api/coding/paas/v4",
            Reasoning = true,
            Compat = new ModelCompatibility
            {
                SupportsStore = true,
                SupportsDeveloperRole = false,
                SupportsUsageInStreaming = false,
                MaxTokensField = "max_tokens",
                ThinkingFormat = "zai",
                ZaiToolStream = true,
                SupportsStrictMode = true
            }
        };

        var context = new LlmContext
        {
            SystemPrompt = "follow instructions",
            Messages =
            [
                new UserMessage(
                [
                    new TextContent("hello"),
                    new ImageContent("aGVsbG8=", "image/png")
                ])
            ],
            Tools =
            [
                new Tool("read_file", "Read a file", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone())
            ]
        };

        await DrainAsync(provider.StreamSimple(
            model,
            context,
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                MaxTokens = 42,
                Reasoning = ThinkingLevel.High
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("stream_options", out _));
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(42, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("enable_thinking").GetBoolean());
        Assert.True(root.GetProperty("tool_stream").GetBoolean());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("text", root.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("messages")[1].GetProperty("content").GetArrayLength());
        Assert.False(root.GetProperty("tools")[0].GetProperty("function").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task StreamSimple_WithRoutingCompatibility_AddsProviderRouting()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "openrouter/openai/gpt-5.4",
            Name = "GPT-5.4 via OpenRouter",
            Api = "openai-chat-completions",
            Provider = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            Reasoning = true,
            Compat = new ModelCompatibility
            {
                ThinkingFormat = "openrouter",
                MaxTokensField = "max_completion_tokens",
                ReasoningEffortMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["xhigh"] = "max"
                },
                OpenRouterRouting = new Dictionary<string, object>
                {
                    ["allow_fallbacks"] = false,
                    ["order"] = new[] { "anthropic", "amazon-bedrock" }
                }
            }
        };

        await DrainAsync(provider.StreamSimple(
            model,
            new LlmContext { Messages = [new UserMessage("route this")] },
            new SimpleStreamOptions
            {
                ApiKey = "test-key",
                MaxTokens = 100,
                Reasoning = ThinkingLevel.ExtraHigh
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal(100, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("max", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(root.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        Assert.Equal("anthropic", root.GetProperty("provider").GetProperty("order")[0].GetString());
    }

    [Fact]
    public async Task Stream_WithVercelGatewayRouting_AddsProviderOptions()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "moonshotai/kimi-k2.5",
            Name = "Kimi K2.5",
            Api = "openai-chat-completions",
            Provider = "vercel-ai-gateway",
            BaseUrl = "https://ai-gateway.vercel.sh/v1",
            Compat = new ModelCompatibility
            {
                VercelGatewayRouting = new VercelGatewayRouting
                {
                    Only = ["fireworks"],
                    Order = ["fireworks", "novita"]
                }
            }
        };

        await DrainAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("route this")] },
            new StreamOptions { ApiKey = "test-key" }));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var gateway = doc.RootElement.GetProperty("providerOptions").GetProperty("gateway");
        Assert.Equal("fireworks", gateway.GetProperty("only")[0].GetString());
        Assert.Equal("novita", gateway.GetProperty("order")[1].GetString());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("stubbed-openai-error", Encoding.UTF8, "text/plain")
            };
        }
    }

    private static async Task DrainAsync(IAsyncEnumerable<StreamEvent> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }
}
