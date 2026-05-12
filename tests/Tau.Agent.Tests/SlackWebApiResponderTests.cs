using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class SlackWebApiResponderTests
{
    [Fact]
    public async Task RespondAsync_PostsSlackMessageWithBearerToken()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
        { "ok": true, "ts": "1778351400.777777" }
        """));
        using var client = new HttpClient(handler);
        var responder = new SlackWebApiResponder(new MomOptions
        {
            SlackBotToken = "xoxb-test-token",
            SlackApiBaseUrl = "https://slack.test/api"
        }, client);

        var ts = await responder.RespondAsync(
            new MomChannelMessage("C123OPS", "hello", "1778351400.123456", "U123"),
            "done");

        Assert.Equal("1778351400.777777", ts);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://slack.test/api/chat.postMessage", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("xoxb-test-token", request.AuthorizationParameter);
        Assert.Contains("\"channel\":\"C123OPS\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"done\"", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("thread_ts", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RespondInThreadAsync_PostsThreadMessage()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
        { "ok": true, "ts": "1778351400.888888" }
        """));
        using var client = new HttpClient(handler);
        var responder = new SlackWebApiResponder(new MomOptions
        {
            SlackBotToken = "xoxb-test-token",
            SlackApiBaseUrl = "https://slack.test/api/"
        }, client);

        var ts = await responder.RespondInThreadAsync(
            new MomChannelMessage("C123OPS", "hello", "1778351400.123456", "U123", ThreadTs: "1778351000.000001"),
            "thread done");

        Assert.Equal("1778351400.888888", ts);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.test/api/chat.postMessage", request.RequestUri!.ToString());
        Assert.Contains("\"thread_ts\":\"1778351000.000001\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadFileAsync_UsesSlackUploadV2MultipartPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-slack-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "report.txt");
        await File.WriteAllTextAsync(filePath, "report body");

        try
        {
            var handler = new RecordingHandler(_ => JsonResponse("""
            { "ok": true }
            """));
            using var client = new HttpClient(handler);
            var responder = new SlackWebApiResponder(new MomOptions
            {
                SlackBotToken = "xoxb-test-token",
                SlackApiBaseUrl = "https://slack.test/api/"
            }, client);

            await responder.UploadFileAsync(
                new MomChannelMessage("C123OPS", "hello", "1778351400.123456", "U123"),
                filePath,
                "Deployment Report");

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://slack.test/api/files.uploadV2", request.RequestUri!.ToString());
            Assert.Equal("xoxb-test-token", request.AuthorizationParameter);
            Assert.Contains("name=channel_id", request.Body, StringComparison.Ordinal);
            Assert.Contains("C123OPS", request.Body, StringComparison.Ordinal);
            Assert.Contains("name=title", request.Body, StringComparison.Ordinal);
            Assert.Contains("Deployment Report", request.Body, StringComparison.Ordinal);
            Assert.Contains("filename=report.txt", request.Body, StringComparison.Ordinal);
            Assert.Contains("report body", request.Body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RespondAsync_WhenSlackReturnsError_ThrowsWithoutTokenInMessage()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
        { "ok": false, "error": "channel_not_found" }
        """));
        using var client = new HttpClient(handler);
        var responder = new SlackWebApiResponder(new MomOptions
        {
            SlackBotToken = "xoxb-secret-token",
            SlackApiBaseUrl = "https://slack.test/api/"
        }, client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => responder.RespondAsync(
            new MomChannelMessage("C404", "hello", "1778351400.123456", "U123"),
            "done"));

        Assert.Contains("channel_not_found", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-secret-token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RespondAsync_WhenTokenMissing_ThrowsBeforeHttpRequest()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
        { "ok": true }
        """));
        using var client = new HttpClient(handler);
        var responder = new SlackWebApiResponder(new MomOptions
        {
            SlackApiBaseUrl = "https://slack.test/api/"
        }, client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => responder.RespondAsync(
            new MomChannelMessage("C123OPS", "hello", "1778351400.123456", "U123"),
            "done"));

        Assert.Equal("Mom Slack bot token is not configured.", ex.Message);
        Assert.Empty(handler.Requests);
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

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);
}
