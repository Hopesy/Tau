using System.Text.Json;
using Tau.Ai.Observability;

namespace Tau.Ai.Tests;

public class JsonlTauLogSinkTests
{
    [Fact]
    public void NullTauLogSink_LogIsNoOp()
    {
        var evt = new TauLogEvent("agent", "noop", DateTimeOffset.UtcNow, new Dictionary<string, string?>());
        NullTauLogSink.Instance.Log(evt); // should not throw
    }

    [Fact]
    public void JsonlTauLogSink_WritesOneJsonObjectPerLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-log-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var sink = new JsonlTauLogSink(path))
            {
                sink.Log(new TauLogEvent("agent", "run.start", DateTimeOffset.Parse("2026-05-18T12:00:00Z"), new Dictionary<string, string?>
                {
                    ["provider"] = "openai",
                    ["model"] = "gpt-5.4",
                    ["nullField"] = null
                }));
                sink.Log(new TauLogEvent("auth", "status.checked", DateTimeOffset.Parse("2026-05-18T12:00:01Z"), new Dictionary<string, string?>
                {
                    ["provider"] = "anthropic"
                }));
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);

            using var first = JsonDocument.Parse(lines[0]);
            Assert.Equal("agent", first.RootElement.GetProperty("category").GetString());
            Assert.Equal("run.start", first.RootElement.GetProperty("event").GetString());
            Assert.Equal("openai", first.RootElement.GetProperty("fields").GetProperty("provider").GetString());
            Assert.Equal("gpt-5.4", first.RootElement.GetProperty("fields").GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("fields").GetProperty("nullField").ValueKind);

            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal("auth", second.RootElement.GetProperty("category").GetString());
            Assert.Equal("status.checked", second.RootElement.GetProperty("event").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void JsonlTauLogSink_EscapesQuotesAndControlChars()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-log-escape-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var sink = new JsonlTauLogSink(path))
            {
                sink.Log(new TauLogEvent("test", "weird", DateTimeOffset.UtcNow, new Dictionary<string, string?>
                {
                    ["message"] = "line1\nline2 \"quoted\" \t\\path"
                }));
            }

            var line = File.ReadAllText(path).TrimEnd();
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("line1\nline2 \"quoted\" \t\\path", doc.RootElement.GetProperty("fields").GetProperty("message").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void JsonlTauLogSink_CreatesParentDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tau-log-dir-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(directory, "log.jsonl");
        try
        {
            using (var sink = new JsonlTauLogSink(path))
            {
                sink.Log(new TauLogEvent("test", "boot", DateTimeOffset.UtcNow, new Dictionary<string, string?>()));
            }

            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }

    [Fact]
    public void JsonlTauLogSink_FromEnvironment_UsesExplicitPathWhenSet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-log-env-{Guid.NewGuid():N}.jsonl");
        var previous = Environment.GetEnvironmentVariable("TAU_LOG_FILE");
        try
        {
            Environment.SetEnvironmentVariable("TAU_LOG_FILE", path);
            using var sink = JsonlTauLogSink.FromEnvironment();
            Assert.NotNull(sink);
            Assert.Equal(path, sink!.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAU_LOG_FILE", previous);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void JsonlTauLogSink_FromEnvironment_ReturnsNullWhenDisabled()
    {
        var previousDisabled = Environment.GetEnvironmentVariable("TAU_LOG_DISABLED");
        var previousPath = Environment.GetEnvironmentVariable("TAU_LOG_FILE");
        try
        {
            Environment.SetEnvironmentVariable("TAU_LOG_FILE", null);
            Environment.SetEnvironmentVariable("TAU_LOG_DISABLED", "1");
            Assert.Null(JsonlTauLogSink.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAU_LOG_DISABLED", previousDisabled);
            Environment.SetEnvironmentVariable("TAU_LOG_FILE", previousPath);
        }
    }
}
