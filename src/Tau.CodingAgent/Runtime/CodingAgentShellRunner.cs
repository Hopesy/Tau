using System.Diagnostics;
using System.Text;

namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentShellRunner
{
    Task<CodingAgentShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default);

    void Abort();
}

public sealed record CodingAgentShellResult(
    string Output,
    int? ExitCode,
    bool Cancelled,
    bool Truncated,
    string? FullOutputPath = null);

public sealed class SystemCodingAgentShellRunner : ICodingAgentShellRunner
{
    private const int MaxReturnedOutputChars = 64 * 1024;

    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;
    private Process? _activeProcess;

    public async Task<CodingAgentShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
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

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
            }

            var cancelled = cts.IsCancellationRequested;
            var stdout = await ReadCompletedOrEmptyAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadCompletedOrEmptyAsync(stderrTask).ConfigureAwait(false);
            var output = BuildOutput(stdout, stderr);
            var truncatedOutput = TruncateTail(output, out var truncated, out var fullOutputPath);
            return new CodingAgentShellResult(
                truncatedOutput,
                cancelled ? null : process.ExitCode,
                cancelled,
                truncated,
                fullOutputPath);
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

    private static async Task<string> ReadCompletedOrEmptyAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string BuildOutput(string stdout, string stderr)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(stdout))
        {
            builder.Append(NormalizeOutput(stdout));
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            if (builder.Length > 0 && builder[^1] != '\n')
            {
                builder.AppendLine();
            }

            builder.Append(NormalizeOutput(stderr));
        }

        return builder.ToString();
    }

    private static string NormalizeOutput(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Replace("\r", string.Empty, StringComparison.Ordinal))
        {
            if (character == '\n' || character == '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }

    private static string TruncateTail(string output, out bool truncated, out string? fullOutputPath)
    {
        fullOutputPath = null;
        if (output.Length <= MaxReturnedOutputChars)
        {
            truncated = false;
            return output;
        }

        fullOutputPath = Path.Combine(Path.GetTempPath(), $"tau-bash-{Guid.NewGuid():N}.log");
        File.WriteAllText(fullOutputPath, output, Encoding.UTF8);
        truncated = true;
        return output[^MaxReturnedOutputChars..];
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
