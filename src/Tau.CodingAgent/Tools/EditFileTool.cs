using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【文件编辑】按原始文件执行不相交的精确替换，并返回可审阅差异。</summary>
public sealed class EditFileTool : IAgentTool
{
    private readonly string _workingDirectory;
    private readonly ICodingAgentEditOperations _operations;

    /// <summary>【CodingAgent】【工具目录】创建绑定会话目录及可选远程后端的编辑工具。</summary>
    /// <param name="workingDirectory">会话目录。</param><param name="operations">可选编辑后端。</param>
    public EditFileTool(string? workingDirectory = null, ICodingAgentEditOperations? operations = null)
    {
        _workingDirectory = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);
        _operations = operations ?? new LocalCodingAgentFileOperations();
    }

    public string Name => "edit";
    public string Label => "edit";
    public string PromptSnippet => "Make precise file edits with exact text replacement, including multiple disjoint edits in one call";
    public IReadOnlyList<string> PromptGuidelines =>
    [
        "Use edit for precise changes (edits[].oldText must match exactly)",
        "When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
        "Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
        "Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions."
    ];
    public string Description => "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.";
    public ConstrainedSamplingConfig ConstrainedSampling => new() { Type = "json_schema", Strict = "prefer" };
    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {"type":"object","properties":{"path":{"type":"string","description":"Path to the file to edit (relative or absolute)"},"edits":{"type":"array","description":"One or more unique, non-overlapping replacements matched against the original file.","items":{"type":"object","properties":{"oldText":{"type":"string"},"newText":{"type":"string"}},"required":["oldText","newText"]}}},"required":["path","edits"]}
        """).RootElement.Clone();

    /// <summary>【CodingAgent】【参数兼容】将 JSON 字符串、单对象和旧顶层替换字段规范为原生 edits 数组。</summary>
    /// <param name="rawArgs">原始参数。</param><param name="ct">取消信号。</param><returns>独立规范参数，不修改原始调用。</returns>
    public ValueTask<JsonElement> PrepareArgumentsAsync(JsonElement rawArgs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (rawArgs.ValueKind != JsonValueKind.Object) return new(rawArgs);
        var args = JsonNode.Parse(rawArgs.GetRawText())!.AsObject();
        if (args["edits"] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                var parsed = JsonNode.Parse(text);
                if (parsed is JsonArray) args["edits"] = parsed;
                else if (IsEdit(parsed)) args["edits"] = new JsonArray(parsed);
            }
            catch (JsonException) { }
        }
        else if (IsEdit(args["edits"])) args["edits"] = new JsonArray(args["edits"]!.DeepClone());
        // 1. 【CodingAgent】【旧字段兼容】原生旧顶层字段与 Tau 旧 snake_case 字段都追加到规范数组
        foreach (var (oldName, newName) in new[] { ("oldText", "newText"), ("old_string", "new_string") })
        {
            if (args[oldName] is not JsonValue old || !old.TryGetValue<string>(out var oldText)
                || args[newName] is not JsonValue replacement || !replacement.TryGetValue<string>(out var newText)) continue;
            var edits = args["edits"] as JsonArray;
            if (edits is null) { edits = []; args["edits"] = edits; }
            edits.Add((JsonNode)new JsonObject { ["oldText"] = oldText, ["newText"] = newText });
            args.Remove(oldName); args.Remove(newName);
        }
        return new(JsonDocument.Parse(args.ToJsonString()).RootElement.Clone());
    }

    /// <summary>【CodingAgent】【替换形状】判断单对象是否包含两个字符串字段。</summary>
    /// <param name="node">待判断对象。</param><returns>是否为单个替换。</returns>
    private static bool IsEdit(JsonNode? node) => node is JsonObject edit
        && edit["oldText"] is JsonValue old && old.TryGetValue<string>(out _)
        && edit["newText"] is JsonValue replacement && replacement.TryGetValue<string>(out _);

    /// <summary>【CodingAgent】【文件编辑】持有文件修改队列直到所有 I/O 结束，保留 BOM 和原始换行风格。</summary>
    /// <param name="toolCallId">调用标识。</param><param name="args">路径和替换参数。</param><param name="ct">取消信号。</param>
    /// <param name="onUpdate">可选更新回调，本工具返回最终结果。</param><returns>替换结果与差异详情。</returns>
    public async Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
    {
        args = await PrepareArgumentsAsync(args, ct).ConfigureAwait(false);
        var displayPath = args.GetProperty("path").GetString()!;
        var path = CodingAgentToolPaths.Resolve(displayPath, _workingDirectory);
        if (!args.TryGetProperty("edits", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            return Error("Edit tool input is invalid. edits must contain at least one replacement.");
        var edits = entries.EnumerateArray().Select(edit => new CodingAgentEdit(edit.GetProperty("oldText").GetString()!, edit.GetProperty("newText").GetString()!)).ToArray();
        return await CodingAgentFileMutations.RunAsync(path, async () =>
        {
            ct.ThrowIfCancellationRequested();
            try { await _operations.AccessAsync(path).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ct.ThrowIfCancellationRequested();
                return Error($"Could not edit file: {displayPath}. {error.Message}.");
            }
            ct.ThrowIfCancellationRequested();
            var bytes = await _operations.ReadFileAsync(path).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var raw = Encoding.UTF8.GetString(bytes);
            var bom = raw.StartsWith('\uFEFF') ? "\uFEFF" : "";
            var content = bom.Length == 0 ? raw : raw[1..];
            var newline = content.IndexOf('\n');
            var crlf = newline > 0 && content[newline - 1] == '\r';
            var original = CodingAgentEditMatching.NormalizeLines(content);
            string updated;
            try { updated = CodingAgentEditMatching.Apply(original, edits, displayPath); }
            catch (InvalidOperationException error) { return Error(error.Message); }
            ct.ThrowIfCancellationRequested();
            // 2. 【CodingAgent】【完整写入】不把取消信号传给正在进行的文件写入，等待落盘后再检查取消并释放队列
            await _operations.WriteFileAsync(path, bom + (crlf ? updated.Replace("\n", "\r\n", StringComparison.Ordinal) : updated)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var details = CodingAgentEditDiff.Create(displayPath, original, updated);
            return new ToolResult([new TextContent($"Successfully replaced {edits.Length} block(s) in {displayPath}.")], Details: details);
        }).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【编辑错误】返回可让模型修正参数的错误结果。</summary>
    /// <param name="message">错误说明。</param><returns>工具错误结果。</returns>
    private static ToolResult Error(string message) => new([new TextContent(message)], IsError: true);
}
