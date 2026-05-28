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
                "endpoint pods do not support remote exec yet",
                PodExecFailureKinds.UnsupportedTransport);
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
                    "ssh exec cancelled",
                    PodExecFailureKinds.SshExecCancelled);
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
                    "ssh process start failed",
                    PodExecFailureKinds.SshProcessStartFailed);
            }

            var success = execution.ExitCode == 0;
            var summary = success ? "ssh exec ok" : $"ssh exec failed ({execution.ExitCode})";
            var failureKind = success ? PodExecFailureKinds.None : ClassifySshExitFailure(execution);

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
                summary,
                failureKind);
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
                "ssh exec cancelled",
                PodExecFailureKinds.SshExecCancelled);
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
                "ssh process start failed",
                PodExecFailureKinds.SshProcessStartFailed);
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
                "ssh process runner failed",
                PodExecFailureKinds.SshProcessRunnerFailed);
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

    private static string ClassifySshExitFailure(ProcessExecutionResult execution)
    {
        var text = string.Join('\n', execution.StdErr, execution.StdOut);
        if (Contains(text, "Permission denied") || Contains(text, "publickey") || Contains(text, "Authentication failed"))
        {
            return PodExecFailureKinds.SshAuthFailed;
        }

        if (Contains(text, "Host key verification failed") || Contains(text, "REMOTE HOST IDENTIFICATION HAS CHANGED"))
        {
            return PodExecFailureKinds.SshHostKeyFailed;
        }

        if (Contains(text, "Could not resolve hostname") || Contains(text, "Name or service not known") || Contains(text, "No such host is known"))
        {
            return PodExecFailureKinds.SshHostUnresolved;
        }

        if (Contains(text, "Connection timed out") || Contains(text, "Operation timed out") || Contains(text, "connect to host") && Contains(text, "timed out"))
        {
            return PodExecFailureKinds.SshConnectTimeout;
        }

        if (Contains(text, "Connection refused") || Contains(text, "No route to host") || Contains(text, "Connection reset by peer") || Contains(text, "Connection closed"))
        {
            return PodExecFailureKinds.SshConnectionFailed;
        }

        return PodExecFailureKinds.SshExecFailed;
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);

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
