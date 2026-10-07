using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class WriteFileTool : IAgentTool
{
    private readonly string _workingDirectory;
    private readonly ICodingAgentWriteOperations _operations;

    /// <summary>【CodingAgent】【工具目录】创建绑定会话目录的文件写入工具</summary>
    /// <param name="workingDirectory">会话目录；为空时捕获当前进程目录</param>
    /// <param name="operations">可选本地或远程写入后端。</param>
    public WriteFileTool(string? workingDirectory = null, ICodingAgentWriteOperations? operations = null)
    {
        _workingDirectory = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);
        _operations = operations ?? new LocalCodingAgentFileOperations();
    }

    public string Name => "write";
    /// <summary>工具在系统提示中的一行简介。</summary>
    public string PromptSnippet => "Create or overwrite files";
    /// <summary>启用工具时加入系统提示的使用规则。</summary>
    public IReadOnlyList<string> PromptGuidelines => ["Use write only for new files or complete rewrites."];
    public string Label => "write";
    public string Description => "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.";
    public ConstrainedSamplingConfig ConstrainedSampling => new() { Type = "json_schema", Strict = "prefer" };

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

    /// <summary>【CodingAgent】【工具执行】在所属会话目录中执行工具请求</summary>
    /// <param name="toolCallId">工具调用标识</param>
    /// <param name="args">工具参数</param>
    /// <param name="ct">取消信号</param>
    /// <param name="onUpdate">增量结果回调</param>
    /// <returns>工具执行结果</returns>
    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
    {
        var displayPath = args.GetProperty("path").GetString()!;
        var path = CodingAgentToolPaths.Resolve(displayPath, _workingDirectory);
        var content = args.GetProperty("content").GetString()!;

        return await CodingAgentFileMutations.RunAsync(path, async () =>
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) await _operations.CreateDirectoryAsync(dir).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // 1. 【CodingAgent】【完整写入】取消后仍等待已经开始的写入结束，防止提前释放同文件修改队列
            await _operations.WriteFileAsync(path, content).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ToolResult([new TextContent($"Successfully wrote to {displayPath}")]);
        }).ConfigureAwait(false);
    }
}
