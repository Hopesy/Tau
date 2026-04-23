using System.Text.Json;
using Tau.Agent;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class WriteFileTool : IAgentTool
{
    public string Name => "write_file";
    public string Label => "Write File";
    public string Description => "Write content to a file, creating it if it doesn't exist.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "Absolute or relative file path to write" },
                "content": { "type": "string", "description": "Content to write to the file" }
            },
            "required": ["path", "content"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.GetProperty("path").GetString()!;
        var content = args.GetProperty("content").GetString()!;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        return new ToolResult([new TextContent($"Successfully wrote {content.Length} characters to {path}")]);
    }
}
