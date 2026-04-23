using System.Text.Json;
using Tau.Agent;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class GlobTool : IAgentTool
{
    public string Name => "glob";
    public string Label => "Glob";
    public string Description => "Find files matching a glob pattern.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "pattern": { "type": "string", "description": "Glob pattern to match files (e.g., **/*.cs)" },
                "path": { "type": "string", "description": "Directory to search in (default: current directory)" }
            },
            "required": ["pattern"]
        }
        """).RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var basePath = args.TryGetProperty("path", out var p) ? p.GetString() : Directory.GetCurrentDirectory();
        basePath ??= Directory.GetCurrentDirectory();

        if (!Directory.Exists(basePath))
            return Task.FromResult(new ToolResult([new TextContent($"Directory not found: {basePath}")], IsError: true));

        var files = Directory.EnumerateFiles(basePath, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = pattern.Contains("**"),
            MatchCasing = MatchCasing.PlatformDefault
        })
        .OrderBy(f => File.GetLastWriteTimeUtc(f))
        .Take(500)
        .Select(f => Path.GetRelativePath(basePath, f))
        .ToList();

        var result = files.Count == 0
            ? "No files matched the pattern."
            : string.Join("\n", files);

        return Task.FromResult(new ToolResult([new TextContent(result)]));
    }
}
