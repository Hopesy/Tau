using System.Text.Json;
using Tau.Agent;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class ListDirectoryTool : IAgentTool
{
    public string Name => "ls";
    public string Label => "List Directory";
    public string Description => "List files and directories at the given path.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "Directory path to list (default: current directory)" }
            }
        }
        """).RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : ".";
        path ??= ".";

        if (!Directory.Exists(path))
            return Task.FromResult(new ToolResult([new TextContent($"Directory not found: {path}")], IsError: true));

        var entries = new List<string>();

        foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d).Take(200))
            entries.Add($"[DIR]  {Path.GetFileName(dir)}/");

        foreach (var file in Directory.EnumerateFiles(path).OrderBy(f => f).Take(300))
        {
            var info = new FileInfo(file);
            entries.Add($"       {Path.GetFileName(file)} ({FormatSize(info.Length)})");
        }

        var result = entries.Count == 0
            ? "Directory is empty."
            : string.Join("\n", entries);

        return Task.FromResult(new ToolResult([new TextContent(result)]));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };
}
