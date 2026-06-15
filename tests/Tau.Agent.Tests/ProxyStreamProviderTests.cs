using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tau.Agent.Proxy;
using Tau.Ai;
using Tau.Ai.Streaming;

namespace Tau.Agent.Tests;

public sealed class ProxyStreamProviderTests
{
    [Fact]
    public async Task StreamSimple_RoundTripsThroughLoopbackProxyServer()
    {
        await using var server = await LoopbackProxyServer.StartAsync(
            BuildSse(
                """{"type":"start"}""",
                """{"type":"text_start","contentIndex":0}""",
                """{"type":"text_delta","contentIndex":0,"delta":"loopback "}""",
                """{"type":"text_delta","contentIndex":0,"delta":"complete"}""",
                """{"type":"text_end","contentIndex":0}""",
                """{"type":"done","reason":"stop","usage":{"input":7,"output":3}}"""));

        var provider = new ProxyStreamProvider(api: "proxy-stream");
        var model = new Model
        {
            Id = "loopback-model",
            Name = "Loopback Model",
            Api = provider.Api,
            Provider = "loopback-provider"
        };

        var stream = provider.StreamSimple(
            model,
            new LlmContext("loopback system", [new UserMessage("hello proxy server")], null),
            new ProxyStreamOptions
            {
                ProxyUrl = server.BaseUrl,
                AuthToken = "loopback-token",
                Temperature = 0.1f
            });

        var events = await CollectAsync(stream);
        var request = await server.Request.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Collection(
            events,
            evt => Assert.IsType<StartEvent>(evt),
            evt => Assert.IsType<TextStartEvent>(evt),
            evt => Assert.IsType<TextDeltaEvent>(evt),
            evt => Assert.IsType<TextDeltaEvent>(evt),
            evt => Assert.IsType<TextEndEvent>(evt),
            evt => Assert.IsType<DoneEvent>(evt));

        var done = Assert.IsType<DoneEvent>(events[^1]);
        var text = Assert.Single(done.Message.Content.OfType<TextContent>());
        Assert.Equal("loopback complete", text.Text);
        Assert.Equal(StopReason.EndTurn, done.Message.StopReason);
        Assert.Equal(7, done.Message.Usage?.InputTokens);
        Assert.Equal(3, done.Message.Usage?.OutputTokens);

        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/stream", request.Path);
        Assert.Equal("Bearer loopback-token", request.Headers["Authorization"]);
        Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.OrdinalIgnoreCase);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("loopback-model", body.RootElement.GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("loopback system", body.RootElement.GetProperty("context").GetProperty("systemPrompt").GetString());
        Assert.Equal(0.1, body.RootElement.GetProperty("options").GetProperty("temperature").GetDouble(), 3);
    }

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
                    """{"type":"done","reason":"toolUse","usage":{"input":11,"output":4,"cacheRead":2,"cacheWrite":1,"serviceTier":"flex","cost":{"input":0.11,"output":0.22,"cacheRead":0.03,"cacheWrite":0.04,"total":0.40}}}"""),
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
                {
                    Usage = new Usage(3, 4, 1, 2, "priority", new UsageCost(0.3m, 0.4m, 0.1m, 0.2m)),
                    Provider = "context-provider",
                    Model = "context-model",
                    Api = "context-api"
                }
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
        Assert.NotNull(done.Message.Usage?.Cost);
        Assert.Equal(0.11m, done.Message.Usage!.Value.Cost!.Value.Input);
        Assert.Equal(0.22m, done.Message.Usage.Value.Cost.Value.Output);
        Assert.Equal(0.03m, done.Message.Usage.Value.Cost.Value.CacheRead);
        Assert.Equal(0.04m, done.Message.Usage.Value.Cost.Value.CacheWrite);
        Assert.Equal(0.40m, done.Message.Usage.Value.Cost.Value.Total);

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
        Assert.Equal("context-provider", messages[1].GetProperty("provider").GetString());
        Assert.Equal("context-model", messages[1].GetProperty("model").GetString());
        var requestUsage = messages[1].GetProperty("usage");
        Assert.Equal("priority", requestUsage.GetProperty("serviceTier").GetString());
        Assert.Equal(1.0m, requestUsage.GetProperty("cost").GetProperty("total").GetDecimal());
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

    [Fact]
    public async Task StreamSimple_WhenProxyEndsWithoutTerminalEventYieldsErrorEvent()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                BuildSse(
                    """{"type":"start"}""",
                    """{"type":"text_start","contentIndex":0}""",
                    """{"type":"text_delta","contentIndex":0,"delta":"partial"}"""),
                Encoding.UTF8,
                "text/event-stream")
        });

        var stream = CreateProxyProvider(handler).StreamSimple(
            CreateProxyModel(),
            new LlmContext(null, [new UserMessage("hello")], null),
            new ProxyStreamOptions { ApiKey = "proxy-token" });

        var events = await CollectAsync(stream);
        var error = Assert.IsType<ErrorEvent>(events[^1]);
        Assert.Equal("Proxy stream ended without a terminal event.", error.Error);
        Assert.Equal("partial", Assert.Single(error.Partial?.Content.OfType<TextContent>() ?? []).Text);

        var result = await stream.ResultAsync;
        Assert.Equal("Proxy stream ended without a terminal event.", result.ErrorMessage);
        Assert.Equal(StopReason.Error, result.StopReason);
    }

    [Fact]
    public async Task StreamSimple_WhenProxySendsMalformedJsonYieldsErrorEvent()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                BuildSse(
                    """{"type":"start"}""",
                    """{"type":"text_start","contentIndex":0}""",
                    """{"type":"text_delta","contentIndex":0,"delta":"safe"}""",
                    """{"type":"text_delta","contentIndex":0,"delta":"""),
                Encoding.UTF8,
                "text/event-stream")
        });

        var stream = CreateProxyProvider(handler).StreamSimple(
            CreateProxyModel(),
            new LlmContext(null, [new UserMessage("hello")], null),
            new ProxyStreamOptions { ApiKey = "proxy-token" });

        var events = await CollectAsync(stream);
        var error = Assert.IsType<ErrorEvent>(events[^1]);

        Assert.Contains("Expected depth", error.Error, StringComparison.OrdinalIgnoreCase);
        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Contains("Expected depth", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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

    private static ProxyStreamProvider CreateProxyProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler), api: "proxy-stream");

    private static Model CreateProxyModel() => new()
    {
        Id = "test-model",
        Name = "Test Model",
        Api = "proxy-stream",
        Provider = "test-provider",
        BaseUrl = "https://proxy.example"
    };

    private sealed class LoopbackProxyServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<LoopbackRequest> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _acceptTask;
        private readonly string _responseBody;

        private LoopbackProxyServer(TcpListener listener, string responseBody)
        {
            _listener = listener;
            _responseBody = responseBody;
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
            _acceptTask = Task.Run(AcceptOneAsync);
        }

        public string BaseUrl { get; }

        public Task<LoopbackRequest> Request => _request.Task;

        public static Task<LoopbackProxyServer> StartAsync(string responseBody)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LoopbackProxyServer(listener, responseBody));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();

            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptOneAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                await HandleClientAsync(client, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                _request.TrySetCanceled();
            }
            catch (Exception ex)
            {
                _request.TrySetException(ex);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            var requestLine = await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("Loopback proxy server received an empty request.");
            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new InvalidOperationException($"Loopback proxy server received an invalid request line: {requestLine}");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    break;
                }

                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                headers[line[..separator]] = line[(separator + 1)..].Trim();
            }

            var contentLength = headers.TryGetValue("Content-Length", out var value) &&
                int.TryParse(value, out var parsedLength)
                    ? parsedLength
                    : 0;
            var bodyBytes = await ReadExactlyAsync(stream, contentLength, cancellationToken).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(bodyBytes);
            _request.TrySetResult(new LoopbackRequest(parts[0], parts[1], headers, body));

            var responseBytes = Encoding.UTF8.GetBytes(_responseBody);
            var responseHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                $"Content-Length: {responseBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n");
            await stream.WriteAsync(responseHeaders, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string?> ReadAsciiLineAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];

            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                }

                if (buffer[0] == (byte)'\n')
                {
                    return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                }

                bytes.Add(buffer[0]);
            }
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
        {
            var bytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidOperationException("Loopback proxy server received a truncated request body.");
                }

                offset += read;
            }

            return bytes;
        }
    }

    private sealed record LoopbackRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

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
