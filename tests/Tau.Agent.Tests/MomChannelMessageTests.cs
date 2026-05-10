using Tau.Mom;

namespace Tau.Agent.Tests;

public sealed class MomChannelMessageTests
{
    [Fact]
    public void ToDelegationRequest_PreservesSlackCompatibleEnvelopeFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-mom-message-{Guid.NewGuid():N}");

        try
        {
            var request = new MomChannelMessage(
                    "C123OPS",
                    "inspect deployment",
                    "1778351400.123456",
                    "U123",
                    [
                        new MomChannelAttachment("report.txt", "attachments/1778351400123_report.txt", "https://slack.example/report.txt")
                    ],
                    "alice",
                    "Alice Ops",
                    "1778351000.000001",
                    "openai",
                    "gpt-5.4",
                    "deployment triage",
                    new Dictionary<string, string>
                    {
                        ["requestId"] = "req-1"
                    })
                .ToDelegationRequest(root);

            Assert.Equal("inspect deployment", request.Prompt);
            Assert.Equal("openai", request.Provider);
            Assert.Equal("gpt-5.4", request.Model);
            Assert.Equal(Path.GetFullPath(root), request.WorkingDirectory);
            Assert.Equal("deployment triage", request.Title);
            Assert.Equal(["attachments/1778351400123_report.txt"], request.Attachments);
            Assert.NotNull(request.Metadata);
            Assert.Equal("C123OPS", request.Metadata["channel"]);
            Assert.Equal("U123", request.Metadata["user"]);
            Assert.Equal("alice", request.Metadata["userName"]);
            Assert.Equal("Alice Ops", request.Metadata["displayName"]);
            Assert.Equal("1778351400.123456", request.Metadata["ts"]);
            Assert.Equal("1778351000.000001", request.Metadata["threadTs"]);
            Assert.Equal("req-1", request.Metadata["requestId"]);
            Assert.Equal("2026-05-09T18:30:00.1230000+00:00", request.Metadata["date"]);
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
    public void FromDelegationRequest_FillsLocalChannelDefaults()
    {
        var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
        var message = MomChannelMessage.FromDelegationRequest(
            new DelegationRequest(
                "local request",
                "anthropic",
                "claude-opus-4-6",
                Title: "local title",
                Attachments: ["notes.txt"]),
            now);

        var request = message.ToDelegationRequest(Path.GetTempPath());

        Assert.Equal("local request", request.Prompt);
        Assert.Equal("anthropic", request.Provider);
        Assert.Equal("claude-opus-4-6", request.Model);
        Assert.Equal("local title", request.Title);
        Assert.Equal(["notes.txt"], request.Attachments);
        Assert.NotNull(request.Metadata);
        Assert.Equal("local", request.Metadata["channel"]);
        Assert.Equal("local", request.Metadata["user"]);
        Assert.Equal("1778414400000", request.Metadata["ts"]);
        Assert.Equal("2026-05-10T12:00:00.0000000+00:00", request.Metadata["date"]);
    }
}
