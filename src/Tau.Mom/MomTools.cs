using System.Text.Json;
using Tau.Agent;
using Tau.Ai;

namespace Tau.Mom;

public static class MomToolSet
{
    public static IAgentTool[] Create(
        IMomSandboxExecutor executor,
        Action<string, string?>? attachFile = null)
    {
        return
        [
            new MomReadTool(executor),
            new MomBashTool(executor),
            new MomEditTool(executor),
            new MomWriteTool(executor),
            new MomAttachTool(executor, attachFile)
        ];
    }
}

public sealed class MomBashTool : IAgentTool
{
    private readonly IMomSandboxExecutor _executor;

    public MomBashTool(IMomSandboxExecutor executor)
    {
        _executor = executor;
    }

    public string Name => "bash";
    public string Label => "bash";
    public string Description => $"Execute a shell command in the current Mom sandbox. Output is truncated to the last {MomToolOutputTruncator.DefaultMaxLines} lines or {MomToolOutputTruncator.DefaultMaxBytes / 1024}KB.";
    public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "label": { "type": "string", "description": "Brief description of what this command does" },
                "command": { "type": "string", "description": "Command to execute" },
                "timeout": { "type": "integer", "description": "Timeout in seconds" }
            },
            "required": ["label", "command"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        Func<ToolUpdate, Task>? onUpdate)
    {
        var command = args.GetProperty("command").GetString()!;
        var timeout = args.TryGetProperty("timeout", out var timeoutElement) ? timeoutElement.GetInt32() : (int?)null;
        var result = await _executor.ExecAsync(command, new MomSandboxExecOptions(timeout), ct).ConfigureAwait(false);

        var output = string.Empty;
        if (!string.IsNullOrEmpty(result.Stdout))
        {
            output += result.Stdout;
        }

        if (!string.IsNullOrEmpty(result.Stderr))
        {
            if (!string.IsNullOrEmpty(output))
            {
                output += "\n";
            }

            output += result.Stderr;
        }

        var truncation = MomToolOutputTruncator.TruncateTail(output);
        var text = string.IsNullOrEmpty(truncation.Content) ? "(no output)" : truncation.Content;
        if (truncation.Truncated)
        {
            var startLine = Math.Max(1, truncation.TotalLines - truncation.OutputLines + 1);
            var endLine = truncation.TotalLines;
            text += truncation.LastLinePartial
                ? $"\n\n[Showing last {MomToolOutputTruncator.FormatSize(truncation.OutputBytes)} of line {endLine}.]"
                : $"\n\n[Showing lines {startLine}-{endLine} of {truncation.TotalLines}.]";
        }

        if (result.ExitCode != 0)
        {
            text = $"{text}\n\nCommand exited with code {result.ExitCode}".Trim();
        }

        return new ToolResult(
            [new TextContent(text)],
            IsError: result.ExitCode != 0 || result.TimedOut,
            Details: truncation.Truncated ? truncation : null);
    }
}

public sealed class MomReadTool : IAgentTool
{
    private static readonly IReadOnlyDictionary<string, string> ImageMimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp"
        };

    private readonly IMomSandboxExecutor _executor;

    public MomReadTool(IMomSandboxExecutor executor)
    {
        _executor = executor;
    }

    public string Name => "read";
    public string Label => "read";
    public string Description => $"Read a text or image file from the Mom sandbox. Text output is truncated to {MomToolOutputTruncator.DefaultMaxLines} lines or {MomToolOutputTruncator.DefaultMaxBytes / 1024}KB.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "label": { "type": "string", "description": "Brief description of what is being read" },
                "path": { "type": "string", "description": "Path to read, relative to the sandbox workspace or absolute" },
                "offset": { "type": "integer", "description": "1-based line number to start reading from" },
                "limit": { "type": "integer", "description": "Maximum number of lines to read" }
            },
            "required": ["label", "path"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.GetProperty("path").GetString()!;
        var hostPath = _executor.ToHostPath(path);
        if (!_executor.IsHostPathInWorkspace(hostPath))
        {
            return new ToolResult([new TextContent($"Path is outside the Mom workspace: {path}")], IsError: true);
        }

        if (!File.Exists(hostPath))
        {
            return new ToolResult([new TextContent($"File not found: {path}")], IsError: true);
        }

        var extension = Path.GetExtension(hostPath);
        if (ImageMimeTypes.TryGetValue(extension, out var mimeType))
        {
            var bytes = await File.ReadAllBytesAsync(hostPath, ct).ConfigureAwait(false);
            return new ToolResult(
                [
                    new TextContent($"Read image file [{mimeType}]"),
                    new ImageContent(Convert.ToBase64String(bytes), mimeType)
                ]);
        }

        var content = await File.ReadAllTextAsync(hostPath, ct).ConfigureAwait(false);
        var offset = args.TryGetProperty("offset", out var offsetElement) ? Math.Max(1, offsetElement.GetInt32()) : 1;
        var limit = args.TryGetProperty("limit", out var limitElement) ? Math.Max(0, limitElement.GetInt32()) : (int?)null;
        var lines = content.Split('\n');
        if (offset > lines.Length)
        {
            return new ToolResult([new TextContent($"Offset {offset} is beyond end of file ({lines.Length} lines total).")], IsError: true);
        }

        var selected = lines.Skip(offset - 1);
        if (limit.HasValue)
        {
            selected = selected.Take(limit.Value);
        }

        var selectedContent = string.Join("\n", selected);
        var truncation = MomToolOutputTruncator.TruncateHead(selectedContent);
        var text = truncation.Content;
        if (truncation.FirstLineExceedsLimit)
        {
            text = $"[Line {offset} exceeds {MomToolOutputTruncator.FormatSize(MomToolOutputTruncator.DefaultMaxBytes)}. Use bash with a byte-limited command.]";
        }
        else if (truncation.Truncated)
        {
            var endLine = offset + truncation.OutputLines - 1;
            var nextOffset = endLine + 1;
            text += $"\n\n[Showing lines {offset}-{endLine} of {lines.Length}. Use offset={nextOffset} to continue.]";
        }
        else if (limit.HasValue && offset - 1 + limit.Value < lines.Length)
        {
            text += $"\n\n[{lines.Length - (offset - 1 + limit.Value)} more lines in file. Use offset={offset + limit.Value} to continue.]";
        }

        return new ToolResult(
            [new TextContent(text)],
            Details: truncation.Truncated ? truncation : null);
    }
}

public sealed class MomWriteTool : IAgentTool
{
    private readonly IMomSandboxExecutor _executor;

    public MomWriteTool(IMomSandboxExecutor executor)
    {
        _executor = executor;
    }

    public string Name => "write";
    public string Label => "write";
    public string Description => "Write content to a file in the Mom sandbox. Creates parent directories and overwrites existing files.";
    public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "label": { "type": "string", "description": "Brief description of what is being written" },
                "path": { "type": "string", "description": "Path to write, relative to the sandbox workspace or absolute" },
                "content": { "type": "string", "description": "Content to write" }
            },
            "required": ["label", "path", "content"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.GetProperty("path").GetString()!;
        var content = args.GetProperty("content").GetString()!;
        var hostPath = _executor.ToHostPath(path);
        if (!_executor.IsHostPathInWorkspace(hostPath))
        {
            return new ToolResult([new TextContent($"Path is outside the Mom workspace: {path}")], IsError: true);
        }

        var directory = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(hostPath, content, ct).ConfigureAwait(false);
        return new ToolResult([new TextContent($"Successfully wrote {content.Length} bytes to {_executor.ToWorkspacePath(hostPath)}")]);
    }
}

public sealed class MomEditTool : IAgentTool
{
    private readonly IMomSandboxExecutor _executor;

    public MomEditTool(IMomSandboxExecutor executor)
    {
        _executor = executor;
    }

    public string Name => "edit";
    public string Label => "edit";
    public string Description => "Edit a file in the Mom sandbox by replacing exact text. oldText must be unique.";
    public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "label": { "type": "string", "description": "Brief description of the edit" },
                "path": { "type": "string", "description": "Path to edit, relative to the sandbox workspace or absolute" },
                "oldText": { "type": "string", "description": "Exact text to find" },
                "newText": { "type": "string", "description": "Replacement text" }
            },
            "required": ["label", "path", "oldText", "newText"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.GetProperty("path").GetString()!;
        var oldText = args.GetProperty("oldText").GetString()!;
        var newText = args.GetProperty("newText").GetString()!;
        var hostPath = _executor.ToHostPath(path);
        if (!_executor.IsHostPathInWorkspace(hostPath))
        {
            return new ToolResult([new TextContent($"Path is outside the Mom workspace: {path}")], IsError: true);
        }

        if (!File.Exists(hostPath))
        {
            return new ToolResult([new TextContent($"File not found: {path}")], IsError: true);
        }

        var content = await File.ReadAllTextAsync(hostPath, ct).ConfigureAwait(false);
        var occurrences = CountOccurrences(content, oldText);
        if (occurrences == 0)
        {
            return new ToolResult([new TextContent($"Could not find the exact text in {path}.")], IsError: true);
        }

        if (occurrences > 1)
        {
            return new ToolResult([new TextContent($"Found {occurrences} occurrences in {path}. Provide more context to make oldText unique.")], IsError: true);
        }

        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        var updated = content[..index] + newText + content[(index + oldText.Length)..];
        if (content == updated)
        {
            return new ToolResult([new TextContent($"No changes made to {path}.")], IsError: true);
        }

        await File.WriteAllTextAsync(hostPath, updated, ct).ConfigureAwait(false);
        return new ToolResult([new TextContent($"Successfully replaced text in {_executor.ToWorkspacePath(hostPath)}.")]);
    }

    private static int CountOccurrences(string text, string search)
    {
        if (search.Length == 0)
        {
            return 0;
        }

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

public sealed class MomAttachTool : IAgentTool
{
    private readonly IMomSandboxExecutor _executor;
    private readonly Action<string, string?>? _attachFile;

    public MomAttachTool(IMomSandboxExecutor executor, Action<string, string?>? attachFile = null)
    {
        _executor = executor;
        _attachFile = attachFile;
    }

    public string Name => "attach";
    public string Label => "attach";
    public string Description => "Attach a file from the Mom workspace to the delegation result or Slack response.";
    public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "label": { "type": "string", "description": "Brief description of what is being shared" },
                "path": { "type": "string", "description": "Path to the file to attach" },
                "title": { "type": "string", "description": "Optional attachment title" }
            },
            "required": ["label", "path"]
        }
        """).RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        Func<ToolUpdate, Task>? onUpdate)
    {
        var path = args.GetProperty("path").GetString()!;
        var title = args.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var hostPath = _executor.ToHostPath(path);
        if (!_executor.IsHostPathInWorkspace(hostPath))
        {
            return Task.FromResult(new ToolResult([new TextContent($"Path is outside the Mom workspace: {path}")], IsError: true));
        }

        if (!File.Exists(hostPath))
        {
            return Task.FromResult(new ToolResult([new TextContent($"File not found: {path}")], IsError: true));
        }

        _attachFile?.Invoke(hostPath, string.IsNullOrWhiteSpace(title) ? Path.GetFileName(hostPath) : title.Trim());
        return Task.FromResult(new ToolResult([new TextContent($"Attached file: {Path.GetFileName(hostPath)}")]));
    }
}
