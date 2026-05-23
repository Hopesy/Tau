using System.Globalization;
using System.Text;
using System.Text.Json;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public static class WebChatJsonlExporter
{
    public static string Render(WebChatSessionDto session)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        var builder = new StringBuilder(Math.Max(1024, session.Messages.Count * 512));
        var header = new WebChatJsonlSessionHeader(
            "session",
            1,
            session.Id,
            session.CreatedAt,
            session.UpdatedAt,
            session.Title,
            session.Provider,
            session.Model,
            "tau-webui");
        AppendJsonl(builder, header, WebUiJsonlContext.Default.WebChatJsonlSessionHeader);

        string? parentId = null;
        for (var index = 0; index < session.Messages.Count; index++)
        {
            var message = session.Messages[index];
            var id = CreateMessageId(index);
            var entry = new WebChatJsonlMessageEntry(
                "message",
                id,
                parentId,
                message.Timestamp,
                message.Role,
                message.Text,
                message.Thinking,
                message.Error,
                message.ToolEvents,
                message.ToolCalls,
                message.Attachments);
            AppendJsonl(builder, entry, WebUiJsonlContext.Default.WebChatJsonlMessageEntry);
            parentId = id;
        }

        return builder.ToString();
    }

    private static void AppendJsonl<T>(StringBuilder builder, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        builder.Append(JsonSerializer.Serialize(value, jsonTypeInfo));
        builder.Append('\n');
    }

    private static string CreateMessageId(int zeroBasedIndex) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"message-{zeroBasedIndex + 1:000000}");
}

internal sealed record WebChatJsonlSessionHeader(
    string Type,
    int Version,
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Title,
    string Provider,
    string Model,
    string Source);

internal sealed record WebChatJsonlMessageEntry(
    string Type,
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string Role,
    string Text,
    string? Thinking,
    string? Error,
    IReadOnlyList<string>? ToolEvents,
    IReadOnlyList<WebChatToolCallDto>? ToolCalls,
    IReadOnlyList<WebChatAttachmentDto>? Attachments);
