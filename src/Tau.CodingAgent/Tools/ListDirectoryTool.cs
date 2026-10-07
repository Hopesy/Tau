using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class ListDirectoryTool : IAgentTool
{
    private const int DefaultLimit = 500;
    private readonly string _workingDirectory;

    /// <summary>【CodingAgent】【工具目录】创建绑定会话目录的目录列表工具</summary>
    /// <param name="workingDirectory">会话目录；为空时捕获当前进程目录</param>
    public ListDirectoryTool(string? workingDirectory = null) =>
        _workingDirectory = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);

    public string Name => "ls";
    /// <summary>工具在系统提示中的一行简介。</summary>
    public string PromptSnippet => "List directory contents";
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

    /// <summary>【CodingAgent】【工具执行】在所属会话目录中执行工具请求</summary>
    /// <param name="toolCallId">工具调用标识</param>
    /// <param name="args">工具参数</param>
    /// <param name="ct">取消信号</param>
    /// <param name="onUpdate">增量结果回调</param>
    /// <returns>工具执行结果</returns>
    public Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = CodingAgentToolPaths.Resolve(args.TryGetProperty("path", out var p) ? p.GetString() : null, _workingDirectory);
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
