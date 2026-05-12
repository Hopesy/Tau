using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class SlackAttachmentDownloaderTests
{
    [Fact]
    public async Task DownloadAttachmentsAsync_DownloadsSlackFileWithBearerToken()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-slack-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("image-body", Encoding.UTF8, "application/octet-stream")
        });
        using var client = new HttpClient(handler);
        var downloader = new SlackAttachmentDownloader(
            new MomOptions { SlackBotToken = "xoxb-test-token" },
            new SilentLogger<SlackAttachmentDownloader>(),
            client);

        try
        {
            var message = await downloader.DownloadAttachmentsAsync(
                new MomChannelMessage(
                    "C123OPS",
                    "see file",
                    "1778351400.123456",
                    "U123",
                    [new MomChannelAttachment("screen shot.png", Url: "https://slack.example/files/1")]),
                root);

            var attachment = Assert.Single(message.Attachments!);
            Assert.Equal("screen shot.png", attachment.Original);
            Assert.Equal("attachments/1778351400123_screen_shot.png", attachment.Local);
            Assert.Equal("https://slack.example/files/1", attachment.Url);
            Assert.Equal("image-body", await File.ReadAllTextAsync(Path.Combine(root, "attachments", "1778351400123_screen_shot.png")));
            var request = Assert.Single(handler.Requests);
            Assert.Equal("Bearer", request.AuthorizationScheme);
            Assert.Equal("xoxb-test-token", request.AuthorizationParameter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_WithSlackUrlAttachment_DownloadsBeforeDelegating()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-channel-download-{Guid.NewGuid():N}");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("report-body", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);

        try
        {
            var options = new MomOptions
            {
                DefaultWorkingDirectory = root,
                DefaultProvider = "openai",
                DefaultModel = "gpt-5.4",
                SlackBotToken = "xoxb-test-token",
                RunningStatusStaleAfterMinutes = 60
            };
            var runner = new FakeDelegationAgentRunner();
            var processor = new MomChannelMessageProcessor(
                options,
                runner,
                new ChannelStatusStore(new SilentLogger<ChannelStatusStore>()),
                new SilentLogger<MomChannelMessageProcessor>(),
                new SlackAttachmentDownloader(options, new SilentLogger<SlackAttachmentDownloader>(), client));

            var processed = await processor.ProcessAsync(
                new MomChannelMessage(
                    "C123OPS",
                    "inspect attached report",
                    "1778351400.123456",
                    "U123",
                    [new MomChannelAttachment("report final.txt", Url: "https://slack.example/files/report")]),
                new RecordingResponder());

            Assert.True(processed);
            var request = Assert.Single(runner.Requests);
            Assert.Equal(["attachments/1778351400123_report_final.txt"], request.Attachments);
            Assert.Equal("report-body", await File.ReadAllTextAsync(Path.Combine(root, "C123OPS", "attachments", "1778351400123_report_final.txt")));
            Assert.Contains("\"original\":\"report final.txt\"", await File.ReadAllTextAsync(Path.Combine(root, "C123OPS", "attachments", "attachments.jsonl")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class FakeDelegationAgentRunner : IDelegationAgentRunner
    {
        public List<DelegationRequest> Requests { get; } = [];

        public Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DelegationExecution(
                "stub-response",
                [],
                Error: null,
                request.Provider ?? "unknown",
                request.Model ?? "unknown",
                request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                request.Metadata,
                StopReason: "end_turn"));
        }
    }

    private sealed class RecordingResponder : IMomChannelResponder
    {
        public Task<string?> RespondAsync(MomChannelMessage message, string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("response-ts");
        }

        public Task<string?> RespondInThreadAsync(MomChannelMessage message, string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("thread-response-ts");
        }

        public Task SetTypingAsync(MomChannelMessage message, bool isTyping, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UploadFileAsync(MomChannelMessage message, string filePath, string? title = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
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
        string? AuthorizationParameter);
}
