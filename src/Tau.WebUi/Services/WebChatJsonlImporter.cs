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
            throw new InvalidDataException("JSONL session header is required.");
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
                throw new InvalidDataException($"JSONL line {lineNumber} is empty.");
            }

            var type = ReadType(line, lineNumber);
            if (lineNumber == 1)
            {
                if (!string.Equals(type, "session", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("First JSONL line must be a session header.");
                }

                header = Deserialize(line, WebUiJsonlContext.Default.WebChatJsonlSessionHeader, lineNumber);
                ValidateHeader(header);
                continue;
            }

            if (!string.Equals(type, "message", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"JSONL line {lineNumber} must be a message entry.");
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
            throw new InvalidDataException("JSONL session header is required.");
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
                throw new InvalidDataException($"JSONL line {lineNumber} is missing a type field.");
            }

            return typeElement.GetString()!;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"JSONL line {lineNumber} is not valid JSON.", ex);
        }
    }

    private static T Deserialize<T>(string line, JsonTypeInfo<T> jsonTypeInfo, int lineNumber)
    {
        try
        {
            return JsonSerializer.Deserialize(line, jsonTypeInfo) ??
                throw new InvalidDataException($"JSONL line {lineNumber} could not be deserialized.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"JSONL line {lineNumber} is not valid WebUi JSONL.", ex);
        }
    }

    private static void ValidateHeader(WebChatJsonlSessionHeader header)
    {
        if (header.Version != 1)
        {
            throw new InvalidDataException($"Unsupported WebUi JSONL version '{header.Version}'.");
        }

        RequireText(header.Id, "session id");
        RequireText(header.Title, "session title");
        RequireText(header.Provider, "session provider");
        RequireText(header.Model, "session model");
    }

    private static void ValidateMessageEntry(
        WebChatJsonlMessageEntry entry,
        string? previousMessageId,
        HashSet<string> seenMessageIds,
        int lineNumber)
    {
        RequireText(entry.Id, $"message id at line {lineNumber}");
        RequireText(entry.Role, $"message role at line {lineNumber}");

        if (!seenMessageIds.Add(entry.Id))
        {
            throw new InvalidDataException($"JSONL line {lineNumber} has duplicate message id '{entry.Id}'.");
        }

        if (previousMessageId is null)
        {
            if (!string.IsNullOrWhiteSpace(entry.ParentId))
            {
                throw new InvalidDataException($"JSONL line {lineNumber} must not have a parentId for the first message.");
            }

            return;
        }

        if (!string.Equals(entry.ParentId, previousMessageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"JSONL line {lineNumber} does not continue the linear parent chain.");
        }
    }

    private static void RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"WebUi JSONL is missing {fieldName}.");
        }
    }
}
