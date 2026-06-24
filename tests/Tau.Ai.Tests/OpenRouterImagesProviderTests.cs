using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenRouter;

namespace Tau.Ai.Tests;

public sealed class OpenRouterImagesProviderTests
{
    [Fact]
    public async Task GenerateImages_SendsOpenRouterChatRequestAndParsesImages()
    {
        using var handler = new StubHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id": "gen-123",
                      "choices": [
                        {
                          "message": {
                            "content": "caption",
                            "images": [
                              { "image_url": { "url": "data:image/png;base64,aW1hZ2U=" } },
                              { "image_url": "data:image/jpeg;base64,anBlZw==" },
                              { "image_url": { "url": "https://example.invalid/not-data-url.png" } }
                            ]
                          }
                        }
                      ],
                      "usage": {
                        "prompt_tokens": 12,
                        "completion_tokens": 5,
                        "prompt_tokens_details": {
                          "cached_tokens": 4,
                          "cache_write_tokens": 1
                        }
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            }
        };
        using var client = new HttpClient(handler);
        var provider = new OpenRouterImagesProvider(client);
        var responseStatus = 0;
        string? responseHeader = null;
        handler.Response.Headers.TryAddWithoutValidation("X-Trace", "trace-1");

        var result = await provider.GenerateImagesAsync(
            BuildModel(),
            new ImagesContext(
            [
                new TextContent("hello \ud800"),
                new ImageContent("aW5wdXQ=", "image/png")
            ]),
            new ImagesOptions
            {
                ApiKey = "test-key",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Client"] = "client-header"
                },
                OnPayload = (payload, _) =>
                {
                    var body = Assert.IsType<Dictionary<string, object>>(payload);
                    body["metadata"] = new Dictionary<string, object> { ["source"] = "test" };
                    return ValueTask.FromResult<object?>(body);
                },
                OnResponse = (response, _) =>
                {
                    responseStatus = response.Status;
                    responseHeader = response.Headers["X-Trace"];
                    return ValueTask.CompletedTask;
                }
            });

        Assert.Equal(ImagesStopReason.Stop, result.StopReason);
        Assert.Equal("gen-123", result.ResponseId);
        Assert.Equal("caption", Assert.IsType<TextContent>(result.Output[0]).Text);
        Assert.Equal("image/png", Assert.IsType<ImageContent>(result.Output[1]).MimeType);
        Assert.Equal("aW1hZ2U=", Assert.IsType<ImageContent>(result.Output[1]).Data);
        Assert.Equal("image/jpeg", Assert.IsType<ImageContent>(result.Output[2]).MimeType);
        Assert.Equal("anBlZw==", Assert.IsType<ImageContent>(result.Output[2]).Data);
        Assert.Equal(3, result.Output.Count);
        Assert.Equal(new Usage(
            InputTokens: 8,
            OutputTokens: 5,
            CacheReadTokens: 3,
            CacheWriteTokens: 1,
            Cost: new UsageCost(
                Input: 0.000016m,
                Output: 0.00002m,
                CacheRead: 0.0000015m,
                CacheWrite: 0.000001m)), result.Usage);
        Assert.Equal(200, responseStatus);
        Assert.Equal("trace-1", responseHeader);

        Assert.Single(handler.SawRequests);
        var request = handler.SawRequests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.invalid/api/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization.Parameter);
        Assert.Equal("client-header", request.Headers.GetValues("X-Client").Single());

        using var document = JsonDocument.Parse(handler.CapturedBody!);
        var root = document.RootElement;
        Assert.Equal("openrouter/test-image", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("image", root.GetProperty("modalities")[0].GetString());
        Assert.Equal("text", root.GetProperty("modalities")[1].GetString());
        var content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("hello ", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64,aW5wdXQ=",
            content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("test", root.GetProperty("metadata").GetProperty("source").GetString());
    }

    [Fact]
    public async Task GenerateImages_WithoutApiKeyReturnsErrorWithoutRequest()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenRouterImagesProvider(client);

        var result = await provider.GenerateImagesAsync(
            BuildModel(),
            new ImagesContext([new TextContent("draw")]),
            new ImagesOptions());

        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Contains("No API key", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(handler.SawRequests);
    }

    [Fact]
    public async Task GenerateImages_WhenSignalAlreadyCanceledReturnsAbortedWithoutRequest()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenRouterImagesProvider(client);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await provider.GenerateImagesAsync(
            BuildModel(),
            new ImagesContext([new TextContent("draw")]),
            new ImagesOptions
            {
                ApiKey = "test-key",
                Signal = cts.Token
            });

        Assert.Equal(ImagesStopReason.Aborted, result.StopReason);
        Assert.Equal("Request was aborted", result.ErrorMessage);
        Assert.Empty(handler.SawRequests);
    }

    [Fact]
    public async Task ImageFunctions_ResolvesScopedEnvApiKeyAndDispatchesRegistry()
    {
        var provider = new CapturingImagesProvider();
        var registry = new ImagesProviderRegistry();
        registry.Register(provider.Api, provider);

        var result = await ImageFunctions.GenerateImagesAsync(
            registry,
            BuildModel(),
            new ImagesContext([new TextContent("draw")]),
            new ImagesOptions
            {
                Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OPENROUTER_API_KEY"] = "scoped-openrouter-key"
                }
            });

        Assert.Equal(ImagesStopReason.Stop, result.StopReason);
        Assert.Equal("scoped-openrouter-key", provider.CapturedOptions!.ApiKey);
        Assert.Equal("draw", Assert.IsType<TextContent>(provider.CapturedContext.Input[0]).Text);
    }

    [Fact]
    public async Task ImageFunctions_DefaultGenerateImagesUsesBuiltInOpenRouterRegistry()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        scope.Set("OPENROUTER_API_KEY", null);

        var result = await ImageFunctions.GenerateImagesAsync(
            BuildModel(),
            new ImagesContext([new TextContent("draw")]),
            new ImagesOptions()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Contains("No API key for provider: openrouter", result.ErrorMessage, StringComparison.Ordinal);
    }

    private static ImagesModel BuildModel() => new()
    {
        Id = "openrouter/test-image",
        Name = "OpenRouter Test Image",
        Api = "openrouter-images",
        Provider = "openrouter",
        BaseUrl = "https://example.invalid/api/v1",
        InputModalities = ["text", "image"],
        OutputModalities = ["image", "text"],
        Cost = new ModelCost(2m, 4m, 0.5m, 1m)
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        public HttpResponseMessage? Response { get; set; }
        public List<HttpRequestMessage> SawRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawRequests.Add(request);
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return Response ?? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("stubbed-openrouter-images-error", Encoding.UTF8, "text/plain")
            };
        }
    }

    private sealed class CapturingImagesProvider : IImagesProvider
    {
        public string Api => "openrouter-images";
        public ImagesContext CapturedContext { get; private set; }
        public ImagesOptions? CapturedOptions { get; private set; }

        public Task<AssistantImages> GenerateImagesAsync(
            ImagesModel model,
            ImagesContext context,
            ImagesOptions options)
        {
            CapturedContext = context;
            CapturedOptions = options;
            return Task.FromResult(new AssistantImages
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Output = [new TextContent("ok")]
            });
        }
    }
}
