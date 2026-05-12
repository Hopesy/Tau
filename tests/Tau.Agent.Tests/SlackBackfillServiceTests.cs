using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class SlackBackfillServiceTests
{
    [Fact]
    public async Task BackfillChannelAsync_AppendsRelevantHistoryInChronologicalOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-slack-backfill-{Guid.NewGuid():N}");
        var channelDir = Path.Combine(root, "C123OPS");
        Directory.CreateDirectory(channelDir);
        await File.WriteAllTextAsync(
            Path.Combine(channelDir, "log.jsonl"),
            """
            {"date":"2026-01-01T00:00:00.0000000Z","ts":"100.100000","user":"U0","text":"already logged","attachments":[],"isBot":false}

            """);

        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/auth.test" => JsonResponse("""{ "ok": true, "user_id": "UBOT" }"""),
                "/api/conversations.history" => JsonResponse("""
                {
                  "ok": true,
                  "messages": [
                    { "type": "message", "user": "U1", "ts": "102.000000", "text": "<@UBOT> newest" },
                    { "type": "message", "user": "U1", "ts": "101.500000", "subtype": "message_changed", "text": "skip edit" },
                    { "type": "message", "bot_id": "B1", "user": "U2", "ts": "101.400000", "text": "skip bot" },
                    { "type": "message", "user": "UBOT", "ts": "101.300000", "text": "bot reply" },
                    { "type": "message", "user": "U1", "ts": "101.200000", "subtype": "file_share", "files": [
                      { "name": "report final.txt", "url_private_download": "https://files.slack.test/report" }
                    ] },
                    { "type": "message", "user": "U1", "ts": "100.100000", "text": "duplicate" }
                  ]
                }
                """),
                "/report" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("report-body", Encoding.UTF8, "text/plain")
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new HttpClient(handler);
        var options = CreateOptions(root);
        var service = CreateService(options, client);

        try
        {
            var count = await service.BackfillChannelAsync("C123OPS");

            Assert.Equal(3, count);
            var historyRequest = Assert.Single(
                handler.Requests,
                static request => request.RequestUri!.AbsolutePath == "/api/conversations.history");
            Assert.Contains("channel=C123OPS", historyRequest.Body, StringComparison.Ordinal);
            Assert.Contains("oldest=100.100000", historyRequest.Body, StringComparison.Ordinal);
            Assert.Contains("inclusive=false", historyRequest.Body, StringComparison.Ordinal);
            Assert.Contains("limit=1000", historyRequest.Body, StringComparison.Ordinal);
            Assert.Equal("xoxb-test-token", historyRequest.AuthorizationParameter);

            var downloadRequest = Assert.Single(
                handler.Requests,
                static request => request.RequestUri!.Host == "files.slack.test");
            Assert.Equal("xoxb-test-token", downloadRequest.AuthorizationParameter);

            var lines = await File.ReadAllLinesAsync(Path.Combine(channelDir, "log.jsonl"));
            Assert.Equal(4, lines.Length);
            Assert.Contains("\"ts\":\"100.100000\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"ts\":\"101.200000\"", lines[1], StringComparison.Ordinal);
            Assert.Contains("\"attachments\":[{\"local\":\"attachments/101200_report_final.txt\",\"original\":\"report final.txt\"}]", lines[1], StringComparison.Ordinal);
            Assert.Contains("\"ts\":\"101.300000\"", lines[2], StringComparison.Ordinal);
            Assert.Contains("\"user\":\"bot\"", lines[2], StringComparison.Ordinal);
            Assert.Contains("\"isBot\":true", lines[2], StringComparison.Ordinal);
            Assert.Contains("\"ts\":\"102.000000\"", lines[3], StringComparison.Ordinal);
            Assert.Contains("\"text\":\"newest\"", lines[3], StringComparison.Ordinal);
            Assert.Equal("report-body", await File.ReadAllTextAsync(Path.Combine(channelDir, "attachments", "101200_report_final.txt")));
        }
        finally
        {
            DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task BackfillExistingChannelsAsync_OnlyBackfillsDirectoriesWithExistingLog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-slack-backfill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "C123OPS"));
        Directory.CreateDirectory(Path.Combine(root, "CNOLOG"));
        await File.WriteAllTextAsync(Path.Combine(root, "C123OPS", "log.jsonl"), string.Empty);
        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/auth.test" => JsonResponse("""{ "ok": true, "user_id": "UBOT" }"""),
                "/api/conversations.history" => JsonResponse("""
                {
                  "ok": true,
                  "messages": [
                    { "type": "message", "user": "U1", "ts": "101.000000", "text": "hello" }
                  ]
                }
                """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new HttpClient(handler);
        var service = CreateService(CreateOptions(root), client);

        try
        {
            var count = await service.BackfillExistingChannelsAsync();

            Assert.Equal(1, count);
            var historyRequest = Assert.Single(
                handler.Requests,
                static request => request.RequestUri!.AbsolutePath == "/api/conversations.history");
            Assert.Contains("channel=C123OPS", historyRequest.Body, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "CNOLOG", "log.jsonl")));
        }
        finally
        {
            DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task BackfillChannelAsync_FollowsCursorUpToConfiguredPageLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-slack-backfill-{Guid.NewGuid():N}");
        var channelDir = Path.Combine(root, "C123OPS");
        Directory.CreateDirectory(channelDir);
        await File.WriteAllTextAsync(Path.Combine(channelDir, "log.jsonl"), string.Empty);
        var historyCalls = 0;
        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/auth.test" => JsonResponse("""{ "ok": true, "user_id": "UBOT" }"""),
                "/api/conversations.history" => JsonResponse(++historyCalls == 1
                    ? """
                      {
                        "ok": true,
                        "messages": [
                          { "type": "message", "user": "U1", "ts": "102.000000", "text": "second" }
                        ],
                        "response_metadata": { "next_cursor": "cursor-2" }
                      }
                      """
                    : """
                      {
                        "ok": true,
                        "messages": [
                          { "type": "message", "user": "U1", "ts": "101.000000", "text": "first" }
                        ],
                        "response_metadata": { "next_cursor": "cursor-3" }
                      }
                      """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new HttpClient(handler);
        var options = CreateOptions(root);
        options.SlackBackfillMaxPages = 2;
        options.SlackBackfillPageSize = 2;
        var service = CreateService(options, client);

        try
        {
            var count = await service.BackfillChannelAsync("C123OPS", botUserId: "UBOT");

            Assert.Equal(2, count);
            var requests = handler.Requests.Where(static request => request.RequestUri!.AbsolutePath == "/api/conversations.history").ToArray();
            Assert.Equal(2, requests.Length);
            Assert.DoesNotContain("cursor=", requests[0].Body, StringComparison.Ordinal);
            Assert.Contains("cursor=cursor-2", requests[1].Body, StringComparison.Ordinal);
            Assert.Contains("limit=2", requests[0].Body, StringComparison.Ordinal);

            var lines = await File.ReadAllLinesAsync(Path.Combine(channelDir, "log.jsonl"));
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"ts\":\"101.000000\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"ts\":\"102.000000\"", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task BackfillChannelAsync_WhenLogMissing_SkipsHttpHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-slack-backfill-{Guid.NewGuid():N}");
        var handler = new RecordingHandler(_ => JsonResponse("""{ "ok": true, "user_id": "UBOT" }"""));
        using var client = new HttpClient(handler);
        var service = CreateService(CreateOptions(root), client);

        try
        {
            var count = await service.BackfillChannelAsync("C123OPS", botUserId: "UBOT");

            Assert.Equal(0, count);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            DeleteIfExists(root);
        }
    }

    private static MomOptions CreateOptions(string root)
    {
        return new MomOptions
        {
            DefaultWorkingDirectory = root,
            SlackApiBaseUrl = "https://slack.test/api/",
            SlackBotToken = "xoxb-test-token",
            SlackBackfillEnabled = true,
            SlackBackfillMaxPages = 3,
            SlackBackfillPageSize = 1000
        };
    }

    private static SlackBackfillService CreateService(MomOptions options, HttpClient client)
    {
        return new SlackBackfillService(
            options,
            new SlackAttachmentDownloader(options, new SilentLogger<SlackAttachmentDownloader>(), client),
            new SilentLogger<SlackBackfillService>(),
            client);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
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
