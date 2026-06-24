using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tau.Ai.Registry;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class OpenAiCodexResponsesProviderTests
{
    [Fact]
    public async Task Stream_AddsCodexHeadersFromJwt()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            capturedRequest = request;
            return OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"type":"response.done","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

                """);
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_test") }));

        Assert.Equal("/backend-api/codex/responses", handler.RequestUri!.AbsolutePath);
        Assert.NotNull(capturedRequest);
        Assert.Equal("acc_test", capturedRequest!.Headers.GetValues("chatgpt-account-id").Single());
        Assert.Equal("tau", capturedRequest.Headers.GetValues("originator").Single());
        Assert.Equal("responses=experimental", capturedRequest.Headers.GetValues("OpenAI-Beta").Single());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Contains(events, evt => evt is DoneEvent);
    }

    [Fact]
    public async Task Stream_DefaultMaxRetries_DoesNotRetryRetryableCodexErrors()
    {
        var calls = 0;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("rate limit", Encoding.UTF8, "text/plain")
                }
                : OpenAiResponsesProviderTests.SseResponse(
                    """
                    data: {"type":"response.done","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

                    """);
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_no_retry") }));

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal("openai-codex-responses error 429: rate limit", error.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Stream_RetriesRetryableCodexErrors()
    {
        var calls = 0;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("rate limit", Encoding.UTF8, "text/plain")
                }
                : OpenAiResponsesProviderTests.SseResponse(
                    """
                    data: {"type":"response.done","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

                    """);
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions
            {
                ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_retry"),
                MaxRetryDelay = TimeSpan.Zero,
                MaxRetries = 1
            }));

        Assert.Equal(2, calls);
        Assert.Contains(events, evt => evt is DoneEvent);
    }

    [Fact]
    public async Task Stream_SseHeaderTimeout_EmitsTimeoutError()
    {
        using var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client, null, TimeSpan.FromMilliseconds(25));

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_timeout") }))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal("Codex SSE response headers timed out after 25ms", error.Error);
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.CancellationObserved);
    }

    [Fact]
    public async Task Stream_MapsIncompleteToMaxTokens()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            data: {"type":"response.incomplete","response":{"id":"resp_1","status":"incomplete","usage":{"input_tokens":1,"output_tokens":2}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_incomplete") }));

        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal(StopReason.MaxTokens, done.Message.StopReason);
    }

    [Fact]
    public async Task Stream_ResolvesCodexDefaultServiceTierToRequestedTierForCost()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            data: {"type":"response.done","response":{"id":"resp_1","status":"completed","service_tier":"default","usage":{"input_tokens":500000,"output_tokens":250000}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client);
        var model = BuildCodexModel() with
        {
            Cost = new ModelCost(2m, 8m)
        };

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            model,
            new LlmContext { Messages = [new UserMessage("hi")] },
            new OpenAiCodexResponsesOptions
            {
                ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_tier"),
                ServiceTier = "flex"
            }));

        using var body = JsonDocument.Parse(handler.CapturedBody);
        Assert.Equal("flex", body.RootElement.GetProperty("service_tier").GetString());
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("flex", done.Message.Usage!.Value.ServiceTier);
        Assert.Equal(1.50m, ModelCatalog.CalculateCost(model, done.Message.Usage.Value).Total);
    }

    [Fact]
    public async Task Stream_AddsReasoningServiceTierAndTextVerbosityOptions()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
            """
            data: {"type":"response.done","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

            """));
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client);

        _ = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new OpenAiCodexResponsesOptions
            {
                ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_options"),
                ReasoningEffort = "minimal",
                ReasoningSummary = "detailed",
                ServiceTier = "priority",
                TextVerbosity = "high"
            }));

        using var body = JsonDocument.Parse(handler.CapturedBody);
        var root = body.RootElement;
        var reasoning = root.GetProperty("reasoning");
        Assert.Equal("low", reasoning.GetProperty("effort").GetString());
        Assert.Equal("detailed", reasoning.GetProperty("summary").GetString());
        Assert.Equal("priority", root.GetProperty("service_tier").GetString());
        Assert.Equal("high", root.GetProperty("text").GetProperty("verbosity").GetString());
    }

    [Fact]
    public async Task Stream_WebSocketTransport_SendsResponseCreateFrameAndParsesEvents()
    {
        var connection = new FakeCodexWebSocketConnection(_ =>
        [
            """{"type":"response.output_item.added","item":{"id":"msg_1","type":"message"}}""",
            """{"type":"response.output_text.delta","item_id":"msg_1","delta":"ws"}""",
            """{"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"output_text","text":"ws","annotations":[]}]}}""",
            """{"type":"response.done","response":{"id":"resp_ws","status":"completed","usage":{"input_tokens":2,"output_tokens":3}}}"""
        ]);
        var webSocket = new FakeCodexWebSocketTransport(connection);
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("SSE fallback should not be used"));
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client, webSocket);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext("system", [new UserMessage("hi")], null),
            new OpenAiCodexResponsesOptions
            {
                ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_ws"),
                Transport = StreamTransport.WebSocket,
                SessionId = "session-ws"
            }));

        var connect = Assert.Single(webSocket.Connects);
        Assert.Equal("wss", connect.Url.Scheme);
        Assert.Equal("/backend-api/codex/responses", connect.Url.AbsolutePath);
        Assert.Equal("Bearer " + OpenAiResponsesSharedTests.BuildFakeJwt("acc_ws"), connect.Headers["Authorization"]);
        Assert.Equal("acc_ws", connect.Headers["chatgpt-account-id"]);
        Assert.Equal("tau", connect.Headers["originator"]);
        Assert.Equal("responses_websockets=2026-02-06", connect.Headers["OpenAI-Beta"]);
        Assert.Equal("session-ws", connect.Headers["x-client-request-id"]);
        Assert.Equal("session-ws", connect.Headers["session_id"]);

        var sent = Assert.Single(connection.SentFrames);
        using var sentJson = JsonDocument.Parse(sent);
        var root = sentJson.RootElement;
        Assert.Equal("response.create", root.GetProperty("type").GetString());
        Assert.Equal("gpt-5.2-codex", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("instructions").GetString());
        Assert.True(root.TryGetProperty("input", out _));
        Assert.False(root.TryGetProperty("messages", out _));
        Assert.Equal("session-ws", root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("medium", root.GetProperty("text").GetProperty("verbosity").GetString());

        Assert.Contains(events, evt => evt is StartEvent);
        Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "ws" });
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("resp_ws", done.Message.ResponseId);
        Assert.Equal(new Usage(2, 3), done.Message.Usage);
    }

    [Fact]
    public async Task Stream_AutoTransport_FallsBackToSseWhenWebSocketFailsBeforeStart()
    {
        var webSocket = new FakeCodexWebSocketTransport(new InvalidOperationException("websocket unavailable"));
        var httpCalls = 0;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
        {
            httpCalls++;
            return OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"type":"response.done","response":{"id":"resp_sse","status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

                """);
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client, webSocket);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new OpenAiCodexResponsesOptions
            {
                ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_auto"),
                Transport = StreamTransport.Auto
            }));

        Assert.Equal(1, httpCalls);
        Assert.Single(webSocket.Connects);
        var done = Assert.Single(events.OfType<DoneEvent>());
        var diagnostic = Assert.Single(done.Message.Diagnostics!);
        Assert.Equal("provider_transport_failure", diagnostic.Type);
        Assert.Equal("InvalidOperationException", diagnostic.Error!.Name);
        Assert.Equal("websocket unavailable", diagnostic.Error.Message);
        Assert.Equal("auto", diagnostic.Details!["configuredTransport"]);
        Assert.Equal("sse", diagnostic.Details["fallbackTransport"]);
        Assert.Equal(false, diagnostic.Details["eventsEmitted"]);
        Assert.Equal("before_message_stream_start", diagnostic.Details["phase"]);
        Assert.IsType<int>(diagnostic.Details["requestBytes"]);
    }

    [Fact]
    public async Task Stream_WebSocketTransport_ReusesIdleSessionSocket()
    {
        var connection = new FakeCodexWebSocketConnection(sendIndex =>
        [
            "{\"type\":\"response.done\",\"response\":{\"id\":\"resp_" + sendIndex + "\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}"
        ]);
        var webSocket = new FakeCodexWebSocketTransport(connection);
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("SSE fallback should not be used"));
        using var client = new HttpClient(handler);
        var provider = new OpenAiCodexResponsesProvider(client, webSocket);
        var options = new OpenAiCodexResponsesOptions
        {
            ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_reuse"),
            Transport = StreamTransport.WebSocket,
            SessionId = "session-reuse"
        };

        _ = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("first")] },
            options));
        var secondEvents = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("second")] },
            options));

        Assert.Single(webSocket.Connects);
        Assert.Equal(2, connection.SentFrames.Count);
        Assert.False(connection.CloseCalled);
        var done = Assert.Single(secondEvents.OfType<DoneEvent>());
        Assert.Equal("resp_2", done.Message.ResponseId);
    }

    [Fact]
    public async Task Stream_WebSocketTransport_CleanupSessionResourcesClosesCachedSocket()
    {
        var connections = new List<FakeCodexWebSocketConnection>();
        var webSocket = new FakeCodexWebSocketTransport(connectIndex =>
        {
            var connection = new FakeCodexWebSocketConnection(sendIndex =>
            [
                "{\"type\":\"response.done\",\"response\":{\"id\":\"resp_" + connectIndex + "_" + sendIndex + "\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}"
            ]);
            connections.Add(connection);
            return connection;
        });
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("SSE fallback should not be used"));
        using var client = new HttpClient(handler);
        using var provider = new OpenAiCodexResponsesProvider(client, webSocket);
        var options = new OpenAiCodexResponsesOptions
        {
            ApiKey = OpenAiResponsesSharedTests.BuildFakeJwt("acc_cleanup"),
            Transport = StreamTransport.WebSocket,
            SessionId = "session-cleanup"
        };

        _ = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("first")] },
            options));
        SessionResources.CleanupSessionResources("session-cleanup");
        var secondEvents = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildCodexModel(),
            new LlmContext { Messages = [new UserMessage("second")] },
            options));

        Assert.Equal(2, webSocket.Connects.Count);
        Assert.Equal(2, connections.Count);
        Assert.True(connections[0].CloseCalled);
        Assert.False(connections[1].CloseCalled);
        var done = Assert.Single(secondEvents.OfType<DoneEvent>());
        Assert.Equal("resp_2_1", done.Message.ResponseId);
    }

    [Fact]
    public void SessionResources_RegisterCleanup_ReturnsUnregisterHandle()
    {
        var calls = new List<string?>();
        var registration = SessionResources.RegisterSessionResourceCleanup(calls.Add);

        SessionResources.CleanupSessionResources("resource-unregister");
        registration.Dispose();
        SessionResources.CleanupSessionResources("resource-unregister-again");

        Assert.Equal(["resource-unregister"], calls);
    }

    [Fact]
    public void SessionResources_CleanupSessionResources_AggregatesErrorsAndContinues()
    {
        var calls = new List<string>();
        var first = SessionResources.RegisterSessionResourceCleanup(sessionId =>
        {
            calls.Add($"first:{sessionId}");
            throw new InvalidOperationException("first failed");
        });
        var second = SessionResources.RegisterSessionResourceCleanup(sessionId =>
        {
            calls.Add($"second:{sessionId}");
            throw new ApplicationException("second failed");
        });
        var third = SessionResources.RegisterSessionResourceCleanup(sessionId =>
        {
            calls.Add($"third:{sessionId}");
        });

        try
        {
            var error = Assert.Throws<AggregateException>(() =>
                SessionResources.CleanupSessionResources("resource-aggregate"));

            Assert.StartsWith("Failed to cleanup session resources.", error.Message, StringComparison.Ordinal);
            Assert.Collection(error.InnerExceptions,
                firstError => Assert.IsType<InvalidOperationException>(firstError),
                secondError => Assert.IsType<ApplicationException>(secondError));
            Assert.Equal(
                ["first:resource-aggregate", "second:resource-aggregate", "third:resource-aggregate"],
                calls);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            third.Dispose();
        }
    }

    private static Model BuildCodexModel() => new()
    {
        Id = "gpt-5.2-codex",
        Name = "GPT-5.2 Codex",
        Api = "openai-codex-responses",
        Provider = "openai-codex",
        BaseUrl = "https://chatgpt.com/backend-api",
        Reasoning = true
    };

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("Blocking handler should only exit through cancellation.");
        }
    }

    private sealed class FakeCodexWebSocketTransport : ICodexWebSocketTransport
    {
        private readonly FakeCodexWebSocketConnection? _connection;
        private readonly Func<int, FakeCodexWebSocketConnection>? _connectionFactory;
        private readonly Exception? _connectError;

        public FakeCodexWebSocketTransport(FakeCodexWebSocketConnection connection)
        {
            _connection = connection;
        }

        public FakeCodexWebSocketTransport(Func<int, FakeCodexWebSocketConnection> connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public FakeCodexWebSocketTransport(Exception connectError)
        {
            _connectError = connectError;
        }

        public List<ConnectCall> Connects { get; } = [];

        public Task<ICodexWebSocketConnection> ConnectAsync(
            Uri url,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken = default)
        {
            Connects.Add(new ConnectCall(url, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)));
            if (_connectError is not null)
            {
                throw _connectError;
            }

            return Task.FromResult<ICodexWebSocketConnection>(
                _connectionFactory?.Invoke(Connects.Count) ?? _connection!);
        }
    }

    private sealed record ConnectCall(Uri Url, Dictionary<string, string> Headers);

    private sealed class FakeCodexWebSocketConnection : ICodexWebSocketConnection
    {
        private readonly Func<int, IReadOnlyList<string>> _messagesForSend;
        private readonly Queue<string> _pendingMessages = new();
        private int _sendCount;

        public FakeCodexWebSocketConnection(Func<int, IReadOnlyList<string>> messagesForSend)
        {
            _messagesForSend = messagesForSend;
        }

        public WebSocketState State { get; private set; } = WebSocketState.Open;
        public List<string> SentFrames { get; } = [];
        public bool CloseCalled { get; private set; }

        public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            SentFrames.Add(text);
            _sendCount++;
            foreach (var message in _messagesForSend(_sendCount))
            {
                _pendingMessages.Enqueue(message);
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReadTextMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (_pendingMessages.Count > 0)
            {
                yield return _pendingMessages.Dequeue();
                await Task.Yield();
            }
        }

        public ValueTask CloseAsync(int statusCode, string reason, CancellationToken cancellationToken = default)
        {
            CloseCalled = true;
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
