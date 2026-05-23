using System.Text.Json;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public static class CodingAgentJsonlSessionPreviewer
{
    private const int PreviewTextLimit = 160;

    public static CodingAgentJsonlSessionPreviewDto ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("CodingAgent JSONL path is required.", nameof(path));
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static CodingAgentJsonlSessionPreviewDto Parse(string jsonl, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(jsonl))
        {
            throw new CodingAgentJsonlPreviewException(
                "missing_session_header",
                "CodingAgent JSONL session header is required.");
        }

        using var reader = new StringReader(jsonl);
        CodingAgentJsonlSessionHeader? header = null;
        var messages = new List<CodingAgentJsonlTimelineMessageDto>();
        var seenEntryIds = new HashSet<string>(StringComparer.Ordinal);
        var entryCount = 0;
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
                throw new CodingAgentJsonlPreviewException(
                    "empty_line",
                    $"CodingAgent JSONL line {lineNumber} is empty.",
                    lineNumber);
            }

            var type = ReadType(line, lineNumber);
            if (lineNumber == 1)
            {
                if (!string.Equals(type, "session", StringComparison.Ordinal))
                {
                    throw new CodingAgentJsonlPreviewException(
                        "missing_session_header",
                        "First CodingAgent JSONL line must be a session header.",
                        lineNumber);
                }

                header = Deserialize(line, WebUiCodingAgentJsonlContext.Default.CodingAgentJsonlSessionHeader, lineNumber);
                ValidateHeader(header, lineNumber);
                continue;
            }

            var entry = Deserialize(line, WebUiCodingAgentJsonlContext.Default.CodingAgentJsonlSessionEntry, lineNumber);
            ValidateEntry(entry, seenEntryIds, lineNumber);
            entryCount++;

            if (string.Equals(entry.Type, "message", StringComparison.Ordinal))
            {
                if (entry.Message is null)
                {
                    throw new CodingAgentJsonlPreviewException(
                        "missing_message",
                        $"CodingAgent JSONL line {lineNumber} is a message entry without a message payload.",
                        lineNumber);
                }

                messages.Add(CreateMessagePreview(entry, lineNumber));
            }
        }

        if (header is null)
        {
            throw new CodingAgentJsonlPreviewException(
                "missing_session_header",
                "CodingAgent JSONL session header is required.");
        }

        return new CodingAgentJsonlSessionPreviewDto(
            header.Id,
            header.Version,
            header.Timestamp,
            header.Cwd,
            header.ParentSession,
            filePath,
            entryCount,
            messages.Count,
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
                throw new CodingAgentJsonlPreviewException(
                    "missing_type",
                    $"CodingAgent JSONL line {lineNumber} is missing a type field.",
                    lineNumber);
            }

            return typeElement.GetString()!;
        }
        catch (JsonException ex)
        {
            throw new CodingAgentJsonlPreviewException(
                "invalid_json",
                $"CodingAgent JSONL line {lineNumber} is not valid JSON.",
                lineNumber,
                ex);
        }
    }

    private static T Deserialize<T>(string line, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo, int lineNumber)
    {
        try
        {
            return JsonSerializer.Deserialize(line, jsonTypeInfo) ??
                throw new CodingAgentJsonlPreviewException(
                    "invalid_coding_agent_jsonl",
                    $"CodingAgent JSONL line {lineNumber} could not be deserialized.",
                    lineNumber);
        }
        catch (JsonException ex)
        {
            throw new CodingAgentJsonlPreviewException(
                "invalid_coding_agent_jsonl",
                $"CodingAgent JSONL line {lineNumber} is not valid CodingAgent JSONL.",
                lineNumber,
                ex);
        }
    }

    private static void ValidateHeader(CodingAgentJsonlSessionHeader header, int lineNumber)
    {
        if (!string.Equals(header.Type, "session", StringComparison.Ordinal))
        {
            throw new CodingAgentJsonlPreviewException(
                "invalid_session_header",
                "CodingAgent JSONL header type must be 'session'.",
                lineNumber);
        }

        if (header.Version <= 0)
        {
            throw new CodingAgentJsonlPreviewException(
                "unsupported_version",
                $"Unsupported CodingAgent JSONL version '{header.Version}'.",
                lineNumber);
        }

        RequireText(header.Id, "session id", lineNumber);
        RequireText(header.Cwd, "session cwd", lineNumber);
    }

    private static void ValidateEntry(
        CodingAgentJsonlSessionEntry entry,
        HashSet<string> seenEntryIds,
        int lineNumber)
    {
        RequireText(entry.Type, $"entry type at line {lineNumber}", lineNumber);
        RequireText(entry.Id, $"entry id at line {lineNumber}", lineNumber);

        if (!seenEntryIds.Add(entry.Id))
        {
            throw new CodingAgentJsonlPreviewException(
                "duplicate_entry_id",
                $"CodingAgent JSONL line {lineNumber} has duplicate entry id '{entry.Id}'.",
                lineNumber);
        }
    }

    private static CodingAgentJsonlTimelineMessageDto CreateMessagePreview(
        CodingAgentJsonlSessionEntry entry,
        int lineNumber)
    {
        var message = entry.Message!;
        RequireText(message.Role, $"message role at line {lineNumber}", lineNumber);

        IReadOnlyList<CodingAgentJsonlSessionContent> content = message.Content ?? [];
        var text = BuildText(content);
        return new CodingAgentJsonlTimelineMessageDto(
            entry.Id,
            entry.ParentId,
            entry.Timestamp,
            message.Role,
            PreviewText(text),
            text.Length,
            content.Count,
            content.Any(static item => string.Equals(item.Type, "thinking", StringComparison.Ordinal)),
            content.Count(static item => string.Equals(item.Type, "toolCall", StringComparison.Ordinal)),
            content.Count(static item => string.Equals(item.Type, "image", StringComparison.Ordinal)),
            message.ToolCallId,
            string.Equals(message.Role, "toolResult", StringComparison.Ordinal) ? message.IsError : null);
    }

    private static string BuildText(IReadOnlyList<CodingAgentJsonlSessionContent> content)
    {
        var parts = content
            .Where(static item => string.Equals(item.Type, "text", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Text))
            .Select(static item => item.Text!.Trim())
            .ToArray();
        return parts.Length == 0 ? string.Empty : string.Join("\n\n", parts);
    }

    private static string PreviewText(string text)
    {
        if (text.Length <= PreviewTextLimit)
        {
            return text;
        }

        return text[..PreviewTextLimit] + "...";
    }

    private static void RequireText(string? value, string fieldName, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CodingAgentJsonlPreviewException(
                "missing_field",
                $"CodingAgent JSONL is missing {fieldName}.",
                lineNumber);
        }
    }
}

public sealed class CodingAgentJsonlPreviewException : Exception
{
    public CodingAgentJsonlPreviewException(
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

internal sealed class CodingAgentJsonlSessionHeader
{
    public string Type { get; init; } = string.Empty;
    public int Version { get; init; }
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string Cwd { get; init; } = string.Empty;
    public string? ParentSession { get; init; }
}

internal sealed class CodingAgentJsonlSessionEntry
{
    public string Type { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public CodingAgentJsonlSessionMessage? Message { get; init; }
}

internal sealed class CodingAgentJsonlSessionMessage
{
    public string Role { get; init; } = string.Empty;
    public string? ToolCallId { get; init; }
    public bool IsError { get; init; }
    public List<CodingAgentJsonlSessionContent>? Content { get; init; } = [];
}

internal sealed class CodingAgentJsonlSessionContent
{
    public string Type { get; init; } = string.Empty;
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public string? Data { get; init; }
    public string? MimeType { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Arguments { get; init; }
}
