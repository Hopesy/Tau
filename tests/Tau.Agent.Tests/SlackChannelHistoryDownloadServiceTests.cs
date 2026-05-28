using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class SlackChannelHistoryDownloadServiceTests
{
    [Fact]
    public async Task DownloadAsync_WritesChronologicalMessagesAndThreadReplies()
    {
        var historyCalls = 0;
        var repliesCalls = 0;
        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/conversations.info" => JsonResponse("""{ "ok": true, "channel": { "name": "ops" } }"""),
                "/api/conversations.history" => JsonResponse(++historyCalls == 1
                    ? """
                      {
                        "ok": true,
                        "messages": [
                          { "type": "message", "user": "U2", "ts": "102.000000", "text": "newer" },
                          { "type": "message", "user": "U1", "ts": "101.000000", "text": "parent\ncontinued", "reply_count": 2 }
                        ],
                        "response_metadata": { "next_cursor": "cursor-2" }
                      }
                      """
                    : """
                      {
                        "ok": true,
                        "messages": [
                          { "type": "message", "user": "U0", "ts": "100.000000", "text": "oldest" }
                        ]
                      }
                      """),
                "/api/conversations.replies" => JsonResponse(++repliesCalls == 1
                    ? """
                      {
                        "ok": true,
                        "messages": [
                          { "type": "message", "user": "U1", "ts": "101.000000", "text": "parent" },
                          { "type": "message", "user": "U3", "ts": "101.100000", "text": "reply one" }
                        ],
                        "response_metadata": { "next_cursor": "thread-cursor-2" }
                      }
                      """
                    : """
                      {
                        "ok": true,
                        "messages": [
                          { "type": "message", "user": "U1", "ts": "101.000000", "text": "parent" },
                          { "type": "message", "user": "U4", "ts": "101.200000", "text": "reply two\nline two" }
                        ]
                      }
                      """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new HttpClient(handler);
        var service = new SlackChannelHistoryDownloadService(
            CreateOptions(),
            new SilentLogger<SlackChannelHistoryDownloadService>(),
            client);
        var output = new StringWriter();
        var progress = new StringWriter();

        var result = await service.DownloadAsync("C123OPS", output, progress);

        Assert.Equal(new SlackChannelHistoryDownloadResult("C123OPS", "ops", 3, 2), result);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                [
                    "[1970-01-01 00:01:40] U0: oldest",
                    "[1970-01-01 00:01:41] U1: parent",
                    "                          continued",
                    "  [1970-01-01 00:01:41] U3: reply one",
                    "  [1970-01-01 00:01:41] U4: reply two",
                    "                            line two",
                    "[1970-01-01 00:01:42] U2: newer",
                    string.Empty
                ]),
            output.ToString());
        Assert.Contains("Downloading history for #ops (C123OPS)", progress.ToString(), StringComparison.Ordinal);
        Assert.Contains("Done! 3 messages, 2 thread replies", progress.ToString(), StringComparison.Ordinal);

        var historyRequests = handler.Requests
            .Where(static request => request.RequestUri!.AbsolutePath == "/api/conversations.history")
            .ToArray();
        Assert.Equal(2, historyRequests.Length);
        Assert.Contains("channel=C123OPS", historyRequests[0].Body, StringComparison.Ordinal);
        Assert.Contains("limit=200", historyRequests[0].Body, StringComparison.Ordinal);
        Assert.Contains("cursor=cursor-2", historyRequests[1].Body, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal("xoxb-test-token", request.AuthorizationParameter));

        var replyRequests = handler.Requests
            .Where(static request => request.RequestUri!.AbsolutePath == "/api/conversations.replies")
            .ToArray();
        Assert.Equal(2, replyRequests.Length);
        Assert.Contains("ts=101.000000", replyRequests[0].Body, StringComparison.Ordinal);
        Assert.Contains("cursor=thread-cursor-2", replyRequests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_WhenInfoFails_UsesChannelIdAsName()
    {
        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/conversations.info" => JsonResponse("""{ "ok": false, "error": "channel_not_found" }"""),
                "/api/conversations.history" => JsonResponse("""
                {
                  "ok": true,
                  "messages": [
                    { "type": "message", "user": "U1", "ts": "100.000000", "text": "hello" }
                  ]
                }
                """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new HttpClient(handler);
        var service = new SlackChannelHistoryDownloadService(
            CreateOptions(),
            new SilentLogger<SlackChannelHistoryDownloadService>(),
            client);

        var result = await service.DownloadAsync("D123DM", new StringWriter(), new StringWriter());

        Assert.Equal("D123DM", result.ChannelName);
        Assert.Equal(1, result.MessageCount);
    }

    [Fact]
    public async Task DownloadAsync_WhenBotTokenMissing_FailsBeforeHttp()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{ "ok": true }"""));
        using var client = new HttpClient(handler);
        var options = CreateOptions();
        options.SlackBotToken = null;
        var service = new SlackChannelHistoryDownloadService(
            options,
            new SilentLogger<SlackChannelHistoryDownloadService>(),
            client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAsync("C123OPS", new StringWriter(), new StringWriter()));

        Assert.Equal("Mom Slack bot token is not configured.", ex.Message);
        Assert.Empty(handler.Requests);
    }

    private static MomOptions CreateOptions() =>
        new()
        {
            SlackApiBaseUrl = "https://slack.test/api/",
            SlackBotToken = "xoxb-test-token"
        };

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

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);
}
