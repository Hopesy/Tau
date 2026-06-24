using System.Diagnostics;
using Tau.AgentCore.Harness;

namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentShellRunner
{
    Task<CodingAgentShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default);

    Task<CodingAgentShellResult> ExecuteAsync(
        string command,
        IProgress<CodingAgentShellEvent>? progress,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, cancellationToken);

    void Abort();
}

public sealed record CodingAgentShellEvent(
    string Stream,
    string Text,
    DateTimeOffset Timestamp);

public sealed record CodingAgentShellResult(
    string Output,
    int? ExitCode,
    bool Cancelled,
    bool Truncated,
    string? FullOutputPath = null);

public sealed class SystemCodingAgentShellRunner : ICodingAgentShellRunner
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;
    private Process? _activeProcess;

    public Task<CodingAgentShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, progress: null, cancellationToken);

    public async Task<CodingAgentShellResult> ExecuteAsync(
        string command,
        IProgress<CodingAgentShellEvent>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Shell command cannot be empty.", nameof(command));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var process = CreateProcess(command);
        lock (_gate)
        {
            if (_activeProcess is not null)
            {
                throw new InvalidOperationException("A bash command is already running.");
            }

            _activeCts = cts;
            _activeProcess = process;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start shell process.");
            }

            var capture = new ShellOutputCapture();
            var stdoutTask = ReadStreamAsync(process.StandardOutput, "stdout", capture, progress, cts.Token);
            var stderrTask = ReadStreamAsync(process.StandardError, "stderr", capture, progress, cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
            }

            var cancelled = cts.IsCancellationRequested;
            await WaitForReaderAsync(stdoutTask).ConfigureAwait(false);
            await WaitForReaderAsync(stderrTask).ConfigureAwait(false);
            var captured = capture.Complete(cancelled ? null : process.ExitCode, cancelled);
            return new CodingAgentShellResult(
                captured.Output,
                captured.ExitCode,
                captured.Cancelled,
                captured.Truncated,
                captured.FullOutputPath);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeProcess, process))
                {
                    _activeProcess = null;
                    _activeCts = null;
                }
            }

            process.Dispose();
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            _activeCts?.Cancel();
            if (_activeProcess is { HasExited: false } process)
            {
                KillProcessTree(process);
            }
        }
    }

    private static Process CreateProcess(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        return new Process { StartInfo = startInfo };
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        string stream,
        ShellOutputCapture capture,
        IProgress<CodingAgentShellEvent>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            var text = capture.AppendChunk(new string(buffer, 0, read));
            if (text.Length == 0)
            {
                continue;
            }

            progress?.Report(new CodingAgentShellEvent(stream, text, DateTimeOffset.UtcNow));
        }
    }

    private static async Task WaitForReaderAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
