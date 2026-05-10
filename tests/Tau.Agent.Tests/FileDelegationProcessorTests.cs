using System.Globalization;
using Microsoft.Extensions.Logging;
using Tau.Mom;

namespace Tau.Agent.Tests;

public class FileDelegationProcessorTests
{
    [Fact]
    public async Task ProcessPendingAsync_WithJsonRequest_WritesStructuredOutboxAndArchivesInput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var outbox = Path.Combine(root, "outbox");
        var archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(inbox);
        var attachmentPath = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(attachmentPath, "mom attachment");

        var requestPath = Path.Combine(inbox, "delegation.json");
        await File.WriteAllTextAsync(requestPath, """
        {
          "prompt": "inspect project",
          "provider": "google",
          "model": "google-gemini-cli/gemini-2.5-pro",
          "workingDirectory": ".",
          "title": "local triage",
          "metadata": {
            "channel": "local-test",
            "requestId": "abc-123",
            "ts": "111.222",
            "user": "ULOCAL",
            "userName": "local-user",
            "displayName": "Local User"
          },
          "attachments": [
            "notes.txt"
          ]
        }
        """);

        var options = new MomOptions
        {
            InboxPath = inbox,
            OutboxPath = outbox,
            ArchivePath = archive,
            DefaultWorkingDirectory = root,
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4"
        };

        var runner = new FakeDelegationAgentRunner();
        var processor = CreateProcessor(options, runner);

        var processed = await processor.ProcessPendingAsync();

        Assert.Equal(1, processed);
        var captured = Assert.Single(runner.Requests);
        Assert.Equal("inspect project", captured.Prompt);
        Assert.Equal("google-gemini-cli", captured.Provider);
        Assert.Equal("gemini-2.5-pro", captured.Model);
        Assert.Equal(Path.GetFullPath(root), captured.WorkingDirectory);
        Assert.Equal("local triage", captured.Title);
        var stagedAttachment = Path.Combine("attachments", "111222_notes.txt").Replace("\\", "/", StringComparison.Ordinal);
        Assert.Equal([stagedAttachment], captured.Attachments);
        var stagedAttachmentPath = Path.Combine(root, "attachments", "111222_notes.txt");
        Assert.True(File.Exists(stagedAttachmentPath));
        Assert.Equal("mom attachment", await File.ReadAllTextAsync(stagedAttachmentPath));

        var outboxFile = Assert.Single(Directory.GetFiles(outbox, "*.json"));
        var outboxJson = await File.ReadAllTextAsync(outboxFile);
        Assert.Contains("\"title\": \"local triage\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"provider\": \"google-gemini-cli\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"model\": \"gemini-2.5-pro\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"workingDirectory\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"requestId\": \"abc-123\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("attachments/111222_notes.txt", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"stopReason\": \"end_turn\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"phase\": \"start\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"phase\": \"end\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"toolName\": \"ls\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"toolCallId\": \"tool-1\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"isError\": false", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"durationMs\": 42", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"inputTokens\": 100", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"outputTokens\": 25", outboxJson, StringComparison.Ordinal);

        var logFile = Path.Combine(root, "log.jsonl");
        var logLines = await File.ReadAllLinesAsync(logFile);
        Assert.Equal(2, logLines.Length);
        Assert.Contains("\"ts\":\"111.222\"", logLines[0], StringComparison.Ordinal);
        Assert.Contains("\"user\":\"ULOCAL\"", logLines[0], StringComparison.Ordinal);
        Assert.Contains("\"userName\":\"local-user\"", logLines[0], StringComparison.Ordinal);
        Assert.Contains("\"displayName\":\"Local User\"", logLines[0], StringComparison.Ordinal);
        Assert.Contains("\"text\":\"inspect project\"", logLines[0], StringComparison.Ordinal);
        Assert.Contains("\"local\":\"attachments/111222_notes.txt\"", logLines[0], StringComparison.Ordinal);
        Assert.Contains("\"original\":\"notes.txt\"", logLines[0], StringComparison.Ordinal);
        Assert.Contains("\"isBot\":false", logLines[0], StringComparison.Ordinal);
        Assert.Contains("\"user\":\"bot\"", logLines[1], StringComparison.Ordinal);
        Assert.Contains("\"text\":\"stub-response\"", logLines[1], StringComparison.Ordinal);
        Assert.Contains("\"isBot\":true", logLines[1], StringComparison.Ordinal);

        var attachmentManifest = await File.ReadAllTextAsync(Path.Combine(root, "attachments", "attachments.jsonl"));
        Assert.Contains("\"original\":\"notes.txt\"", attachmentManifest, StringComparison.Ordinal);
        Assert.Contains("\"local\":\"attachments/111222_notes.txt\"", attachmentManifest, StringComparison.Ordinal);

        var statusJson = await File.ReadAllTextAsync(Path.Combine(root, "status.json"));
        Assert.Contains("\"state\": \"completed\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"requestFile\": \"delegation.json\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"title\": \"local triage\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"promptPreview\": \"inspect project\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"responsePreview\": \"stub-response\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"stopReason\": \"end_turn\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"durationMs\"", statusJson, StringComparison.Ordinal);

        var archived = Assert.Single(Directory.GetFiles(archive));
        Assert.EndsWith("delegation.json", archived, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ProcessPendingAsync_WhenChannelLogContainsMalformedLines_DoesNotDuplicateExistingRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var outbox = Path.Combine(root, "outbox");
        var archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(root, "log.jsonl"), """
        {not-valid-json}
        {"date":"2026-05-05T01:00:00Z","ts":"dup-ts","user":"U1","text":"existing request","isBot":false}
        """);

        var requestPath = Path.Combine(inbox, "delegation.json");
        await File.WriteAllTextAsync(requestPath, """
        {
          "prompt": "existing request",
          "metadata": {
            "ts": "dup-ts",
            "user": "U1"
          }
        }
        """);

        var options = new MomOptions
        {
            InboxPath = inbox,
            OutboxPath = outbox,
            ArchivePath = archive,
            DefaultWorkingDirectory = root,
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4"
        };

        var processor = CreateProcessor(options, new FakeDelegationAgentRunner());

        var processed = await processor.ProcessPendingAsync();

        Assert.Equal(1, processed);
        var logLines = await File.ReadAllLinesAsync(Path.Combine(root, "log.jsonl"));
        Assert.Equal(3, logLines.Length);
        Assert.Equal(1, logLines.Count(line => line.Contains("\"ts\":\"dup-ts\"", StringComparison.Ordinal)));
        Assert.Contains("\"user\":\"bot\"", logLines[2], StringComparison.Ordinal);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ProcessPendingAsync_WhenRunnerReturnsError_WritesFailedStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var outbox = Path.Combine(root, "outbox");
        var archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(inbox);

        await File.WriteAllTextAsync(Path.Combine(inbox, "delegation.txt"), "please fail");

        var options = new MomOptions
        {
            InboxPath = inbox,
            OutboxPath = outbox,
            ArchivePath = archive,
            DefaultWorkingDirectory = root,
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4"
        };

        var processor = CreateProcessor(options, new FakeDelegationAgentRunner { Error = "provider unavailable" });

        var processed = await processor.ProcessPendingAsync();

        Assert.Equal(1, processed);
        var statusJson = await File.ReadAllTextAsync(Path.Combine(root, "status.json"));
        Assert.Contains("\"state\": \"failed\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"requestFile\": \"delegation.txt\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"error\": \"provider unavailable\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"stopReason\": \"error\"", statusJson, StringComparison.Ordinal);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ProcessPendingAsync_WhenWorkingDirectoryIsAlreadyRunning_SkipsRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var outbox = Path.Combine(root, "outbox");
        var archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(inbox);

        var requestPath = Path.Combine(inbox, "delegation.txt");
        await File.WriteAllTextAsync(requestPath, "queued request");
        await WriteRunningStatusAsync(root, DateTimeOffset.UtcNow);

        var options = new MomOptions
        {
            InboxPath = inbox,
            OutboxPath = outbox,
            ArchivePath = archive,
            DefaultWorkingDirectory = root,
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4",
            RunningStatusStaleAfterMinutes = 60
        };

        var runner = new FakeDelegationAgentRunner();
        var processor = CreateProcessor(options, runner);

        var processed = await processor.ProcessPendingAsync();

        Assert.Equal(0, processed);
        Assert.Empty(runner.Requests);
        Assert.True(File.Exists(requestPath));
        Assert.Empty(Directory.GetFiles(outbox, "*.json"));
        Assert.Empty(Directory.GetFiles(archive));

        var statusJson = await File.ReadAllTextAsync(Path.Combine(root, "status.json"));
        Assert.Contains("\"state\": \"running\"", statusJson, StringComparison.Ordinal);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ProcessPendingAsync_WhenRunningStatusIsStale_ProcessesRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var outbox = Path.Combine(root, "outbox");
        var archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(inbox);

        var requestPath = Path.Combine(inbox, "delegation.txt");
        await File.WriteAllTextAsync(requestPath, "queued request");
        await WriteRunningStatusAsync(root, DateTimeOffset.UtcNow.AddMinutes(-10));

        var options = new MomOptions
        {
            InboxPath = inbox,
            OutboxPath = outbox,
            ArchivePath = archive,
            DefaultWorkingDirectory = root,
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4",
            RunningStatusStaleAfterMinutes = 1
        };

        var runner = new FakeDelegationAgentRunner();
        var processor = CreateProcessor(options, runner);

        var processed = await processor.ProcessPendingAsync();

        Assert.Equal(1, processed);
        Assert.Single(runner.Requests);
        Assert.False(File.Exists(requestPath));
        Assert.Single(Directory.GetFiles(outbox, "*.json"));
        Assert.Single(Directory.GetFiles(archive));

        var statusJson = await File.ReadAllTextAsync(Path.Combine(root, "status.json"));
        Assert.Contains("\"state\": \"completed\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"requestFile\": \"delegation.txt\"", statusJson, StringComparison.Ordinal);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ProcessPendingAsync_WithTextRequest_UsesDefaultProviderAndModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var outbox = Path.Combine(root, "outbox");
        var archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(inbox);

        var requestPath = Path.Combine(inbox, "delegation.txt");
        await File.WriteAllTextAsync(requestPath, "hello from mom");

        var options = new MomOptions
        {
            InboxPath = inbox,
            OutboxPath = outbox,
            ArchivePath = archive,
            DefaultWorkingDirectory = root,
            DefaultProvider = "anthropic",
            DefaultModel = "default"
        };

        var runner = new FakeDelegationAgentRunner();
        var processor = CreateProcessor(options, runner);

        var processed = await processor.ProcessPendingAsync();

        Assert.Equal(1, processed);
        var captured = Assert.Single(runner.Requests);
        Assert.Equal("anthropic", captured.Provider);
        Assert.Equal("claude-opus-4-6", captured.Model);
        Assert.Equal("hello from mom", captured.Prompt);

        Directory.Delete(root, recursive: true);
    }

    private sealed class FakeDelegationAgentRunner : IDelegationAgentRunner
    {
        public List<DelegationRequest> Requests { get; } = [];
        public string? Error { get; init; }

        public Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DelegationExecution(
                Error is null ? "stub-response" : string.Empty,
                [
                    new DelegationToolEvent("start", "ls", "tool-1"),
                    new DelegationToolEvent("end", "ls", "tool-1", IsError: false, DurationMs: 42)
                ],
                Error,
                request.Provider ?? "unknown",
                request.Model ?? "unknown",
                request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                request.Metadata,
                StopReason: Error is null ? "end_turn" : "error",
                Usage: new DelegationUsage(InputTokens: 100, OutputTokens: 25)));
        }
    }

    private static FileDelegationProcessor CreateProcessor(MomOptions options, IDelegationAgentRunner runner)
    {
        return new FileDelegationProcessor(
            options,
            runner,
            new ChannelStatusStore(new SilentLogger<ChannelStatusStore>()),
            new SilentLogger<FileDelegationProcessor>());
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

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
