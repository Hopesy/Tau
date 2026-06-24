using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class ShellTool : IAgentTool
{
    public string Name => "shell";
    public string Label => "Shell";
    public string Description => "Execute a shell command and return its output.";
    public ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "command": { "type": "string", "description": "The shell command to execute" },
                "working_directory": { "type": "string", "description": "Working directory for the command" },
                "timeout_ms": { "type": "integer", "description": "Timeout in milliseconds (default: 120000)" }
            },
            "required": ["command"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var command = args.GetProperty("command").GetString()!;
        var workDir = args.TryGetProperty("working_directory", out var wd) ? wd.GetString() : null;
        var timeoutMs = args.TryGetProperty("timeout_ms", out var t) ? t.GetInt32() : 120_000;

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir ?? Directory.GetCurrentDirectory()
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        using var process = Process.Start(psi);
        if (process is null)
            return new ToolResult([new TextContent("Failed to start process.")], IsError: true);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var readOut = process.StandardOutput.ReadToEndAsync(cts.Token);
        var readErr = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new ToolResult([new TextContent("Command timed out.")], IsError: true);
        }

        stdout.Append(await readOut.ConfigureAwait(false));
        stderr.Append(await readErr.ConfigureAwait(false));

        var output = new StringBuilder();
        if (stdout.Length > 0) output.Append(stdout);
        if (stderr.Length > 0)
        {
            if (output.Length > 0) output.AppendLine();
            output.Append("[stderr]\n").Append(stderr);
        }
        output.Append($"\n[exit code: {process.ExitCode}]");

        return new ToolResult(
            [new TextContent(output.ToString())],
            IsError: process.ExitCode != 0);
    }
}
