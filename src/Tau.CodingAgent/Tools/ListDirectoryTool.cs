using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class ListDirectoryTool : IAgentTool
{
    private const int DefaultLimit = 500;

    public string Name => "ls";
    public string Label => "List Directory";
    public string Description => "List directory contents. Returns entries sorted alphabetically, with '/' suffix for directories. Includes dotfiles. Output is truncated to 500 entries or 50KB.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "Directory to list (default: current directory)" },
                "limit": { "type": "number", "description": "Maximum number of entries to return (default: 500)" }
            }
        }
        """).RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : ".";
        path ??= ".";
        var limit = args.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind == JsonValueKind.Number
            ? Math.Max(0, limitElement.GetInt32())
            : DefaultLimit;

        if (!Directory.Exists(path))
        {
            if (File.Exists(path))
                return Task.FromResult(new ToolResult([new TextContent($"Not a directory: {path}")], IsError: true));

            return Task.FromResult(new ToolResult([new TextContent($"Path not found: {path}")], IsError: true));
        }

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(path)
                .Select(static entry => new DirectoryEntry(
                    Path.GetFileName(entry),
                    Directory.Exists(entry)))
                .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (entries.Length == 0)
                return Task.FromResult(new ToolResult([new TextContent("(empty directory)")]));

            var visibleEntries = entries
                .Take(limit)
                .Select(static entry => entry.IsDirectory ? $"{entry.Name}/" : entry.Name)
                .ToList();

            var truncation = ToolOutputTruncator.TruncateHead(
                string.Join("\n", visibleEntries),
                maxLines: int.MaxValue);
            var result = truncation.Content;
            var details = new ListDirectoryToolDetails();
            var notices = new List<string>();
            if (entries.Length > visibleEntries.Count)
            {
                notices.Add($"{limit} entries limit reached. Use limit={limit * 2} for more");
                details = details with { EntryLimitReached = limit };
            }

            if (truncation.Truncated)
            {
                notices.Add($"{ToolOutputTruncator.FormatSize(ToolOutputTruncator.DefaultMaxBytes)} limit reached");
                details = details with { Truncation = truncation };
            }

            if (notices.Count > 0)
                result += $"\n\n[{string.Join(". ", notices)}]";

            var resultDetails = details.Truncation is null && details.EntryLimitReached is null
                ? null
                : details;
            return Task.FromResult(new ToolResult([new TextContent(result)], Details: resultDetails));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new ToolResult([new TextContent($"Cannot read directory: {ex.Message}")], IsError: true));
        }
    }

    private sealed record DirectoryEntry(string Name, bool IsDirectory);
}

public sealed record ListDirectoryToolDetails(
    ToolOutputTruncationResult? Truncation = null,
    int? EntryLimitReached = null);
