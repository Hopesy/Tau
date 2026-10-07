using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【命令工具】执行 Bash 命令，提供有界模型输出和独立的程序输出。</summary>
public class ShellTool : IAgentTool
{
    private readonly string _workingDirectory;
    private readonly bool _powershell;
    private readonly CodingAgentShellToolOptions _options;
    private Func<CodingAgentShellToolOptions>? _sessionOptions;
    private Func<IReadOnlyDictionary<string, string?>>? _sessionEnvironment;

    /// <summary>【CodingAgent】【命令工具】创建绑定目录的 Bash 工具。</summary>
    /// <param name="workingDirectory">会话目录，空值捕获当前进程目录。</param><param name="options">执行后端和命令配置。</param>
    public ShellTool(string? workingDirectory = null, CodingAgentShellToolOptions? options = null) : this(false, workingDirectory, options) { }

    /// <summary>【CodingAgent】【命令工具】创建共享执行管线的指定 Shell 工具。</summary>
    /// <param name="powershell">是否使用 PowerShell。</param><param name="workingDirectory">会话目录。</param><param name="options">执行选项。</param>
    protected ShellTool(bool powershell, string? workingDirectory, CodingAgentShellToolOptions? options)
    {
        _powershell = powershell;
        _workingDirectory = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);
        _options = options ?? new();
    }

    public string Name => _powershell ? "powershell" : "bash";
    public string Label => Name;
    public string PromptSnippet => _powershell ? "Execute PowerShell commands" : "Execute bash commands (ls, grep, find, etc.)";
    public IReadOnlyList<string> PromptGuidelines => _options.ExposeSessionEnvironment
        ? ["You can inspect PI_* environment variables for current model and session details."] : [];
    public string Description => $"Execute a {Name} command in the current working directory. Returns stdout and stderr. Output is truncated to last 2000 lines or 50KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.";
    public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;
    public ConstrainedSamplingConfig ConstrainedSampling => new() { Type = "json_schema", Strict = "prefer" };
    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {"type":"object","properties":{"command":{"type":"string","description":"Shell command to execute"},"timeout":{"type":"number","description":"Timeout in seconds (optional, no default timeout)"}},"required":["command"]}
        """).RootElement.Clone();
    public JsonElement? OutputSchema { get; } = JsonDocument.Parse("""
        {"type":"object","properties":{"output":{"type":"string"},"truncated":{"type":"boolean"},"full_output_path":{"type":"string"},"exit_code":{"type":"number"},"wall_time_seconds":{"type":"number"}},"required":["output","truncated","exit_code","wall_time_seconds"]}
        """).RootElement.Clone();

    /// <summary>【CodingAgent】【会话绑定】每次执行读取当前配置和元数据，模型切换后不沿用旧值。</summary>
    /// <param name="options">动态配置来源。</param><param name="environment">当前会话的 PI_* 元数据。</param>
    internal void BindSession(Func<CodingAgentShellToolOptions> options, Func<IReadOnlyDictionary<string, string?>> environment)
    {
        _sessionOptions = options;
        _sessionEnvironment = environment;
    }

    /// <summary>【CodingAgent】【命令执行】串行汇总双输出管道，节流进度并在退出后关闭完整输出文件。</summary>
    /// <param name="toolCallId">调用标识。</param><param name="args">命令及可选秒数超时，兼容旧目录和毫秒参数。</param>
    /// <param name="ct">用户取消信号。</param><param name="onUpdate">可选增量快照接收器。</param><returns>文本、截断详情和结构化结果。</returns>
    public async Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
    {
        var command = args.GetProperty("command").GetString() ?? throw new ArgumentException("Command must be a string.");
        var timeout = ResolveTimeout(args);
        var options = _sessionOptions?.Invoke();
        var prefix = _options.CommandPrefix ?? (_powershell ? null : options?.CommandPrefix);
        var cwd = args.TryGetProperty("working_directory", out var directory) ? CodingAgentToolPaths.Resolve(directory.GetString(), _workingDirectory) : _workingDirectory;
        var context = new CodingAgentShellSpawnContext(string.IsNullOrEmpty(prefix) ? command : prefix + "\n" + command, cwd,
            CreateEnvironment(_options.ExposeSessionEnvironment ? _sessionEnvironment?.Invoke() : null));
        context = _options.SpawnHook?.Invoke(context) ?? context;
        var operations = _options.Operations ?? new LocalCodingAgentShellOperations(_powershell, _options.ShellPath ?? options?.ShellPath);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } seconds) cancellation.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, seconds * 1000)));
        using var output = new CodingAgentShellOutput("tau-" + Name);
        using var updates = new CancellationTokenSource();
        var gate = new SemaphoreSlim(1, 1);
        var accepting = true;
        var dirty = false;
        Exception? updateError = null;
        if (onUpdate is not null) await onUpdate(new ToolUpdate("", Content: [])).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();

        /// <summary>【CodingAgent】【进度快照】在持有输出锁时发送最新快照，避免重复或交错通知。</summary>
        /// <returns>发送完成的任务。</returns>
        async Task EmitAsync()
        {
            if (!dirty || onUpdate is null) return;
            dirty = false;
            var snapshot = output.Snapshot();
            await onUpdate(new ToolUpdate(snapshot.Content, Details: new ShellToolDetails(snapshot.Truncated ? snapshot : null, output.FullOutputPath))).ConfigureAwait(false);
        }

        /// <summary>【CodingAgent】【输出接收】忽略执行结束后自定义后端迟到的数据。</summary>
        /// <param name="text">新增输出。</param><returns>追加完成的任务。</returns>
        async Task AppendAsync(string text)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { if (accepting) { output.Append(text); dirty = true; } }
            finally { gate.Release(); }
        }

        /// <summary>【CodingAgent】【进度节流】每秒最多发送十次快照，接收器失败时取消正在执行的命令。</summary>
        /// <returns>停止或回调失败后完成的任务。</returns>
        async Task PumpAsync()
        {
            if (onUpdate is null) return;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            try
            {
                while (await timer.WaitForNextTickAsync(updates.Token).ConfigureAwait(false))
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try { await EmitAsync().ConfigureAwait(false); }
                    finally { gate.Release(); }
                }
            }
            catch (OperationCanceledException) when (updates.IsCancellationRequested) { }
            catch (Exception error) { updateError = error; cancellation.Cancel(); }
        }

        // 1. 【CodingAgent】【执行生命周期】输出泵与进程共同运行，结束时先停止接收再排空通知
        var pump = PumpAsync();
        int? exitCode = null;
        Exception? executionError = null;
        try
        {
            exitCode = await operations.ExecuteAsync(context, AppendAsync, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { executionError = error; }
        finally
        {
            await updates.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                accepting = false;
                if (updateError is null) await EmitAsync().ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        if (updateError is not null) throw updateError;
        var snapshot = output.Snapshot();
        var text = FormatOutput(snapshot, output.FullOutputPath, executionError is null ? "(no output)" : "");
        var details = snapshot.Truncated ? new ShellToolDetails(snapshot, output.FullOutputPath) : null;
        if (executionError is not null || exitCode is null)
        {
            var status = executionError is OperationCanceledException
                ? ct.IsCancellationRequested ? "Command aborted" : "Command timed out after " + timeout?.ToString(CultureInfo.InvariantCulture) + " seconds"
                : executionError?.Message ?? "Command terminated without an exit code";
            return new([new TextContent(AppendStatus(text, status))], IsError: true, Details: details);
        }
        // 2. 【CodingAgent】【程序结果】结构化内容使用独立的一 MiB 上限，模型只接收截断后的尾部
        var complete = await output.ReadStructuredOutputAsync().ConfigureAwait(false);
        var structured = new JsonObject
        {
            ["output"] = complete.Output, ["truncated"] = complete.Truncated, ["exit_code"] = exitCode.Value,
            ["wall_time_seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1, MidpointRounding.AwayFromZero)
        };
        if (complete.Truncated) structured["full_output_path"] = output.FullOutputPath;
        return new ToolResult([new TextContent(exitCode == 0 ? text : AppendStatus(text, $"Command exited with code {exitCode}"))], exitCode != 0, details)
        { StructuredContent = JsonSerializer.SerializeToElement(structured) };
    }

    /// <summary>【CodingAgent】【命令环境】清理继承的会话元数据并加入本次会话值。</summary>
    /// <param name="metadata">允许公开的会话字段，空值禁用公开。</param><returns>独立环境字典。</returns>
    internal static Dictionary<string, string?> CreateEnvironment(IReadOnlyDictionary<string, string?>? metadata)
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) environment[(string)entry.Key] = entry.Value?.ToString();
        foreach (var name in new[] { "PI_SESSION_ID", "PI_SESSION_FILE", "PI_PROVIDER", "PI_MODEL", "PI_REASONING_LEVEL" }) environment.Remove(name);
        if (metadata is not null) foreach (var pair in metadata) environment[pair.Key] = pair.Value;
        return environment;
    }

    /// <summary>【CodingAgent】【超时校验】按秒解析原生参数，仅在未提供秒数时读取旧毫秒字段。</summary>
    /// <param name="args">请求参数。</param><returns>有效秒数，空值表示不限时。</returns>
    private static double? ResolveTimeout(JsonElement args)
    {
        double value;
        if (args.TryGetProperty("timeout", out var timeout)) value = timeout.GetDouble();
        else if (args.TryGetProperty("timeout_ms", out var milliseconds)) value = milliseconds.GetDouble() / 1000;
        else return null;
        if (!double.IsFinite(value) || value <= 0 || value > int.MaxValue / 1000.0)
            throw new ArgumentOutOfRangeException(nameof(args), "Invalid timeout: must be a finite positive number of seconds, maximum 2147483.647.");
        return value;
    }

    /// <summary>【CodingAgent】【输出显示】加入原生截断范围和完整文件路径。</summary>
    /// <param name="snapshot">尾部快照。</param><param name="path">完整输出文件。</param><param name="empty">无输出时的文本。</param><returns>模型可见文本。</returns>
    private static string FormatOutput(ToolOutputTruncationResult snapshot, string? path, string empty)
    {
        var text = snapshot.Content.Length == 0 ? empty : snapshot.Content;
        if (!snapshot.Truncated) return text;
        var range = snapshot.LastLinePartial
            ? $"Showing last {ToolOutputTruncator.FormatSize(snapshot.OutputBytes)} of line {snapshot.TotalLines}"
            : $"Showing lines {snapshot.TotalLines - snapshot.OutputLines + 1}-{snapshot.TotalLines} of {snapshot.TotalLines}";
        return text + $"\n\n[{range}. Full output: {path}]";
    }

    /// <summary>【CodingAgent】【状态文本】以空行连接已有输出和执行状态。</summary>
    /// <param name="text">已有输出。</param><param name="status">执行状态。</param><returns>合并文本。</returns>
    private static string AppendStatus(string text, string status) => text.Length == 0 ? status : text + "\n\n" + status;
}

/// <summary>【CodingAgent】【命令详情】保存模型输出的截断统计及完整输出位置。</summary>
public sealed record ShellToolDetails(ToolOutputTruncationResult? Truncation = null, string? FullOutputPath = null);
