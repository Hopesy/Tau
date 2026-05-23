using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public static class WebChatJsonlImporter
{
    public static WebChatSessionDto Parse(string jsonl)
    {
        if (string.IsNullOrWhiteSpace(jsonl))
        {
            throw new WebChatJsonlImportException(
                "missing_session_header",
                "JSONL session header is required.");
        }

        using var reader = new StringReader(jsonl);
        WebChatJsonlSessionHeader? header = null;
        var messages = new List<WebChatMessageDto>();
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);
        string? previousMessageId = null;
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (lineNumber == 1)
            {
                line = line.TrimStart('\uFEFF');
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                throw new WebChatJsonlImportException(
                    "empty_line",
                    $"JSONL line {lineNumber} is empty.",
                    lineNumber);
            }

            var type = ReadType(line, lineNumber);
            if (lineNumber == 1)
            {
                if (!string.Equals(type, "session", StringComparison.Ordinal))
                {
                    throw new WebChatJsonlImportException(
                        "missing_session_header",
                        "First JSONL line must be a session header.",
                        lineNumber);
                }

                header = Deserialize(line, WebUiJsonlContext.Default.WebChatJsonlSessionHeader, lineNumber);
                ValidateHeader(header);
                continue;
            }

            if (!string.Equals(type, "message", StringComparison.Ordinal))
            {
                throw new WebChatJsonlImportException(
                    "invalid_entry_type",
                    $"JSONL line {lineNumber} must be a message entry.",
                    lineNumber);
            }

            var entry = Deserialize(line, WebUiJsonlContext.Default.WebChatJsonlMessageEntry, lineNumber);
            ValidateMessageEntry(entry, previousMessageId, seenMessageIds, lineNumber);
            messages.Add(new WebChatMessageDto(
                entry.Role,
                entry.Text ?? string.Empty,
                entry.Timestamp,
                entry.Thinking,
                entry.ToolEvents,
                entry.Error,
                entry.Attachments,
                entry.ToolCalls));
            previousMessageId = entry.Id;
        }

        if (header is null)
        {
            throw new WebChatJsonlImportException(
                "missing_session_header",
                "JSONL session header is required.");
        }

        return new WebChatSessionDto(
            header.Id,
            header.Title,
            header.Provider,
            header.Model,
            header.CreatedAt,
            header.UpdatedAt,
            false,
            messages);
    }

    private static string ReadType(string line, int lineNumber)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                throw new WebChatJsonlImportException(
                    "missing_type",
                    $"JSONL line {lineNumber} is missing a type field.",
                    lineNumber);
            }

            return typeElement.GetString()!;
        }
        catch (JsonException ex)
        {
            throw new WebChatJsonlImportException(
                "invalid_json",
                $"JSONL line {lineNumber} is not valid JSON.",
                lineNumber,
                ex);
        }
    }

    private static T Deserialize<T>(string line, JsonTypeInfo<T> jsonTypeInfo, int lineNumber)
    {
        try
        {
            return JsonSerializer.Deserialize(line, jsonTypeInfo) ??
                throw new WebChatJsonlImportException(
                    "invalid_webui_jsonl",
                    $"JSONL line {lineNumber} could not be deserialized.",
                    lineNumber);
        }
        catch (JsonException ex)
        {
            throw new WebChatJsonlImportException(
                "invalid_webui_jsonl",
                $"JSONL line {lineNumber} is not valid WebUi JSONL.",
                lineNumber,
                ex);
        }
    }

    private static void ValidateHeader(WebChatJsonlSessionHeader header)
    {
        if (header.Version != 1)
        {
            throw new WebChatJsonlImportException(
                "unsupported_version",
                $"Unsupported WebUi JSONL version '{header.Version}'.",
                1);
        }

        RequireText(header.Id, "session id", 1);
        RequireText(header.Title, "session title", 1);
        RequireText(header.Provider, "session provider", 1);
        RequireText(header.Model, "session model", 1);
    }

    private static void ValidateMessageEntry(
        WebChatJsonlMessageEntry entry,
        string? previousMessageId,
        HashSet<string> seenMessageIds,
        int lineNumber)
    {
        RequireText(entry.Id, $"message id at line {lineNumber}", lineNumber);
        RequireText(entry.Role, $"message role at line {lineNumber}", lineNumber);

        if (!seenMessageIds.Add(entry.Id))
        {
            throw new WebChatJsonlImportException(
                "duplicate_message_id",
                $"JSONL line {lineNumber} has duplicate message id '{entry.Id}'.",
                lineNumber);
        }

        if (previousMessageId is null)
        {
            if (!string.IsNullOrWhiteSpace(entry.ParentId))
            {
                throw new WebChatJsonlImportException(
                    "invalid_parent_chain",
                    $"JSONL line {lineNumber} must not have a parentId for the first message.",
                    lineNumber);
            }

            return;
        }

        if (!string.Equals(entry.ParentId, previousMessageId, StringComparison.Ordinal))
        {
            throw new WebChatJsonlImportException(
                "invalid_parent_chain",
                $"JSONL line {lineNumber} does not continue the linear parent chain.",
                lineNumber);
        }
    }

    private static void RequireText(string? value, string fieldName, int? lineNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WebChatJsonlImportException(
                "missing_field",
                $"WebUi JSONL is missing {fieldName}.",
                lineNumber);
        }
    }
}

public sealed class WebChatJsonlImportException : Exception
{
    public WebChatJsonlImportException(
        string code,
        string message,
        int? lineNumber = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        LineNumber = lineNumber;
    }

    public string Code { get; }

    public int? LineNumber { get; }
}
