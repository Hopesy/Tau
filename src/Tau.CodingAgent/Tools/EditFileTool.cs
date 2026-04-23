using System.Text.Json;
using Tau.Agent;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class EditFileTool : IAgentTool
{
    public string Name => "edit_file";
    public string Label => "Edit File";
    public string Description => "Replace an exact string in a file with new content.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "Path to the file to edit" },
                "old_string": { "type": "string", "description": "Exact string to find and replace" },
                "new_string": { "type": "string", "description": "Replacement string" }
            },
            "required": ["path", "old_string", "new_string"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.GetProperty("path").GetString()!;
        var oldString = args.GetProperty("old_string").GetString()!;
        var newString = args.GetProperty("new_string").GetString()!;

        if (!File.Exists(path))
            return new ToolResult([new TextContent($"File not found: {path}")], IsError: true);

        var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var occurrences = CountOccurrences(content, oldString);

        if (occurrences == 0)
            return new ToolResult([new TextContent("old_string not found in the file.")], IsError: true);

        if (occurrences > 1)
            return new ToolResult([new TextContent($"old_string found {occurrences} times. Provide more context to make it unique.")], IsError: true);

        var updated = content.Replace(oldString, newString);
        await File.WriteAllTextAsync(path, updated, ct).ConfigureAwait(false);

        return new ToolResult([new TextContent($"Successfully edited {path}")]);
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
