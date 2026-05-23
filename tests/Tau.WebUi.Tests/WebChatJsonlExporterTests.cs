using System.Text.Json;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;

namespace Tau.WebUi.Tests;

public sealed class WebChatJsonlExporterTests
{
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
}
