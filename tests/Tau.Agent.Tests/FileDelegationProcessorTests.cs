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

        var requestPath = Path.Combine(inbox, "delegation.json");
        await File.WriteAllTextAsync(requestPath, """
        {
          "prompt": "inspect project",
          "provider": "google",
          "model": "google-gemini-cli/gemini-2.5-pro",
          "workingDirectory": ".",
          "metadata": {
            "channel": "local-test",
            "requestId": "abc-123"
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

        var runner = new FakeDelegationAgentRunner();
        var processor = new FileDelegationProcessor(options, runner, new SilentLogger<FileDelegationProcessor>());

        var processed = await processor.ProcessPendingAsync();

        Assert.Equal(1, processed);
        var captured = Assert.Single(runner.Requests);
        Assert.Equal("inspect project", captured.Prompt);
        Assert.Equal("google-gemini-cli", captured.Provider);
        Assert.Equal("gemini-2.5-pro", captured.Model);
        Assert.Equal(Path.GetFullPath(root), captured.WorkingDirectory);

        var outboxFile = Assert.Single(Directory.GetFiles(outbox, "*.json"));
        var outboxJson = await File.ReadAllTextAsync(outboxFile);
        Assert.Contains("\"provider\": \"google-gemini-cli\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"model\": \"gemini-2.5-pro\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"workingDirectory\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"requestId\": \"abc-123\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"stopReason\": \"end_turn\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"phase\": \"start\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"phase\": \"end\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"toolName\": \"ls\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"toolCallId\": \"tool-1\"", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"isError\": false", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"durationMs\": 42", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"inputTokens\": 100", outboxJson, StringComparison.Ordinal);
        Assert.Contains("\"outputTokens\": 25", outboxJson, StringComparison.Ordinal);

        var archived = Assert.Single(Directory.GetFiles(archive));
        Assert.EndsWith("delegation.json", archived, StringComparison.OrdinalIgnoreCase);

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
        var processor = new FileDelegationProcessor(options, runner, new SilentLogger<FileDelegationProcessor>());

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

        public Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DelegationExecution(
                "stub-response",
                [
                    new DelegationToolEvent("start", "ls", "tool-1"),
                    new DelegationToolEvent("end", "ls", "tool-1", IsError: false, DurationMs: 42)
                ],
                null,
                request.Provider ?? "unknown",
                request.Model ?? "unknown",
                request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                request.Metadata,
                StopReason: "end_turn",
                Usage: new DelegationUsage(InputTokens: 100, OutputTokens: 25)));
        }
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
