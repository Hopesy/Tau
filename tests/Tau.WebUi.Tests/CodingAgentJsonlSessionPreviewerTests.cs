using Tau.Ai;
using Tau.WebUi.Services;

namespace Tau.WebUi.Tests;

public sealed class CodingAgentJsonlSessionPreviewerTests
{
    private const string Secret = "sk-EXAMPLE_BASE_KEY_99999999";

    [Fact]
    public void Parse_ReturnsHeaderAndMessageTimelineSummary()
    {
        var preview = CodingAgentJsonlSessionPreviewer.Parse(ValidCodingAgentJsonl(), "session.jsonl");

        Assert.Equal("coding-session-1", preview.SessionId);
        Assert.Equal(3, preview.Version);
        Assert.Equal(DateTimeOffset.Parse("2026-05-23T02:00:00+00:00"), preview.Timestamp);
        Assert.Equal("C:\\Users\\zhouh\\Desktop\\Tau", preview.Cwd);
        Assert.Equal("parent-session.jsonl", preview.ParentSession);
        Assert.Equal("session.jsonl", preview.FilePath);
        Assert.Equal(4, preview.EntryCount);
        Assert.Equal(3, preview.MessageCount);

        var user = preview.Messages[0];
        Assert.Equal("entry-user", user.EntryId);
        Assert.Null(user.ParentEntryId);
        Assert.Equal("user", user.Role);
        Assert.Equal("hello coding agent", user.TextPreview);
        Assert.Equal("hello coding agent".Length, user.TextLength);
        Assert.Equal(1, user.ContentPartCount);
        Assert.False(user.HasThinking);
        Assert.Equal(0, user.ToolCallCount);
        Assert.Equal(0, user.ImageCount);

        var assistant = preview.Messages[1];
        Assert.Equal("entry-assistant", assistant.EntryId);
        Assert.Equal("entry-model", assistant.ParentEntryId);
        Assert.Equal("assistant", assistant.Role);
        Assert.Equal("done", assistant.TextPreview);
        Assert.True(assistant.HasThinking);
        Assert.Equal(1, assistant.ToolCallCount);
        Assert.Equal(3, assistant.ContentPartCount);

        var toolResult = preview.Messages[2];
        Assert.Equal("entry-tool", toolResult.EntryId);
        Assert.Equal("toolResult", toolResult.Role);
        Assert.Equal("tool-1", toolResult.ToolCallId);
        Assert.True(toolResult.IsError);
        Assert.Equal("not found", toolResult.TextPreview);
    }

    [Fact]
    public void Parse_RedactsPreviewTextValuesAndDisabledRedactorPreservesOriginal()
    {
        var jsonl = ValidHeader() +
            $"{{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":\"{Secret}\"}}]}}}}\n";

        var preview = CodingAgentJsonlSessionPreviewer.Parse(jsonl, redactor: new TauSecretRedactor(enabled: true));

        var message = Assert.Single(preview.Messages);
        Assert.Equal(TauSecretRedactor.Placeholder, message.TextPreview);
        Assert.Equal(TauSecretRedactor.Placeholder.Length, message.TextLength);

        var unredacted = CodingAgentJsonlSessionPreviewer.Parse(jsonl, redactor: new TauSecretRedactor(enabled: false));
        Assert.Equal(Secret, Assert.Single(unredacted.Messages).TextPreview);
    }

    [Fact]
    public void ParseFile_ReadsSessionFileAndIncludesPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-webui-coding-agent-preview-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, ValidCodingAgentJsonl());

            var preview = CodingAgentJsonlSessionPreviewer.ParseFile(path);

            Assert.Equal(path, preview.FilePath);
            Assert.Equal("coding-session-1", preview.SessionId);
            Assert.Equal(3, preview.MessageCount);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Theory]
    [InlineData("{not-json}\n", "invalid_json", 1, "not valid JSON")]
    [InlineData("{\"type\":\"message\",\"id\":\"entry-user\",\"timestamp\":\"2026-05-23T02:01:00+00:00\"}\n", "missing_session_header", 1, "First CodingAgent JSONL line must be a session header")]
    public void Parse_RejectsMalformedHeaderWithStableErrorCode(
        string jsonl,
        string expectedCode,
        int expectedLineNumber,
        string expectedMessage)
    {
        var error = Assert.Throws<CodingAgentJsonlPreviewException>(() => CodingAgentJsonlSessionPreviewer.Parse(jsonl));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedLineNumber, error.LineNumber);
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMalformedEntryJsonWithStableErrorCode()
    {
        var jsonl = ValidHeader() + "{not-json}\n";

        var error = Assert.Throws<CodingAgentJsonlPreviewException>(() => CodingAgentJsonlSessionPreviewer.Parse(jsonl));

        Assert.Equal("invalid_json", error.Code);
        Assert.Equal(2, error.LineNumber);
        Assert.Contains("line 2 is not valid JSON", error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(error.InnerException);
    }

    [Fact]
    public void Parse_RejectsMessageEntryWithoutMessagePayloadWithStableErrorCode()
    {
        var jsonl = ValidHeader() +
            "{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\"}\n";

        var error = Assert.Throws<CodingAgentJsonlPreviewException>(() => CodingAgentJsonlSessionPreviewer.Parse(jsonl));

        Assert.Equal("missing_message", error.Code);
        Assert.Equal(2, error.LineNumber);
        Assert.Contains("without a message payload", error.Message, StringComparison.Ordinal);
    }

    private static string ValidCodingAgentJsonl() =>
        ValidHeader() +
        "{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello coding agent\"}]}}\n" +
        "{\"type\":\"model_change\",\"id\":\"entry-model\",\"parentId\":\"entry-user\",\"timestamp\":\"2026-05-23T02:01:30+00:00\",\"provider\":\"openai\",\"model\":\"gpt-5.4\"}\n" +
        "{\"type\":\"message\",\"id\":\"entry-assistant\",\"parentId\":\"entry-model\",\"timestamp\":\"2026-05-23T02:02:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"plan\"},{\"type\":\"toolCall\",\"id\":\"tool-1\",\"name\":\"read\",\"arguments\":\"{}\"},{\"type\":\"text\",\"text\":\"done\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-tool\",\"parentId\":\"entry-assistant\",\"timestamp\":\"2026-05-23T02:03:00+00:00\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"tool-1\",\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"not found\"}]}}\n";

    private static string ValidHeader() =>
        "{\"type\":\"session\",\"version\":3,\"id\":\"coding-session-1\",\"timestamp\":\"2026-05-23T02:00:00+00:00\",\"cwd\":\"C:\\\\Users\\\\zhouh\\\\Desktop\\\\Tau\",\"parentSession\":\"parent-session.jsonl\"}\n";
}
