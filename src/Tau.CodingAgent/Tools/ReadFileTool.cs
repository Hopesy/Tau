using System.Text.Json;
using Tau.Agent;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class ReadFileTool : IAgentTool
{
    public string Name => "read_file";
    public string Label => "Read File";
    public string Description => "Read the contents of a file at the given path.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "Absolute or relative file path to read" },
                "offset": { "type": "integer", "description": "Line number to start reading from (0-based)" },
                "limit": { "type": "integer", "description": "Maximum number of lines to read" }
            },
            "required": ["path"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.GetProperty("path").GetString()!;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : (int?)null;

        if (!File.Exists(path))
            return new ToolResult([new TextContent($"File not found: {path}")], IsError: true);

        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        var selected = lines.Skip(offset);
        if (limit.HasValue) selected = selected.Take(limit.Value);

        var numbered = selected.Select((line, i) => $"{offset + i + 1}\t{line}");
        var content = string.Join("\n", numbered);

        return new ToolResult([new TextContent(content)]);
    }
}
