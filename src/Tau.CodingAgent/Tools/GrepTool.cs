using System.Diagnostics;
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class GrepTool : IAgentTool
{
    private readonly string _workingDirectory;

    /// <summary>【CodingAgent】【工具目录】创建绑定会话目录的内容搜索工具</summary>
    /// <param name="workingDirectory">会话目录；为空时捕获当前进程目录</param>
    public GrepTool(string? workingDirectory = null) =>
        _workingDirectory = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);

    public string Name => "grep";
    /// <summary>工具在系统提示中的一行简介。</summary>
    public string PromptSnippet => "Search file contents by pattern";
    public string Label => "Grep";
    public string Description => "Search for a regex pattern in files. Returns matching file paths or content.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "pattern": { "type": "string", "description": "Regex pattern to search for" },
                "path": { "type": "string", "description": "File or directory to search in" },
                "glob": { "type": "string", "description": "Glob filter for files (e.g., *.cs)" },
                "include_content": { "type": "boolean", "description": "Show matching lines (default: false, shows file paths only)" }
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
    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var path = CodingAgentToolPaths.Resolve(args.TryGetProperty("path", out var p) ? p.GetString() : null, _workingDirectory);
        var glob = args.TryGetProperty("glob", out var g) ? g.GetString() : null;
        var includeContent = args.TryGetProperty("include_content", out var ic) && ic.GetBoolean();

        var psi = new ProcessStartInfo
        {
            FileName = "rg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory
        };
        // 1. 【CodingAgent】【工具目录】逐项传参，保留包含空格或引号的会话路径和搜索模式
        psi.ArgumentList.Add(includeContent ? "-n" : "-l");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(pattern);
        if (glob is not null)
        {
            psi.ArgumentList.Add("--glob");
            psi.ArgumentList.Add(glob);
        }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return FallbackGrep(pattern, path, includeContent, ct);

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(output))
                return new ToolResult([new TextContent("No matches found.")]);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 250)
            {
                output = string.Join("\n", lines.Take(250));
                output += $"\n... ({lines.Length - 250} more results)";
            }

            return new ToolResult([new TextContent(output)]);
        }
        catch
        {
            return FallbackGrep(pattern, path, includeContent, ct);
        }
    }

    private static ToolResult FallbackGrep(string pattern, string path, bool includeContent, CancellationToken ct)
    {
        try
        {
            var regex = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(5));

            var results = new List<string>();
            var searchPath = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? ".";
            var searchPattern = Directory.Exists(path) ? "*" : Path.GetFileName(path);

            foreach (var file in Directory.EnumerateFiles(searchPath, searchPattern,
                new EnumerationOptions { RecurseSubdirectories = true }))
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= 250) break;

                try
                {
                    var lines = File.ReadLines(file);
                    var lineNum = 0;
                    var matched = false;
                    foreach (var line in lines)
                    {
                        lineNum++;
                        if (regex.IsMatch(line))
                        {
                            if (includeContent)
                                results.Add($"{file}:{lineNum}:{line}");
                            else if (!matched)
                            {
                                results.Add(file);
                                matched = true;
                            }
                            if (!includeContent) break;
                        }
                    }
                }
                catch { /* skip binary/unreadable files */ }
            }

            return results.Count == 0
                ? new ToolResult([new TextContent("No matches found.")])
                : new ToolResult([new TextContent(string.Join("\n", results))]);
        }
        catch (Exception ex)
        {
            return new ToolResult([new TextContent($"Grep failed: {ex.Message}")], IsError: true);
        }
    }
}
