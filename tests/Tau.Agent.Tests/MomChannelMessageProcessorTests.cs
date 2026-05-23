using System.Globalization;
using Microsoft.Extensions.Logging;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
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
    public async Task ProcessAsync_WithStopWhileRunningButNoActiveRunner_RespondsWithDetachedState()
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
            Assert.Equal("_Stop requested, but no active runner is attached in this process._", response.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_WithStopWhileActive_CancelsRunnerAndWritesCancelledStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-channel-{Guid.NewGuid():N}");
        var runner = new CancellableDelegationAgentRunner();
        var responder = new RecordingResponder();
        var registry = new MomChannelRunRegistry();
        var processor = CreateProcessor(CreateOptions(root), runner, registry);

        try
        {
            var processing = processor.ProcessAsync(
                new MomChannelMessage("D456", "long work", "1778351400.123456", "U123"),
                responder);
            var startedRequest = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var stopped = await processor.ProcessAsync(
                new MomChannelMessage("D456", "stop", "1778351401.123456", "U123"),
                responder);

            Assert.False(stopped);
            await runner.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await processing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Path.Combine(root, "D456"), startedRequest.WorkingDirectory);
            Assert.Contains(responder.Responses, static response => response.Text == "_Stopping..._");
            Assert.Contains(responder.Responses, static response => response.Text == "_Stopped_");
            Assert.Equal([true, false], responder.Typing);

            var statusJson = await File.ReadAllTextAsync(Path.Combine(root, "D456", "status.json"));
            Assert.Contains("\"state\": \"cancelled\"", statusJson, StringComparison.Ordinal);
            Assert.Contains("\"stopReason\": \"cancelled\"", statusJson, StringComparison.Ordinal);
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
    public async Task ProcessAsync_WithConsecutiveChannelMessages_CarriesSessionModelAndWritesBack()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-channel-session-{Guid.NewGuid():N}");
        var runner = new PersistingSessionDelegationAgentRunner();
        var responder = new RecordingResponder();
        var processor = CreateProcessor(CreateOptions(root), runner);
        var workingDirectory = Path.Combine(root, "CSESSION");
        var contextPath = Path.Combine(workingDirectory, ChannelSessionStore.ContextFileName);

        try
        {
            var firstProcessed = await processor.ProcessAsync(
                new MomChannelMessage(
                    "CSESSION",
                    "first incident update",
                    "1778351400.123456",
                    "U123",
                    Provider: "google",
                    Model: "gemini-2.5-pro",
                    Title: "incident room"),
                responder);
            var secondProcessed = await processor.ProcessAsync(
                new MomChannelMessage(
                    "CSESSION",
                    "second incident update",
                    "1778351460.123456",
                    "U456"),
                responder);

            Assert.True(firstProcessed);
            Assert.True(secondProcessed);
            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal("google-gemini-cli", runner.Requests[0].Provider);
            Assert.Equal("gemini-2.5-pro", runner.Requests[0].Model);
            Assert.Equal("incident room", runner.Requests[0].Title);
            Assert.Equal("google-gemini-cli", runner.Requests[1].Provider);
            Assert.Equal("gemini-2.5-pro", runner.Requests[1].Model);
            Assert.Null(runner.Requests[1].Title);
            Assert.Equal(new[] { 0, 2 }, runner.RestoredMessageCounts);

            var saved = new CodingAgentSessionStore(contextPath).Load();
            Assert.Equal("google-gemini-cli", saved.Provider);
            Assert.Equal("gemini-2.5-pro", saved.Model);
            Assert.Equal("incident room", saved.Name);
            Assert.Equal(4, saved.Messages.Count);
            Assert.Equal(2, responder.Responses.Count);
            Assert.Equal("response-1", responder.Responses[0].Text);
            Assert.Equal("response-2", responder.Responses[1].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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

    private static MomChannelMessageProcessor CreateProcessor(
        MomOptions options,
        IDelegationAgentRunner runner,
        MomChannelRunRegistry? runRegistry = null)
    {
        return new MomChannelMessageProcessor(
            options,
            runner,
            new ChannelStatusStore(new SilentLogger<ChannelStatusStore>()),
            new SilentLogger<MomChannelMessageProcessor>(),
            runRegistry: runRegistry);
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

    private sealed class PersistingSessionDelegationAgentRunner : IDelegationAgentRunner
    {
        public List<DelegationRequest> Requests { get; } = [];
        public List<int> RestoredMessageCounts { get; } = [];

        public Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var contextPath = Path.Combine(request.WorkingDirectory!, ChannelSessionStore.ContextFileName);
            var store = new CodingAgentSessionStore(contextPath);
            var snapshot = store.Load();
            RestoredMessageCounts.Add(snapshot.Messages.Count);

            var response = $"response-{Requests.Count}";
            var messages = snapshot.Messages
                .Concat<ChatMessage>(
                [
                    new UserMessage(request.Prompt),
                    new AssistantMessage([new TextContent(response)])
                ])
                .ToArray();
            var model = new Model
            {
                Provider = request.Provider!,
                Id = request.Model!,
                Name = request.Model!,
                Api = "test"
            };
            store.Save(
                messages,
                model,
                string.IsNullOrWhiteSpace(request.Title) ? snapshot.Name : request.Title);

            return Task.FromResult(new DelegationExecution(
                response,
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

    private sealed class CancellableDelegationAgentRunner : IDelegationAgentRunner
    {
        public TaskCompletionSource<DelegationRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(request);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult(true);
                throw;
            }

            return new DelegationExecution(
                "unexpected",
                [],
                Error: null,
                request.Provider ?? "unknown",
                request.Model ?? "unknown",
                request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                request.Metadata,
                StopReason: "end_turn");
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
