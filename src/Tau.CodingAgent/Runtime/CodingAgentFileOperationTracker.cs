using System.Text;
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentFileOperationTracker
{
    public static IReadOnlyList<CodingAgentFileOperationSummary> Collect(IReadOnlyList<ChatMessage> messages)
    {
        var summaries = new Dictionary<string, FileOperationAccumulator>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var assistant in messages.OfType<AssistantMessage>())
        {
            foreach (var toolCall in assistant.Content.OfType<ToolCallContent>())
            {
                if (!TryGetFileOperation(toolCall.Name, out var operation) ||
                    !TryGetToolCallPath(toolCall.Arguments, out var path))
                {
                    continue;
                }

                if (!summaries.TryGetValue(path, out var summary))
                {
                    summary = new FileOperationAccumulator(path);
                    summaries.Add(path, summary);
                    order.Add(path);
                }

                summary.Add(operation);
            }
        }

        return order
            .Select(path =>
            {
                var summary = summaries[path];
                return new CodingAgentFileOperationSummary(
                    summary.Path,
                    summary.ReadCount,
                    summary.WriteCount,
                    summary.EditCount);
            })
            .ToArray();
    }

    public static string FormatCounts(CodingAgentFileOperationSummary file)
    {
        var parts = new List<string>();
        if (file.ReadCount > 0)
        {
            parts.Add($"read {file.ReadCount}");
        }

        if (file.WriteCount > 0)
        {
            parts.Add($"write {file.WriteCount}");
        }

        if (file.EditCount > 0)
        {
            parts.Add($"edit {file.EditCount}");
        }

        return string.Join(", ", parts);
    }

    public static string FormatForCompaction(IReadOnlyList<ChatMessage> messages)
    {
        var files = Collect(messages);
        if (files.Count == 0)
        {
            return string.Empty;
        }

        var modified = files
            .Where(static file => file.WasModified)
            .Select(static file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var readOnly = files
            .Where(static file => file.ReadCount > 0 && !file.WasModified)
            .Select(static file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        if (readOnly.Length > 0)
        {
            builder.AppendLine("<read-files>");
            foreach (var path in readOnly)
            {
                builder.AppendLine(path);
            }

            builder.AppendLine("</read-files>");
        }

        if (modified.Length > 0)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine("<modified-files>");
            foreach (var path in modified)
            {
                builder.AppendLine(path);
            }

            builder.AppendLine("</modified-files>");
        }

        return builder.ToString().Trim();
    }

    private static bool TryGetFileOperation(string toolName, out FileOperation operation)
    {
        operation = toolName switch
        {
            "read_file" or "read" => FileOperation.Read,
            "write_file" or "write" => FileOperation.Write,
            "edit_file" or "edit" => FileOperation.Edit,
            _ => (FileOperation)(-1)
        };
        return operation != (FileOperation)(-1);
    }

    private static bool TryGetToolCallPath(string arguments, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetStringProperty(document.RootElement, out var value, "path", "file_path"))
            {
                return false;
            }

            path = value.Trim();
            return path.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetStringProperty(JsonElement root, out string value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString() ?? string.Empty;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private enum FileOperation
    {
        Read,
        Write,
        Edit
    }

    private sealed class FileOperationAccumulator(string path)
    {
        public string Path { get; } = path;
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int EditCount { get; private set; }

        public void Add(FileOperation operation)
        {
            switch (operation)
            {
                case FileOperation.Read:
                    ReadCount++;
                    break;
                case FileOperation.Write:
                    WriteCount++;
                    break;
                case FileOperation.Edit:
                    EditCount++;
                    break;
            }
        }
    }
}

internal sealed record CodingAgentFileOperationSummary(
    string Path,
    int ReadCount,
    int WriteCount,
    int EditCount)
{
    public bool WasModified => WriteCount > 0 || EditCount > 0;
}
