using System.Text.Json;
using Tau.Ai;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;

namespace Tau.WebUi.Tests;

public sealed class WebChatJsonlExporterTests
{
    private const string Secret = "sk-EXAMPLE_BASE_KEY_99999999";
    private const string GitHubSecret = "ghp_AAAA1111BBBB2222CCCC3333DDDD4444EEEE";

    [Fact]
    public void Render_EmitsSessionHeaderAndMessageEntriesAsLfJsonl()
    {
        var createdAt = DateTimeOffset.Parse("2026-05-23T01:02:03+00:00");
        var updatedAt = DateTimeOffset.Parse("2026-05-23T01:05:03+00:00");
        var userAt = DateTimeOffset.Parse("2026-05-23T01:03:00+00:00");
        var assistantAt = DateTimeOffset.Parse("2026-05-23T01:04:00+00:00");
        var toolCreatedAt = DateTimeOffset.Parse("2026-05-23T01:03:30+00:00");
        var attachment = new WebChatAttachmentDto(
            "att-1",
            "document",
            "notes.txt",
            "text/plain",
            42,
            "aGVsbG8=",
            "hello");
        var toolCall = new WebChatToolCallDto(
            "tool-1",
            "read",
            "completed",
            "{\"path\":\"notes.txt\"}",
            "hello",
            CreatedAt: toolCreatedAt);
        var session = new WebChatSessionDto(
            "session-1",
            "JSONL baseline",
            "openai",
            "gpt-5.4",
            createdAt,
            updatedAt,
            true,
            [
                new WebChatMessageDto("user", "inspect this", userAt, Attachments: [attachment]),
                new WebChatMessageDto(
                    "assistant",
                    "done",
                    assistantAt,
                    "thinking",
                    ["start:read", "end:tool-1:ok"],
                    ToolCalls: [toolCall])
            ]);

        var jsonl = WebChatJsonlExporter.Render(session);

        Assert.EndsWith("\n", jsonl);
        Assert.DoesNotContain("\r\n", jsonl, StringComparison.Ordinal);
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal("session", header.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("session-1", header.RootElement.GetProperty("id").GetString());
        Assert.Equal("JSONL baseline", header.RootElement.GetProperty("title").GetString());
        Assert.Equal("openai", header.RootElement.GetProperty("provider").GetString());
        Assert.Equal("gpt-5.4", header.RootElement.GetProperty("model").GetString());
        Assert.Equal("tau-webui", header.RootElement.GetProperty("source").GetString());

        using var firstMessage = JsonDocument.Parse(lines[1]);
        Assert.Equal("message", firstMessage.RootElement.GetProperty("type").GetString());
        Assert.Equal("message-000001", firstMessage.RootElement.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, firstMessage.RootElement.GetProperty("parentId").ValueKind);
        Assert.Equal("user", firstMessage.RootElement.GetProperty("role").GetString());
        Assert.Equal("inspect this", firstMessage.RootElement.GetProperty("text").GetString());
        var exportedAttachment = firstMessage.RootElement.GetProperty("attachments")[0];
        Assert.Equal("att-1", exportedAttachment.GetProperty("id").GetString());
        Assert.Equal("notes.txt", exportedAttachment.GetProperty("fileName").GetString());
        Assert.Equal("hello", exportedAttachment.GetProperty("extractedText").GetString());

        using var secondMessage = JsonDocument.Parse(lines[2]);
        Assert.Equal("message-000002", secondMessage.RootElement.GetProperty("id").GetString());
        Assert.Equal("message-000001", secondMessage.RootElement.GetProperty("parentId").GetString());
        Assert.Equal("assistant", secondMessage.RootElement.GetProperty("role").GetString());
        Assert.Equal("thinking", secondMessage.RootElement.GetProperty("thinking").GetString());
        Assert.Equal("start:read", secondMessage.RootElement.GetProperty("toolEvents")[0].GetString());
        var exportedToolCall = secondMessage.RootElement.GetProperty("toolCalls")[0];
        Assert.Equal("tool-1", exportedToolCall.GetProperty("id").GetString());
        Assert.Equal("read", exportedToolCall.GetProperty("toolName").GetString());
        Assert.Equal("completed", exportedToolCall.GetProperty("status").GetString());
        Assert.Equal("hello", exportedToolCall.GetProperty("output").GetString());
    }

    [Fact]
    public void Render_RedactsStringValuesAndDisabledRedactorPreservesOriginal()
    {
        var session = CreateSecretJsonlSession();

        var jsonl = WebChatJsonlExporter.Render(session, new TauSecretRedactor(enabled: true));

        Assert.DoesNotContain(Secret, jsonl, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubSecret, jsonl, StringComparison.Ordinal);
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal($"Session {TauSecretRedactor.Placeholder}", header.RootElement.GetProperty("title").GetString());

        using var firstMessage = JsonDocument.Parse(lines[1]);
        Assert.Equal($"Use {TauSecretRedactor.Placeholder}", firstMessage.RootElement.GetProperty("text").GetString());
        var attachment = firstMessage.RootElement.GetProperty("attachments")[0];
        Assert.Equal(TauSecretRedactor.Placeholder, attachment.GetProperty("content").GetString());
        Assert.Equal($"Attachment {TauSecretRedactor.Placeholder}", attachment.GetProperty("extractedText").GetString());

        using var secondMessage = JsonDocument.Parse(lines[2]);
        Assert.Equal($"Thinking {TauSecretRedactor.Placeholder}", secondMessage.RootElement.GetProperty("thinking").GetString());
        Assert.Equal($"error {TauSecretRedactor.Placeholder}", secondMessage.RootElement.GetProperty("error").GetString());
        var toolCall = secondMessage.RootElement.GetProperty("toolCalls")[0];
        Assert.Equal($"{{\"token\":\"{TauSecretRedactor.Placeholder}\"}}", toolCall.GetProperty("arguments").GetString());
        Assert.Equal($"Output {TauSecretRedactor.Placeholder}", toolCall.GetProperty("output").GetString());

        var unredacted = WebChatJsonlExporter.Render(session, new TauSecretRedactor(enabled: false));
        Assert.Contains(Secret, unredacted, StringComparison.Ordinal);
        Assert.Contains(GitHubSecret, unredacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RoundTripsExporterOutputAsWebUiLocalSessionDto()
    {
        var session = CreateJsonlSession();

        var imported = WebChatJsonlImporter.Parse(WebChatJsonlExporter.Render(session));

        Assert.Equal(session.Id, imported.Id);
        Assert.Equal(session.Title, imported.Title);
        Assert.Equal(session.Provider, imported.Provider);
        Assert.Equal(session.Model, imported.Model);
        Assert.Equal(session.CreatedAt, imported.CreatedAt);
        Assert.Equal(session.UpdatedAt, imported.UpdatedAt);
        Assert.False(imported.Persisted);
        Assert.Equal(2, imported.Messages.Count);
        Assert.Equal("user", imported.Messages[0].Role);
        Assert.Equal("inspect this", imported.Messages[0].Text);
        Assert.Equal("att-1", Assert.Single(imported.Messages[0].Attachments!).Id);
        Assert.Equal("assistant", imported.Messages[1].Role);
        Assert.Equal("done", imported.Messages[1].Text);
        Assert.Equal("thinking", imported.Messages[1].Thinking);
        Assert.Equal("start:read", Assert.Single(imported.Messages[1].ToolEvents!, item => item == "start:read"));
        Assert.Equal("tool-1", Assert.Single(imported.Messages[1].ToolCalls!).Id);
    }

    [Fact]
    public void Parse_RoundTripsExporterOutputPreservingSourceMetadata()
    {
        var session = CreateJsonlSession() with
        {
            SourceMetadata = new WebChatSessionSourceMetadataDto(
                "coding-agent-jsonl",
                "coding-session-1",
                3,
                DateTimeOffset.Parse("2026-05-23T01:02:03+00:00"),
                "C:\\Users\\zhouh\\Desktop\\Tau",
                "parent-session.jsonl",
                "source-session.jsonl",
                4,
                3,
                ImportStrategy: new CodingAgentJsonlImportStrategyDto(
                    "conservative-timeline-linearized",
                    "entry-tool",
                    false,
                    true,
                    false,
                    ["webchat_import_is_linearized"]))
        };

        var jsonl = WebChatJsonlExporter.Render(session);
        using var header = JsonDocument.Parse(jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        var sourceMetadata = header.RootElement.GetProperty("sourceMetadata");
        Assert.Equal("coding-agent-jsonl", sourceMetadata.GetProperty("kind").GetString());
        Assert.Equal("coding-session-1", sourceMetadata.GetProperty("sessionId").GetString());
        Assert.Equal("source-session.jsonl", sourceMetadata.GetProperty("filePath").GetString());
        Assert.Equal("conservative-timeline-linearized", sourceMetadata.GetProperty("importStrategy").GetProperty("strategy").GetString());

        var imported = WebChatJsonlImporter.Parse(jsonl);

        Assert.NotNull(imported.SourceMetadata);
        Assert.Equal("coding-agent-jsonl", imported.SourceMetadata!.Kind);
        Assert.Equal("coding-session-1", imported.SourceMetadata.SessionId);
        Assert.Equal(3, imported.SourceMetadata.Version);
        Assert.Equal("C:\\Users\\zhouh\\Desktop\\Tau", imported.SourceMetadata.Cwd);
        Assert.Equal("parent-session.jsonl", imported.SourceMetadata.ParentSession);
        Assert.Equal("source-session.jsonl", imported.SourceMetadata.FilePath);
        Assert.Equal(4, imported.SourceMetadata.EntryCount);
        Assert.Equal(3, imported.SourceMetadata.MessageCount);
        Assert.NotNull(imported.SourceMetadata.ImportStrategy);
        Assert.Equal("conservative-timeline-linearized", imported.SourceMetadata.ImportStrategy!.Strategy);
        Assert.Equal("entry-tool", imported.SourceMetadata.ImportStrategy.SourceLeafEntryId);
        Assert.False(imported.SourceMetadata.ImportStrategy.CurrentBranchOnly);
        Assert.True(imported.SourceMetadata.ImportStrategy.ImportsTimelineMessagesOnly);
        Assert.False(imported.SourceMetadata.ImportStrategy.PersistsBranchTree);
        Assert.Equal(["webchat_import_is_linearized"], imported.SourceMetadata.ImportStrategy.WarningCodes);
    }

    [Fact]
    public void Parse_RedactsImportedStringValuesAndDisabledRedactorPreservesOriginal()
    {
        var source = CreateSecretJsonlSession();
        var rawJsonl = WebChatJsonlExporter.Render(source, new TauSecretRedactor(enabled: false));

        var imported = WebChatJsonlImporter.Parse(rawJsonl, new TauSecretRedactor(enabled: true));

        Assert.Equal($"Session {TauSecretRedactor.Placeholder}", imported.Title);
        Assert.Equal($"Use {TauSecretRedactor.Placeholder}", imported.Messages[0].Text);
        Assert.Equal(TauSecretRedactor.Placeholder, imported.Messages[0].Attachments![0].Content);
        Assert.Equal($"Attachment {TauSecretRedactor.Placeholder}", imported.Messages[0].Attachments![0].ExtractedText);
        Assert.Equal($"Thinking {TauSecretRedactor.Placeholder}", imported.Messages[1].Thinking);
        Assert.Equal($"error {TauSecretRedactor.Placeholder}", imported.Messages[1].Error);
        Assert.Equal($"{{\"token\":\"{TauSecretRedactor.Placeholder}\"}}", imported.Messages[1].ToolCalls![0].Arguments);
        Assert.Equal($"Output {TauSecretRedactor.Placeholder}", imported.Messages[1].ToolCalls![0].Output);

        var unredacted = WebChatJsonlImporter.Parse(rawJsonl, new TauSecretRedactor(enabled: false));
        Assert.Contains(Secret, unredacted.Title, StringComparison.Ordinal);
        Assert.Contains(Secret, unredacted.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(GitHubSecret, unredacted.Messages[0].Attachments![0].Content);
    }

    [Theory]
    [InlineData("{not-json}\n", "invalid_json", 1, "not valid JSON")]
    [InlineData("{\"type\":\"message\",\"id\":\"message-000001\",\"parentId\":null,\"timestamp\":\"2026-05-23T01:03:00+00:00\",\"role\":\"user\",\"text\":\"hello\"}\n", "missing_session_header", 1, "First JSONL line must be a session header")]
    public void Parse_RejectsMalformedJsonlAndMissingSessionHeader(
        string jsonl,
        string expectedCode,
        int expectedLineNumber,
        string expectedMessage)
    {
        var error = Assert.Throws<WebChatJsonlImportException>(() => WebChatJsonlImporter.Parse(jsonl));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedLineNumber, error.LineNumber);
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnsupportedVersionWithStableErrorCode()
    {
        var jsonl = ValidHeader(version: 2);

        var error = Assert.Throws<WebChatJsonlImportException>(() => WebChatJsonlImporter.Parse(jsonl));

        Assert.Equal("unsupported_version", error.Code);
        Assert.Equal(1, error.LineNumber);
        Assert.Contains("Unsupported WebUi JSONL version '2'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsDuplicateMessageIdsWithStableErrorCode()
    {
        var jsonl = ValidHeader() +
            MessageLine("message-000001", null) +
            MessageLine("message-000001", "message-000001");

        var error = Assert.Throws<WebChatJsonlImportException>(() => WebChatJsonlImporter.Parse(jsonl));

        Assert.Equal("duplicate_message_id", error.Code);
        Assert.Equal(3, error.LineNumber);
        Assert.Contains("duplicate message id 'message-000001'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsNonLinearParentChainWithStableErrorCode()
    {
        var jsonl = ValidHeader() +
            MessageLine("message-000001", "unexpected-parent");

        var error = Assert.Throws<WebChatJsonlImportException>(() => WebChatJsonlImporter.Parse(jsonl));

        Assert.Equal("invalid_parent_chain", error.Code);
        Assert.Equal(2, error.LineNumber);
        Assert.Contains("must not have a parentId for the first message", error.Message, StringComparison.Ordinal);
    }

    private static WebChatSessionDto CreateJsonlSession()
    {
        var createdAt = DateTimeOffset.Parse("2026-05-23T01:02:03+00:00");
        var updatedAt = DateTimeOffset.Parse("2026-05-23T01:05:03+00:00");
        var userAt = DateTimeOffset.Parse("2026-05-23T01:03:00+00:00");
        var assistantAt = DateTimeOffset.Parse("2026-05-23T01:04:00+00:00");
        var toolCreatedAt = DateTimeOffset.Parse("2026-05-23T01:03:30+00:00");
        var attachment = new WebChatAttachmentDto(
            "att-1",
            "document",
            "notes.txt",
            "text/plain",
            42,
            "aGVsbG8=",
            "hello");
        var toolCall = new WebChatToolCallDto(
            "tool-1",
            "read",
            "completed",
            "{\"path\":\"notes.txt\"}",
            "hello",
            CreatedAt: toolCreatedAt);
        return new WebChatSessionDto(
            "session-1",
            "JSONL baseline",
            "openai",
            "gpt-5.4",
            createdAt,
            updatedAt,
            true,
            [
                new WebChatMessageDto("user", "inspect this", userAt, Attachments: [attachment]),
                new WebChatMessageDto(
                    "assistant",
                    "done",
                    assistantAt,
                    "thinking",
                    ["start:read", "end:tool-1:ok"],
                    ToolCalls: [toolCall])
            ]);
    }

    private static WebChatSessionDto CreateSecretJsonlSession()
    {
        var createdAt = DateTimeOffset.Parse("2026-05-23T01:02:03+00:00");
        var updatedAt = DateTimeOffset.Parse("2026-05-23T01:05:03+00:00");
        var userAt = DateTimeOffset.Parse("2026-05-23T01:03:00+00:00");
        var assistantAt = DateTimeOffset.Parse("2026-05-23T01:04:00+00:00");
        var attachment = new WebChatAttachmentDto(
            "att-secret",
            "document",
            "secret.txt",
            "text/plain",
            42,
            GitHubSecret,
            $"Attachment {Secret}");
        var toolCall = new WebChatToolCallDto(
            "tool-secret",
            "read",
            "error",
            $"{{\"token\":\"{Secret}\"}}",
            $"Output {GitHubSecret}",
            IsError: true,
            CreatedAt: assistantAt);
        return new WebChatSessionDto(
            "session-secret",
            $"Session {Secret}",
            "openai",
            "gpt-5.4",
            createdAt,
            updatedAt,
            true,
            [
                new WebChatMessageDto("user", $"Use {Secret}", userAt, Attachments: [attachment]),
                new WebChatMessageDto(
                    "assistant",
                    "done",
                    assistantAt,
                    $"Thinking {GitHubSecret}",
                    ["start:read"],
                    $"error {Secret}",
                    ToolCalls: [toolCall])
            ]);
    }

    private static string ValidHeader(int version = 1) =>
        $"{{\"type\":\"session\",\"version\":{version},\"id\":\"session-1\",\"createdAt\":\"2026-05-23T01:02:03+00:00\",\"updatedAt\":\"2026-05-23T01:05:03+00:00\",\"title\":\"JSONL baseline\",\"provider\":\"openai\",\"model\":\"gpt-5.4\",\"source\":\"tau-webui\"}}\n";

    private static string MessageLine(string id, string? parentId)
    {
        var parentJson = parentId is null ? "null" : $"\"{parentId}\"";
        return $"{{\"type\":\"message\",\"id\":\"{id}\",\"parentId\":{parentJson},\"timestamp\":\"2026-05-23T01:03:00+00:00\",\"role\":\"user\",\"text\":\"hello\"}}\n";
    }
}
