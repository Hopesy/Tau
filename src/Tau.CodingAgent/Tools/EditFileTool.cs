using System.Text.Json;
using Tau.AgentCore;
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
                "new_string": { "type": "string", "description": "Replacement string" },
                "edits": {
                    "description": "One replacement object or an array of replacements",
                    "oneOf": [
                        { "type": "object", "properties": { "oldText": { "type": "string" }, "newText": { "type": "string" } }, "required": ["oldText", "newText"] },
                        { "type": "array", "items": { "type": "object", "properties": { "oldText": { "type": "string" }, "newText": { "type": "string" } }, "required": ["oldText", "newText"] } }
                    ]
                }
            },
            "required": ["path"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.GetProperty("path").GetString()!;
        var replacements = ReadReplacements(args);
        if (replacements.Count == 0)
            return new ToolResult([new TextContent("Edit input must provide old_string/new_string or edits.")], IsError: true);

        if (!File.Exists(path))
            return new ToolResult([new TextContent($"File not found: {path}")], IsError: true);

        var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var matches = new List<(int Index, int Length, string NewText)>();
        for (var index = 0; index < replacements.Count; index++)
        {
            var (oldText, newText) = replacements[index];
            var occurrence = FindUniqueOccurrence(content, oldText);
            if (occurrence is null)
                return new ToolResult([new TextContent($"edit {index + 1} old text was not found uniquely in the file.")], IsError: true);
            matches.Add((occurrence.Value.Index, oldText.Length, newText));
        }

        foreach (var pair in matches.OrderBy(static item => item.Index).Zip(matches.OrderBy(static item => item.Index).Skip(1)))
        {
            if (pair.First.Index + pair.First.Length > pair.Second.Index)
                return new ToolResult([new TextContent("Edits overlap; merge overlapping replacements into one edit.")], IsError: true);
        }

        var updated = content;
        foreach (var match in matches.OrderByDescending(static item => item.Index))
            updated = updated.Remove(match.Index, match.Length).Insert(match.Index, match.NewText);
        await File.WriteAllTextAsync(path, updated, ct).ConfigureAwait(false);

        return new ToolResult([new TextContent($"Successfully edited {path} ({matches.Count} replacement{(matches.Count == 1 ? string.Empty : "s")})")]);
    }

    /// <summary>读取兼容的新旧 edit 参数并统一为替换列表。</summary>
    /// <param name="args">工具调用参数。</param>
    /// <returns>按调用顺序排列的旧文本与新文本。</returns>
    private static IReadOnlyList<(string OldText, string NewText)> ReadReplacements(JsonElement args)
    {
        var replacements = new List<(string, string)>();
        if (args.TryGetProperty("old_string", out var oldValue) &&
            args.TryGetProperty("new_string", out var newValue) &&
            oldValue.ValueKind == JsonValueKind.String && newValue.ValueKind == JsonValueKind.String)
        {
            replacements.Add((oldValue.GetString() ?? string.Empty, newValue.GetString() ?? string.Empty));
        }

        if (!args.TryGetProperty("edits", out var edits))
            return replacements;

        if (edits.ValueKind == JsonValueKind.Object)
        {
            if (TryReadReplacement(edits, out var single)) replacements.Add(single);
        }
        else if (edits.ValueKind == JsonValueKind.Array)
        {
            foreach (var edit in edits.EnumerateArray())
            {
                if (TryReadReplacement(edit, out var replacement)) replacements.Add(replacement);
            }
        }

        return replacements;
    }

    /// <summary>读取一个编辑对象，兼容 oldText/newText 与 old_string/new_string 字段。</summary>
    /// <param name="edit">编辑对象。</param>
    /// <param name="replacement">解析出的替换内容。</param>
    /// <returns>字段完整时返回 true。</returns>
    private static bool TryReadReplacement(JsonElement edit, out (string OldText, string NewText) replacement)
    {
        replacement = default;
        if (edit.ValueKind != JsonValueKind.Object)
            return false;
        var oldProperty = edit.TryGetProperty("oldText", out var oldText) ? oldText : edit.TryGetProperty("old_string", out var legacyOld) ? legacyOld : default;
        var newProperty = edit.TryGetProperty("newText", out var newText) ? newText : edit.TryGetProperty("new_string", out var legacyNew) ? legacyNew : default;
        if (oldProperty.ValueKind != JsonValueKind.String || newProperty.ValueKind != JsonValueKind.String)
            return false;
        replacement = (oldProperty.GetString() ?? string.Empty, newProperty.GetString() ?? string.Empty);
        return true;
    }

    /// <summary>查找唯一的非空旧文本匹配位置。</summary>
    /// <param name="content">原始文件内容。</param>
    /// <param name="oldText">待查找文本。</param>
    /// <returns>唯一位置；未找到或重复时返回 null。</returns>
    private static (int Index, int Length)? FindUniqueOccurrence(string content, string oldText)
    {
        if (string.IsNullOrEmpty(oldText)) return null;
        var first = content.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0 || content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0) return null;
        return (first, oldText.Length);
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
