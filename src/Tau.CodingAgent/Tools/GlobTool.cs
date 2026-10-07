using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class GlobTool : IAgentTool
{
    private readonly string _workingDirectory;

    /// <summary>【CodingAgent】【工具目录】创建绑定会话目录的文件查找工具</summary>
    /// <param name="workingDirectory">会话目录；为空时捕获当前进程目录</param>
    public GlobTool(string? workingDirectory = null) =>
        _workingDirectory = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);

    public string Name => "find";
    /// <summary>工具在系统提示中的一行简介。</summary>
    public string PromptSnippet => "Find files by glob pattern";
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

    /// <summary>【CodingAgent】【工具执行】在所属会话目录中执行工具请求</summary>
    /// <param name="toolCallId">工具调用标识</param>
    /// <param name="args">工具参数</param>
    /// <param name="ct">取消信号</param>
    /// <param name="onUpdate">增量结果回调</param>
    /// <returns>工具执行结果</returns>
    public Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var basePath = CodingAgentToolPaths.Resolve(args.TryGetProperty("path", out var p) ? p.GetString() : null, _workingDirectory);

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
