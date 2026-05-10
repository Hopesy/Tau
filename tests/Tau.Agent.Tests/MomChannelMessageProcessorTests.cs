using System.Globalization;
using Microsoft.Extensions.Logging;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class MomChannelMessageProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WithChannelMessage_DelegatesAndRespondsInThread()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-channel-{Guid.NewGuid():N}");
        var attachmentPath = Path.Combine(root, "C123OPS", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
        await File.WriteAllTextAsync(attachmentPath, "channel attachment");

        try
        {
            var options = CreateOptions(root);
            var runner = new FakeDelegationAgentRunner();
            var responder = new RecordingResponder();
            var processor = CreateProcessor(options, runner);

            var processed = await processor.ProcessAsync(
                new MomChannelMessage(
                    "C123OPS",
                    "inspect deployment",
                    "1778351400.123456",
                    "U123",
                    [new MomChannelAttachment("notes.txt", "notes.txt")],
                    "alice",
                    "Alice Ops",
                    "1778351000.000001",
                    "google",
                    "gemini-2.5-pro",
                    "deployment triage"),
                responder);

            Assert.True(processed);
            var request = Assert.Single(runner.Requests);
            Assert.Equal("inspect deployment", request.Prompt);
            Assert.Equal("google-gemini-cli", request.Provider);
            Assert.Equal("gemini-2.5-pro", request.Model);
            Assert.Equal(Path.Combine(root, "C123OPS"), request.WorkingDirectory);
            Assert.Equal("deployment triage", request.Title);
            Assert.Equal("C123OPS", request.Metadata!["channel"]);
            Assert.Equal("U123", request.Metadata["user"]);
            Assert.Equal("alice", request.Metadata["userName"]);
            Assert.Equal("1778351000.000001", request.Metadata["threadTs"]);
            Assert.Contains("attachments/1778351400123_notes.txt", request.Attachments!);

            Assert.Equal([true, false], responder.Typing);
            var threaded = Assert.Single(responder.ThreadResponses);
            Assert.Equal("stub-response", threaded.Text);
            Assert.Equal("1778351000.000001", threaded.Message.ThreadTs);
            Assert.Empty(responder.Responses);

            var statusJson = await File.ReadAllTextAsync(Path.Combine(root, "C123OPS", "status.json"));
            Assert.Contains("\"state\": \"completed\"", statusJson, StringComparison.Ordinal);
            Assert.Contains("\"requestFile\": \"channel:C123OPS:1778351400.123456\"", statusJson, StringComparison.Ordinal);

            var logLines = await File.ReadAllLinesAsync(Path.Combine(root, "C123OPS", "log.jsonl"));
            Assert.Equal(2, logLines.Length);
            Assert.Contains("\"user\":\"U123\"", logLines[0], StringComparison.Ordinal);
            Assert.Contains("\"text\":\"inspect deployment\"", logLines[0], StringComparison.Ordinal);
            Assert.Contains("\"user\":\"bot\"", logLines[1], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WhenChannelIsRunning_RespondsBusyWithoutDelegating()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-channel-{Guid.NewGuid():N}");
        var channelDir = Path.Combine(root, "C123OPS");
        Directory.CreateDirectory(channelDir);
        await WriteRunningStatusAsync(channelDir, DateTimeOffset.UtcNow);

        try
        {
            var runner = new FakeDelegationAgentRunner();
            var responder = new RecordingResponder();
            var processor = CreateProcessor(CreateOptions(root), runner);

            var processed = await processor.ProcessAsync(
                new MomChannelMessage("C123OPS", "new work", "1778351400.123456", "U123"),
                responder);

            Assert.False(processed);
            Assert.Empty(runner.Requests);
            var response = Assert.Single(responder.Responses);
            Assert.Equal("_Already working. Say `stop` to cancel._", response.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_WithStopWhileRunning_RespondsWithStopPlaceholder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-channel-{Guid.NewGuid():N}");
        var channelDir = Path.Combine(root, "D456");
        Directory.CreateDirectory(channelDir);
        await WriteRunningStatusAsync(channelDir, DateTimeOffset.UtcNow);

        try
        {
            var runner = new FakeDelegationAgentRunner();
            var responder = new RecordingResponder();
            var processor = CreateProcessor(CreateOptions(root), runner);

            var processed = await processor.ProcessAsync(
                new MomChannelMessage("D456", "stop", "1778351400.123456", "U123"),
                responder);

            Assert.False(processed);
            Assert.Empty(runner.Requests);
            var response = Assert.Single(responder.Responses);
            Assert.Equal("_Stop requested. Cancellation is not wired for this local runner yet._", response.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MomOptions CreateOptions(string root)
    {
        return new MomOptions
        {
            DefaultWorkingDirectory = root,
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4",
            RunningStatusStaleAfterMinutes = 60
        };
    }

    private static MomChannelMessageProcessor CreateProcessor(MomOptions options, IDelegationAgentRunner runner)
    {
        return new MomChannelMessageProcessor(
            options,
            runner,
            new ChannelStatusStore(new SilentLogger<ChannelStatusStore>()),
            new SilentLogger<MomChannelMessageProcessor>());
    }

    private static Task WriteRunningStatusAsync(string workingDirectory, DateTimeOffset updatedAt)
    {
        var escapedWorkingDirectory = workingDirectory.Replace("\\", "\\\\", StringComparison.Ordinal);
        var timestamp = updatedAt.ToString("O", CultureInfo.InvariantCulture);
        var json = $$"""
        {
          "state": "running",
          "requestFile": "current.txt",
          "provider": "openai",
          "model": "gpt-5.4",
          "workingDirectory": "{{escapedWorkingDirectory}}",
          "startedAt": "{{timestamp}}",
          "updatedAt": "{{timestamp}}"
        }
        """;
        return File.WriteAllTextAsync(Path.Combine(workingDirectory, "status.json"), json);
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
                StopReason: "end_turn",
                Usage: new DelegationUsage(InputTokens: 10, OutputTokens: 5)));
        }
    }

    private sealed class RecordingResponder : IMomChannelResponder
    {
        public List<(MomChannelMessage Message, string Text)> Responses { get; } = [];
        public List<(MomChannelMessage Message, string Text)> ThreadResponses { get; } = [];
        public List<bool> Typing { get; } = [];
        public List<(MomChannelMessage Message, string FilePath, string? Title)> Uploads { get; } = [];

        public Task<string?> RespondAsync(MomChannelMessage message, string text, CancellationToken cancellationToken = default)
        {
            Responses.Add((message, text));
            return Task.FromResult<string?>("response-ts");
        }

        public Task<string?> RespondInThreadAsync(MomChannelMessage message, string text, CancellationToken cancellationToken = default)
        {
            ThreadResponses.Add((message, text));
            return Task.FromResult<string?>("thread-response-ts");
        }

        public Task SetTypingAsync(MomChannelMessage message, bool isTyping, CancellationToken cancellationToken = default)
        {
            Typing.Add(isTyping);
            return Task.CompletedTask;
        }

        public Task UploadFileAsync(MomChannelMessage message, string filePath, string? title = null, CancellationToken cancellationToken = default)
        {
            Uploads.Add((message, filePath, title));
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
}
