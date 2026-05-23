using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class MomLocalDelegationFlowTests
{
    [Fact]
    public async Task ProcessOnceAsync_WithDueEvent_RunsLocalDelegationEndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-flow-{Guid.NewGuid():N}");
        var options = CreateOptions(root);
        var now = new DateTimeOffset(2026, 5, 10, 9, 30, 0, TimeSpan.Zero);
        var channelWorkingDirectory = Path.Combine(options.DefaultWorkingDirectory, "CFLOW");

        Directory.CreateDirectory(options.EventsPath);
        Directory.CreateDirectory(channelWorkingDirectory);
        await File.WriteAllTextAsync(Path.Combine(channelWorkingDirectory, "evidence.txt"), "flow attachment");
        await File.WriteAllTextAsync(Path.Combine(options.EventsPath, "release-check.json"), """
        {
          "type": "immediate",
          "channelId": "CFLOW",
          "text": "Check release status",
          "provider": "google",
          "model": "gemini-2.5-pro",
          "metadata": {
            "requestId": "flow-1"
          },
          "attachments": [
            "evidence.txt"
          ]
        }
        """);

        try
        {
            var runner = new FakeDelegationAgentRunner();
            var flow = CreateFlow(options, runner);

            var result = await flow.ProcessOnceAsync(now);

            Assert.Equal(1, result.QueuedEvents);
            Assert.Equal(1, result.ProcessedRequests);
            Assert.Empty(Directory.GetFiles(options.EventsPath, "*.json"));
            Assert.Empty(Directory.GetFiles(options.InboxPath, "*.json"));
            Assert.Single(Directory.GetFiles(options.ArchivePath, "*.json"));

            var request = Assert.Single(runner.Requests);
            Assert.Equal("[EVENT:release-check.json:immediate:immediate] Check release status", request.Prompt);
            Assert.Equal("google-gemini-cli", request.Provider);
            Assert.Equal("gemini-2.5-pro", request.Model);
            Assert.Equal(channelWorkingDirectory, request.WorkingDirectory);
            Assert.Equal("event:release-check.json", request.Title);
            Assert.Equal("true", request.Metadata!["event"]);
            Assert.Equal("flow-1", request.Metadata["requestId"]);
            var stagedAttachment = Assert.Single(request.Attachments!);
            Assert.EndsWith("_evidence.txt", stagedAttachment, StringComparison.Ordinal);
            Assert.StartsWith("attachments/", stagedAttachment, StringComparison.Ordinal);
            Assert.Equal(
                "flow attachment",
                await File.ReadAllTextAsync(Path.Combine(channelWorkingDirectory, stagedAttachment)));

            var outboxJson = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(options.OutboxPath, "*.json")));
            using var outbox = JsonDocument.Parse(outboxJson);
            var outboxRoot = outbox.RootElement;
            Assert.Equal("flow-response", outboxRoot.GetProperty("response").GetString());
            Assert.Equal("event:release-check.json", outboxRoot.GetProperty("title").GetString());
            Assert.Equal("google-gemini-cli", outboxRoot.GetProperty("provider").GetString());
            Assert.Equal("gemini-2.5-pro", outboxRoot.GetProperty("model").GetString());
            Assert.Equal("flow-1", outboxRoot.GetProperty("metadata").GetProperty("requestId").GetString());
            Assert.Equal("end_turn", outboxRoot.GetProperty("stopReason").GetString());
            Assert.Equal(12, outboxRoot.GetProperty("usage").GetProperty("inputTokens").GetInt32());
            Assert.Equal(stagedAttachment, Assert.Single(outboxRoot.GetProperty("attachments").EnumerateArray()).GetString());

            var statusJson = await File.ReadAllTextAsync(Path.Combine(channelWorkingDirectory, "status.json"));
            using var status = JsonDocument.Parse(statusJson);
            var statusRoot = status.RootElement;
            Assert.Equal("completed", statusRoot.GetProperty("state").GetString());
            Assert.StartsWith("event_release-check_", statusRoot.GetProperty("requestFile").GetString(), StringComparison.Ordinal);
            Assert.Equal("event:release-check.json", statusRoot.GetProperty("title").GetString());
            Assert.Equal("[EVENT:release-check.json:immediate:immediate] Check release status", statusRoot.GetProperty("promptPreview").GetString());
            Assert.Equal("flow-response", statusRoot.GetProperty("responsePreview").GetString());

            var logLines = await File.ReadAllLinesAsync(Path.Combine(channelWorkingDirectory, "log.jsonl"));
            Assert.Equal(2, logLines.Length);
            Assert.Contains("\"user\":\"EVENT\"", logLines[0], StringComparison.Ordinal);
            Assert.Contains("\"text\":\"[EVENT:release-check.json:immediate:immediate] Check release status\"", logLines[0], StringComparison.Ordinal);
            Assert.Contains("\"local\":\"" + stagedAttachment + "\"", logLines[0], StringComparison.Ordinal);
            Assert.Contains("\"user\":\"bot\"", logLines[1], StringComparison.Ordinal);
            Assert.Contains("\"text\":\"flow-response\"", logLines[1], StringComparison.Ordinal);
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
            InboxPath = Path.Combine(root, "inbox"),
            OutboxPath = Path.Combine(root, "outbox"),
            ArchivePath = Path.Combine(root, "archive"),
            EventsPath = Path.Combine(root, "events"),
            DefaultWorkingDirectory = Path.Combine(root, "workdir"),
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4"
        };
    }

    private static MomLocalDelegationFlow CreateFlow(MomOptions options, IDelegationAgentRunner runner)
    {
        return new MomLocalDelegationFlow(
            new MomEventProcessor(options, new SilentLogger<MomEventProcessor>()),
            new FileDelegationProcessor(
                options,
                runner,
                new ChannelStatusStore(new SilentLogger<ChannelStatusStore>()),
                new SilentLogger<FileDelegationProcessor>()));
    }

    private sealed class FakeDelegationAgentRunner : IDelegationAgentRunner
    {
        public List<DelegationRequest> Requests { get; } = [];

        public Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DelegationExecution(
                "flow-response",
                [new DelegationToolEvent("end", "status", "tool-flow", DurationMs: 7)],
                Error: null,
                request.Provider ?? "unknown",
                request.Model ?? "unknown",
                request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                request.Metadata,
                StopReason: "end_turn",
                Usage: new DelegationUsage(InputTokens: 12, OutputTokens: 3)));
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
