using System.Diagnostics;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodExecService
{
    private readonly Func<ProcessStartInfo, CancellationToken, Task<ProcessExecutionResult>> _executeProcessAsync;

    public PodExecService(Func<ProcessStartInfo, CancellationToken, Task<ProcessExecutionResult>>? executeProcessAsync = null)
    {
        _executeProcessAsync = executeProcessAsync ?? ExecuteProcessAsync;
    }

    public async Task<PodExecResult> ExecuteAsync(PodDefinition pod, string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (!string.IsNullOrWhiteSpace(pod.Endpoint))
        {
            return new PodExecResult(
                pod.Id,
                false,
                "http",
                command.Trim(),
                pod.Endpoint!,
                1,
                string.Empty,
                string.Empty,
                TimeSpan.Zero,
                "endpoint pods do not support remote exec yet");
        }

        var host = pod.SshHost ?? string.Empty;
        var port = pod.SshPort ?? 22;
        var target = $"{host}:{port}";
        var watch = Stopwatch.StartNew();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"-p {port} -o BatchMode=yes -o ConnectTimeout=5 {host} \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var execution = await _executeProcessAsync(psi, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            var success = execution.ExitCode == 0;
            var summary = success ? "ssh exec ok" : $"ssh exec failed ({execution.ExitCode})";

            return new PodExecResult(
                pod.Id,
                success,
                "ssh",
                command.Trim(),
                target,
                execution.ExitCode,
                execution.StdOut,
                execution.StdErr,
                watch.Elapsed,
                summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.Stop();
            return new PodExecResult(
                pod.Id,
                false,
                "ssh",
                command.Trim(),
                target,
                1,
                string.Empty,
                ex.Message,
                watch.Elapsed,
                $"ssh exec error: {ex.Message}");
        }
    }

    private static async Task<ProcessExecutionResult> ExecuteProcessAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var process = Process.Start(psi);
        if (process is null)
        {
            return new ProcessExecutionResult(1, string.Empty, "failed to start ssh process");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
    }

    public sealed record ProcessExecutionResult(int ExitCode, string StdOut, string StdErr);
}
