using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Tau.Pods.Models;

namespace Tau.Pods.Services;

public sealed class PodExecService
{
    private const int ProcessFailureExitCode = -1;

    private readonly Func<ProcessStartInfo, CancellationToken, Task<ProcessExecutionResult>> _executeProcessAsync;

    public PodExecService(Func<ProcessStartInfo, CancellationToken, Task<ProcessExecutionResult>>? executeProcessAsync = null)
    {
        _executeProcessAsync = executeProcessAsync ?? ExecuteProcessAsync;
    }

    public async Task<PodExecResult> ExecuteAsync(
        PodDefinition pod,
        string command,
        CancellationToken cancellationToken = default,
        bool keepAlive = false)
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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ConnectTimeout=5");
            if (keepAlive)
            {
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("ServerAliveInterval=30");
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("ServerAliveCountMax=120");
            }
            psi.ArgumentList.Add(host);
            psi.ArgumentList.Add(command);

            var execution = await _executeProcessAsync(psi, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            if (execution.Cancelled)
            {
                return new PodExecResult(
                    pod.Id,
                    false,
                    "ssh",
                    command.Trim(),
                    target,
                    ProcessFailureExitCode,
                    execution.StdOut,
                    string.IsNullOrWhiteSpace(execution.StdErr) ? "ssh exec cancelled before completion" : execution.StdErr,
                    watch.Elapsed,
                    "ssh exec cancelled");
            }

            if (!execution.Started)
            {
                return new PodExecResult(
                    pod.Id,
                    false,
                    "ssh",
                    command.Trim(),
                    target,
                    ProcessFailureExitCode,
                    execution.StdOut,
                    execution.StdErr,
                    watch.Elapsed,
                    "ssh process start failed");
            }

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
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new PodExecResult(
                pod.Id,
                false,
                "ssh",
                command.Trim(),
                target,
                ProcessFailureExitCode,
                string.Empty,
                "ssh exec cancelled before completion",
                watch.Elapsed,
                "ssh exec cancelled");
        }
        catch (Exception ex) when (IsProcessStartException(ex))
        {
            watch.Stop();
            return new PodExecResult(
                pod.Id,
                false,
                "ssh",
                command.Trim(),
                target,
                ProcessFailureExitCode,
                string.Empty,
                FormatProcessError("ssh process start failed", ex),
                watch.Elapsed,
                "ssh process start failed");
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
                ProcessFailureExitCode,
                string.Empty,
                FormatProcessError("ssh process runner failed", ex),
                watch.Elapsed,
                "ssh process runner failed");
        }
    }

    private static async Task<ProcessExecutionResult> ExecuteProcessAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        Process? startedProcess;
        try
        {
            startedProcess = Process.Start(psi);
        }
        catch (Exception ex) when (IsProcessStartException(ex))
        {
            return ProcessExecutionResult.StartFailed(FormatProcessError("ssh process start failed", ex));
        }

        using var process = startedProcess;
        if (process is null)
        {
            return ProcessExecutionResult.StartFailed("ssh process start failed: Process.Start returned null");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            return ProcessExecutionResult.CancelledFailure();
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // Best effort: cancellation must still surface as a structured exec failure.
        }
    }

    private static bool IsProcessStartException(Exception ex) =>
        ex is Win32Exception or InvalidOperationException or DirectoryNotFoundException or UnauthorizedAccessException;

    private static string FormatProcessError(string prefix, Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrWhiteSpace(message)
            ? $"{prefix}: {ex.GetType().Name}"
            : $"{prefix}: {ex.GetType().Name}: {message}";
    }

    public sealed record ProcessExecutionResult(
        int ExitCode,
        string StdOut,
        string StdErr,
        bool Started = true,
        bool Cancelled = false)
    {
        public static ProcessExecutionResult StartFailed(string stderr) =>
            new(ProcessFailureExitCode, string.Empty, stderr, Started: false);

        public static ProcessExecutionResult CancelledFailure() =>
            new(ProcessFailureExitCode, string.Empty, "ssh exec cancelled before completion", Cancelled: true);
    }
}
