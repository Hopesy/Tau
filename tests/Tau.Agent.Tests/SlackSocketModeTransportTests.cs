using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class SlackSocketModeTransportTests
{
    [Fact]
    public async Task ReadMessagesAsync_OpensSocketAcksEnvelopeAndYieldsMappedMessage()
    {
        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/auth.test" => JsonResponse("""
                { "ok": true, "user_id": "UBOT" }
                """),
                "/api/apps.connections.open" => JsonResponse("""
                { "ok": true, "url": "wss://slack.example/socket/1" }
                """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new HttpClient(handler);
        var connection = new FakeSocketModeConnection([
            """
            {
              "envelope_id": "env-1",
              "payload": {
                "event": {
                  "type": "app_mention",
                  "channel": "C123OPS",
                  "user": "U123",
                  "ts": "1778351400.123456",
                  "text": "<@UBOT> inspect deploy"
                }
              }
            }
            """
        ]);
        var connector = new FakeConnector(connection);
        var options = new MomOptions
        {
            SlackAppToken = "xapp-test-token",
            SlackBotToken = "xoxb-test-token",
            SlackApiBaseUrl = "https://slack.test/api/",
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4"
        };
        var responder = new SlackWebApiResponder(options, new HttpClient(new RecordingHandler(_ => JsonResponse("""{ "ok": true }"""))));
        var transport = new SlackSocketModeTransport(options, responder, NullLogger<SlackSocketModeTransport>.Instance, client, connector);

        MomChannelMessage? mapped = null;
        await foreach (var message in transport.ReadMessagesAsync())
        {
            mapped = message;
            break;
        }

        Assert.NotNull(mapped);
        Assert.Equal("C123OPS", mapped.ChannelId);
        Assert.Equal("inspect deploy", mapped.Text);
        Assert.Equal("openai", mapped.Provider);
        Assert.Equal("gpt-5.4", mapped.Model);

        Assert.Equal(new Uri("wss://slack.example/socket/1"), connector.SocketUrl);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("xoxb-test-token", handler.Requests[0].AuthorizationParameter);
        Assert.Equal("xapp-test-token", handler.Requests[1].AuthorizationParameter);
        var ack = Assert.Single(connection.SentMessages);
        Assert.Contains("\"envelope_id\":\"env-1\"", ack, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMessagesAsync_WhenAppTokenMissing_ThrowsBeforeHttpRequest()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{ "ok": true }"""));
        using var client = new HttpClient(handler);
        var options = new MomOptions
        {
            SlackBotToken = "xoxb-test-token",
            SlackApiBaseUrl = "https://slack.test/api/"
        };
        var responder = new SlackWebApiResponder(options, client);
        var transport = new SlackSocketModeTransport(
            options,
            responder,
            NullLogger<SlackSocketModeTransport>.Instance,
            client,
            new FakeConnector(new FakeSocketModeConnection([])));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in transport.ReadMessagesAsync())
            {
            }
        });

        Assert.Equal("Mom Slack app token is not configured.", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadMessagesAsync_WhenSlackOpenFails_ThrowsWithoutTokenInMessage()
    {
        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/auth.test" => JsonResponse("""{ "ok": true, "user_id": "UBOT" }"""),
                "/api/apps.connections.open" => JsonResponse("""{ "ok": false, "error": "invalid_auth" }"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new HttpClient(handler);
        var options = new MomOptions
        {
            SlackAppToken = "xapp-secret-token",
            SlackBotToken = "xoxb-secret-token",
            SlackApiBaseUrl = "https://slack.test/api/"
        };
        var responder = new SlackWebApiResponder(options, client);
        var transport = new SlackSocketModeTransport(
            options,
            responder,
            NullLogger<SlackSocketModeTransport>.Instance,
            client,
            new FakeConnector(new FakeSocketModeConnection([])));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in transport.ReadMessagesAsync())
            {
            }
        });

        Assert.Contains("invalid_auth", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-secret-token", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-secret-token", ex.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                body));
            return _responseFactory(request);
        }
    }

    private sealed class FakeConnector : ISlackSocketModeConnector
    {
        private readonly ISlackSocketModeConnection _connection;

        public FakeConnector(ISlackSocketModeConnection connection)
        {
            _connection = connection;
        }

        public Uri? SocketUrl { get; private set; }

        public Task<ISlackSocketModeConnection> ConnectAsync(Uri socketUrl, CancellationToken cancellationToken = default)
        {
            SocketUrl = socketUrl;
            return Task.FromResult(_connection);
        }
    }

    private sealed class FakeSocketModeConnection : ISlackSocketModeConnection
    {
        private readonly IReadOnlyList<string> _messages;

        public FakeSocketModeConnection(IReadOnlyList<string> messages)
        {
            _messages = messages;
        }

        public List<string> SentMessages { get; } = [];

        public async IAsyncEnumerable<string> ReadTextMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var message in _messages)
            {
                await Task.Yield();
                yield return message;
            }
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(text);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);
}
