using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class MomEventProcessorTests
{
    [Fact]
    public async Task ProcessDueEventsAsync_WithImmediateEvent_QueuesDelegationRequestAndDeletesEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-events-{Guid.NewGuid():N}");
        var options = CreateOptions(root);
        Directory.CreateDirectory(options.EventsPath);
        await File.WriteAllTextAsync(Path.Combine(options.EventsPath, "issue-opened.json"), """
        {
          "type": "immediate",
          "channelId": "C123OPS",
          "text": "New issue opened",
          "provider": "google",
          "model": "gemini-2.5-pro",
          "metadata": {
            "requestId": "evt-1"
          },
          "attachments": [
            "attachments/issue.txt"
          ]
        }
        """);

        try
        {
            var processor = CreateProcessor(options);

            var queued = await processor.ProcessDueEventsAsync(new DateTimeOffset(2026, 5, 10, 9, 30, 0, TimeSpan.Zero));

            Assert.Equal(1, queued);
            Assert.Empty(Directory.GetFiles(options.EventsPath, "*.json"));
            var requestFile = Assert.Single(Directory.GetFiles(options.InboxPath, "*.json"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(requestFile));
            var rootElement = document.RootElement;
            Assert.Equal("google", rootElement.GetProperty("provider").GetString());
            Assert.Equal("gemini-2.5-pro", rootElement.GetProperty("model").GetString());
            Assert.Equal(Path.Combine(options.DefaultWorkingDirectory, "C123OPS"), rootElement.GetProperty("workingDirectory").GetString());
            Assert.Equal("event:issue-opened.json", rootElement.GetProperty("title").GetString());
            Assert.Equal("[EVENT:issue-opened.json:immediate:immediate] New issue opened", rootElement.GetProperty("prompt").GetString());

            var metadata = rootElement.GetProperty("metadata");
            Assert.Equal("true", metadata.GetProperty("event").GetString());
            Assert.Equal("immediate", metadata.GetProperty("eventType").GetString());
            Assert.Equal("issue-opened.json", metadata.GetProperty("eventFile").GetString());
            Assert.Equal("C123OPS", metadata.GetProperty("channel").GetString());
            Assert.Equal("evt-1", metadata.GetProperty("requestId").GetString());
            Assert.Equal("EVENT", metadata.GetProperty("user").GetString());

            var attachment = Assert.Single(rootElement.GetProperty("attachments").EnumerateArray());
            Assert.Equal("attachments/issue.txt", attachment.GetString());
            Assert.True(Directory.Exists(Path.Combine(options.DefaultWorkingDirectory, "C123OPS")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessDueEventsAsync_WithOneShotEvent_OnlyQueuesWhenDue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-events-{Guid.NewGuid():N}");
        var options = CreateOptions(root);
        Directory.CreateDirectory(options.EventsPath);
        var eventPath = Path.Combine(options.EventsPath, "reminder.json");
        await File.WriteAllTextAsync(eventPath, """
        {
          "type": "one-shot",
          "channelId": "D456",
          "text": "standup reminder",
          "at": "2026-05-10T10:00:00+00:00"
        }
        """);

        try
        {
            var processor = CreateProcessor(options);

            var early = await processor.ProcessDueEventsAsync(new DateTimeOffset(2026, 5, 10, 9, 59, 0, TimeSpan.Zero));
            var due = await processor.ProcessDueEventsAsync(new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

            Assert.Equal(0, early);
            Assert.Equal(1, due);
            Assert.False(File.Exists(eventPath));
            var requestJson = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(options.InboxPath, "*.json")));
            Assert.Contains("[EVENT:reminder.json:one-shot:2026-05-10T10:00:00", requestJson, StringComparison.Ordinal);
            Assert.Contains("] standup reminder", requestJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessDueEventsAsync_WithPeriodicEvent_QueuesOncePerDueMinute()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-events-{Guid.NewGuid():N}");
        var options = CreateOptions(root);
        Directory.CreateDirectory(options.EventsPath);
        var eventPath = Path.Combine(options.EventsPath, "daily.json");
        await File.WriteAllTextAsync(eventPath, """
        {
          "type": "periodic",
          "channelId": "C999",
          "text": "daily summary",
          "schedule": "30 9 * * *",
          "timezone": "UTC"
        }
        """);

        try
        {
            var processor = CreateProcessor(options);

            var notDue = await processor.ProcessDueEventsAsync(new DateTimeOffset(2026, 5, 10, 9, 29, 0, TimeSpan.Zero));
            var due = await processor.ProcessDueEventsAsync(new DateTimeOffset(2026, 5, 10, 9, 30, 0, TimeSpan.Zero));
            var duplicate = await processor.ProcessDueEventsAsync(new DateTimeOffset(2026, 5, 10, 9, 30, 30, TimeSpan.Zero));
            var nextDay = await processor.ProcessDueEventsAsync(new DateTimeOffset(2026, 5, 11, 9, 30, 0, TimeSpan.Zero));

            Assert.Equal(0, notDue);
            Assert.Equal(1, due);
            Assert.Equal(0, duplicate);
            Assert.Equal(1, nextDay);
            Assert.True(File.Exists(eventPath));
            Assert.Equal(2, Directory.GetFiles(options.InboxPath, "*.json").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessDueEventsAsync_WithInvalidEvent_ArchivesInvalidFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-events-{Guid.NewGuid():N}");
        var options = CreateOptions(root);
        Directory.CreateDirectory(options.EventsPath);
        var eventPath = Path.Combine(options.EventsPath, "bad.json");
        await File.WriteAllTextAsync(eventPath, "{ not-json");

        try
        {
            var processor = CreateProcessor(options);

            var queued = await processor.ProcessDueEventsAsync(new DateTimeOffset(2026, 5, 10, 9, 30, 0, TimeSpan.Zero));

            Assert.Equal(0, queued);
            Assert.False(File.Exists(eventPath));
            var archived = Assert.Single(Directory.GetFiles(Path.Combine(options.ArchivePath, "invalid-events"), "*.json"));
            Assert.EndsWith("bad.json", archived, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(options.InboxPath, "*.json"));
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
            InboxPath = Path.Combine(root, "inbox"),
            OutboxPath = Path.Combine(root, "outbox"),
            ArchivePath = Path.Combine(root, "archive"),
            EventsPath = Path.Combine(root, "events"),
            DefaultWorkingDirectory = Path.Combine(root, "workdir"),
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.4"
        };
    }

    private static MomEventProcessor CreateProcessor(MomOptions options)
    {
        return new MomEventProcessor(options, new SilentLogger<MomEventProcessor>());
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
