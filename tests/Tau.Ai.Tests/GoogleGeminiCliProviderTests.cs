using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Google;

namespace Tau.Ai.Tests;

public sealed class GoogleGeminiCliProviderTests
{
    [Fact]
    public async Task Stream_PostsGeminiCliHeadersAndRequestBody()
    {
        using var handler = new RecordingHandler(_ => SseResponse("hello"));
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);
        var model = BuildModel(providerId: "google-gemini-cli", baseUrl: "https://cloudcode-pa.googleapis.com");

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext("sys", [new UserMessage("hi")], Tools: null),
            new StreamOptions
            {
                ApiKey = ApiKey(),
                SessionId = "session-1",
                Temperature = 0.3f,
                MaxTokens = 123
            }));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse", request.Uri.ToString());
        Assert.Equal("Bearer token", Assert.Single(request.Headers["Authorization"]));
        Assert.Equal("google-cloud-sdk vscode_cloudshelleditor/0.1", string.Join(" ", request.Headers["User-Agent"]));
        Assert.Equal("gl-dotnet/tau", Assert.Single(request.Headers["X-Goog-Api-Client"]));
        Assert.True(request.Headers.ContainsKey("Client-Metadata"));
        Assert.DoesNotContain("anthropic-beta", request.Headers.Keys);
        Assert.Contains("\"project\":\"project\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"gemini-2.5-flash\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\":\"session-1\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"userAgent\":\"tau-coding-agent\"", request.Body, StringComparison.Ordinal);
        Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "hello" });
    }

    [Fact]
    public async Task Stream_AntigravityUsesSandboxFallbackAndAgentPayload()
    {
        using var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Host.StartsWith("daily-", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("blocked")
                };
            }

            return SseResponse("fallback ok");
        });
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);
        var model = BuildModel("gemini-3.1-pro-high", "google-antigravity", baseUrl: string.Empty);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext("custom sys", [new UserMessage("hi")], Tools: null),
            new StreamOptions { ApiKey = ApiKey(), MaxRetryDelay = TimeSpan.Zero }));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:streamGenerateContent?alt=sse", handler.Requests[0].Uri.ToString());
        Assert.Equal("https://autopush-cloudcode-pa.sandbox.googleapis.com/v1internal:streamGenerateContent?alt=sse", handler.Requests[1].Uri.ToString());
        var second = handler.Requests[1];
        Assert.Equal("antigravity/1.21.9 darwin/arm64", string.Join(" ", second.Headers["User-Agent"]));
        Assert.False(second.Headers.ContainsKey("X-Goog-Api-Client"));
        Assert.False(second.Headers.ContainsKey("Client-Metadata"));
        Assert.Contains("\"requestType\":\"agent\"", second.Body, StringComparison.Ordinal);
        Assert.Contains("\"userAgent\":\"antigravity\"", second.Body, StringComparison.Ordinal);
        Assert.Contains("You are Antigravity", second.Body, StringComparison.Ordinal);
        Assert.Contains("custom sys", second.Body, StringComparison.Ordinal);
        Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "fallback ok" });
    }

    [Fact]
    public async Task Stream_AddsAnthropicBetaForAntigravityClaudeThinkingModels()
    {
        using var handler = new RecordingHandler(_ => SseResponse("claude"));
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);
        var model = BuildModel("claude-sonnet-4-5-thinking", "google-antigravity", baseUrl: "https://cloudcode-pa.googleapis.com", reasoning: true);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = ApiKey() }));

        Assert.Equal("interleaved-thinking-2025-05-14", Assert.Single(handler.Requests[0].Headers["anthropic-beta"]));
    }

    [Fact]
    public async Task StreamSimple_UsesCustomThinkingBudgetsAndClampsExtraHigh()
    {
        using var handler = new RecordingHandler(_ => SseResponse("budget"));
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);
        var model = BuildModel(providerId: "google-gemini-cli", baseUrl: "https://cloudcode-pa.googleapis.com");

        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            model,
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = ApiKey(),
                Reasoning = ThinkingLevel.ExtraHigh,
                ThinkingBudgets = new ThinkingBudgets { High = 12_345 }
            }));

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var thinking = doc.RootElement
            .GetProperty("request")
            .GetProperty("generationConfig")
            .GetProperty("thinkingConfig");
        Assert.Equal(12_345, thinking.GetProperty("thinkingBudget").GetInt32());
    }

    [Fact]
    public async Task Stream_AddsToolChoiceAndExplicitThinkingOptions()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""");
        using var handler = new RecordingHandler(_ => SseResponse("tool choice"));
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);
        var model = BuildModel(providerId: "google-gemini-cli", baseUrl: "https://cloudcode-pa.googleapis.com");

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext
            {
                Messages = [new UserMessage("think")],
                Tools = [new Tool("read_file", "Read file", schema.RootElement.Clone())]
            },
            new GoogleGeminiCliOptions
            {
                ApiKey = ApiKey(),
                ToolChoice = "any",
                Thinking = new GoogleThinkingOptions
                {
                    Enabled = true,
                    BudgetTokens = 4_321
                },
                ProjectId = "configured-project"
            }));

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var root = doc.RootElement;
        Assert.Equal("configured-project", root.GetProperty("project").GetString());
        var request = root.GetProperty("request");
        Assert.Equal("ANY", request
            .GetProperty("toolConfig")
            .GetProperty("functionCallingConfig")
            .GetProperty("mode")
            .GetString());
        Assert.Equal(4_321, request
            .GetProperty("generationConfig")
            .GetProperty("thinkingConfig")
            .GetProperty("thinkingBudget")
            .GetInt32());
    }

    [Fact]
    public async Task Stream_RetriesEmptySseWithoutDuplicateStart()
    {
        var callCount = 0;
        using var handler = new RecordingHandler(_ =>
        {
            callCount++;
            return callCount == 1 ? EmptySseResponse() : SseResponse("after empty");
        });
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);
        var model = BuildModel(providerId: "google-gemini-cli", baseUrl: "https://cloudcode-pa.googleapis.com");

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = ApiKey() }));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(events.OfType<StartEvent>());
        Assert.Single(events.OfType<DoneEvent>());
        Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "after empty" });
    }

    [Fact]
    public async Task Stream_FailsWhenServerRetryDelayExceedsMax()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("Your quota will reset after 39s")
        });
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);
        var model = BuildModel(providerId: "google-gemini-cli", baseUrl: "https://cloudcode-pa.googleapis.com");

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = ApiKey(), MaxRetryDelay = TimeSpan.FromSeconds(1) }));

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Contains("Server requested 40s retry delay", error.Error, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Stream_WhenSignalCancelsRetryDelayTerminatesWithAbortedAssistant()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new RecordingHandler(_ =>
        {
            cts.Cancel();
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("Please retry in 1s", Encoding.UTF8, "text/plain")
            };
        });
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);
        var model = BuildModel("gemini-3.1-pro-high", "google-antigravity", baseUrl: string.Empty);

        var stream = provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = ApiKey(), Signal = cts.Token });
        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal(StreamOptionHelpers.AbortedErrorMessage, error.Error);
        var response = await stream.ResultAsync;
        Assert.Equal(StopReason.Aborted, response.StopReason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ExtractRetryDelay_ParsesHeadersAndBody()
    {
        using var retryAfterSeconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retryAfterSeconds.Headers.TryAddWithoutValidation("Retry-After", "5");
        Assert.Equal(TimeSpan.FromSeconds(6), GoogleGeminiCliProvider.ExtractRetryDelay("Please retry in 1s", retryAfterSeconds));

        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var reset = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        reset.Headers.TryAddWithoutValidation("x-ratelimit-reset", now.AddSeconds(20).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromSeconds(21), GoogleGeminiCliProvider.ExtractRetryDelay(string.Empty, reset, now));

        Assert.Equal(TimeSpan.FromSeconds(39) + TimeSpan.FromSeconds(1), GoogleGeminiCliProvider.ExtractRetryDelay("Your quota will reset after 39s"));
        Assert.Equal(TimeSpan.FromMilliseconds(2500 + 1000), GoogleGeminiCliProvider.ExtractRetryDelay("Please retry in 2500ms"));
        Assert.Equal(TimeSpan.FromMilliseconds(Math.Ceiling(34074.824224 + 1000)), GoogleGeminiCliProvider.ExtractRetryDelay("{\"retryDelay\":\"34.074824224s\"}"));
    }

    private static Model BuildModel(
        string id = "gemini-2.5-flash",
        string providerId = "google-gemini-cli",
        string? baseUrl = "https://cloudcode-pa.googleapis.com",
        bool reasoning = true) => new()
    {
        Id = id,
        Name = id,
        Api = "google-gemini-cli",
        Provider = providerId,
        BaseUrl = baseUrl,
        Reasoning = reasoning
    };

    private static string ApiKey() => """{"token":"token","projectId":"project"}""";

    private static HttpResponseMessage SseResponse(string text) => OpenAiResponsesProviderTests.SseResponse(
        "data: {\"response\":{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"" + text + "\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1,\"totalTokenCount\":2}}}\n\n");

    private static HttpResponseMessage EmptySseResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = header.Value.ToList();
                }
            }

            if (request.Headers.Authorization is AuthenticationHeaderValue authorization)
            {
                headers["Authorization"] = [authorization.ToString()];
            }

            Requests.Add(new RecordedRequest(request.RequestUri!, body, headers));
            return _responseFactory(request);
        }
    }

    private sealed record RecordedRequest(Uri Uri, string Body, IReadOnlyDictionary<string, List<string>> Headers);
}
