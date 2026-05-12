using System.Diagnostics;
using System.Text;

namespace Tau.Mom;

public enum MomSandboxKind
{
    Host,
    Docker
}

public sealed record MomSandboxConfig(MomSandboxKind Kind, string? Container = null)
{
    public static MomSandboxConfig Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            return new MomSandboxConfig(MomSandboxKind.Host);
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("docker:", StringComparison.OrdinalIgnoreCase))
        {
            var container = trimmed["docker:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(container))
            {
                throw new ArgumentException("Docker sandbox requires a container name, e.g. docker:mom-sandbox.", nameof(value));
            }

            return new MomSandboxConfig(MomSandboxKind.Docker, container);
        }

        throw new ArgumentException("Mom sandbox must be 'host' or 'docker:<container>'.", nameof(value));
    }

    public string DisplayName => Kind == MomSandboxKind.Host ? "host" : $"docker:{Container}";
}

public sealed record MomSandboxExecOptions(int? TimeoutSeconds = null);

public sealed record MomSandboxExecResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool TimedOut = false);

public interface IMomSandboxExecutor
{
    MomSandboxConfig Config { get; }
    string HostWorkingDirectory { get; }
    string WorkspacePath { get; }

    string ToWorkspacePath(string hostPath);
    string ToHostPath(string sandboxPath);
    bool IsHostPathInWorkspace(string hostPath);

    Task<MomSandboxExecResult> ExecAsync(
        string command,
        MomSandboxExecOptions? options = null,
        CancellationToken cancellationToken = default);
}

public static class MomSandboxExecutorFactory
{
    public static IMomSandboxExecutor Create(MomOptions options, string hostWorkingDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(MomSandboxConfig.Parse(options.Sandbox), hostWorkingDirectory);
    }

    public static IMomSandboxExecutor Create(MomSandboxConfig config, string hostWorkingDirectory)
    {
        var fullWorkingDirectory = Path.GetFullPath(hostWorkingDirectory);
        return config.Kind switch
        {
            MomSandboxKind.Host => new HostMomSandboxExecutor(config, fullWorkingDirectory),
            MomSandboxKind.Docker => new DockerMomSandboxExecutor(config, fullWorkingDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Kind, "Unknown Mom sandbox kind.")
        };
    }

    public static async Task ValidateAsync(MomSandboxConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Kind == MomSandboxKind.Host)
        {
            return;
        }

        var dockerVersion = await ProcessRunner.RunAsync(
                "docker",
                ["--version"],
                null,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (dockerVersion.ExitCode != 0)
        {
            throw new InvalidOperationException("Docker is not installed or not in PATH.");
        }

        var inspect = await ProcessRunner.RunAsync(
                "docker",
                ["inspect", "-f", "{{.State.Running}}", config.Container!],
                null,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (inspect.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker container '{config.Container}' does not exist.");
        }

        if (!inspect.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Docker container '{config.Container}' is not running.");
        }
    }
}

public sealed class HostMomSandboxExecutor : IMomSandboxExecutor
{
    public HostMomSandboxExecutor(MomSandboxConfig config, string hostWorkingDirectory)
    {
        Config = config;
        HostWorkingDirectory = Path.GetFullPath(hostWorkingDirectory);
        WorkspacePath = HostWorkingDirectory;
    }

    public MomSandboxConfig Config { get; }
    public string HostWorkingDirectory { get; }
    public string WorkspacePath { get; }

    public string ToWorkspacePath(string hostPath) => Path.GetFullPath(hostPath);

    public string ToHostPath(string sandboxPath)
    {
        if (string.IsNullOrWhiteSpace(sandboxPath))
        {
            return HostWorkingDirectory;
        }

        return Path.IsPathRooted(sandboxPath)
            ? Path.GetFullPath(sandboxPath)
            : Path.GetFullPath(sandboxPath, HostWorkingDirectory);
    }

    public bool IsHostPathInWorkspace(string hostPath)
    {
        return IsSubPath(Path.GetFullPath(hostPath), HostWorkingDirectory);
    }

    public Task<MomSandboxExecResult> ExecAsync(
        string command,
        MomSandboxExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var isWindows = OperatingSystem.IsWindows();
        return ProcessRunner.RunAsync(
            isWindows ? "cmd.exe" : "sh",
            isWindows ? ["/c", command] : ["-c", command],
            HostWorkingDirectory,
            options?.TimeoutSeconds,
            cancellationToken);
    }

    private static bool IsSubPath(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DockerMomSandboxExecutor : IMomSandboxExecutor
{
    public DockerMomSandboxExecutor(MomSandboxConfig config, string hostWorkingDirectory)
    {
        if (config.Kind != MomSandboxKind.Docker || string.IsNullOrWhiteSpace(config.Container))
        {
            throw new ArgumentException("Docker executor requires docker:<container> config.", nameof(config));
        }

        Config = config;
        HostWorkingDirectory = Path.GetFullPath(hostWorkingDirectory);
        WorkspacePath = "/workspace";
    }

    public MomSandboxConfig Config { get; }
    public string HostWorkingDirectory { get; }
    public string WorkspacePath { get; }

    public string ToWorkspacePath(string hostPath)
    {
        var fullHostPath = Path.GetFullPath(hostPath);
        if (!IsHostPathInWorkspace(fullHostPath))
        {
            return fullHostPath;
        }

        var relative = Path.GetRelativePath(HostWorkingDirectory, fullHostPath).Replace('\\', '/');
        return relative == "."
            ? WorkspacePath
            : $"{WorkspacePath}/{relative}";
    }

    public string ToHostPath(string sandboxPath)
    {
        if (string.IsNullOrWhiteSpace(sandboxPath))
        {
            return HostWorkingDirectory;
        }

        var normalized = sandboxPath.Replace('\\', '/');
        if (normalized.Equals(WorkspacePath, StringComparison.Ordinal) ||
            normalized.StartsWith($"{WorkspacePath}/", StringComparison.Ordinal))
        {
            var relative = normalized.Length == WorkspacePath.Length
                ? "."
                : normalized[(WorkspacePath.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(relative, HostWorkingDirectory);
        }

        return Path.IsPathRooted(sandboxPath)
            ? Path.GetFullPath(sandboxPath)
            : Path.GetFullPath(sandboxPath, HostWorkingDirectory);
    }

    public bool IsHostPathInWorkspace(string hostPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(HostWorkingDirectory)) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(hostPath);
        return normalizedCandidate.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(HostWorkingDirectory)), StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public Task<MomSandboxExecResult> ExecAsync(
        string command,
        MomSandboxExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ProcessRunner.RunAsync(
            "docker",
            ["exec", "-w", WorkspacePath, Config.Container!, "sh", "-c", command],
            null,
            options?.TimeoutSeconds,
            cancellationToken);
    }
}

internal static class ProcessRunner
{
    public static async Task<MomSandboxExecResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new MomSandboxExecResult(string.Empty, $"Failed to start process: {fileName}", -1);
        }

        using var _ = process;
        using var registration = cancellationToken.Register(static state =>
        {
            var p = (Process)state!;
            TryKill(p);
        }, process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitTask = process.WaitForExitAsync(CancellationToken.None);
        var timedOut = false;

        if (timeoutSeconds is > 0)
        {
            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds.Value), cancellationToken);
            var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
            if (completed == delayTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                timedOut = true;
                TryKill(process);
            }
        }

        if (!timedOut)
        {
            await waitTask.ConfigureAwait(false);
        }
        else
        {
            try
            {
                await waitTask.ConfigureAwait(false);
            }
            catch
            {
                // The process may already have exited after taskkill/process kill.
            }
        }

        var stdout = await ReadCompletedAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await ReadCompletedAsync(stderrTask).ConfigureAwait(false);
        if (timedOut)
        {
            stderr = string.IsNullOrWhiteSpace(stderr)
                ? $"Command timed out after {timeoutSeconds} seconds."
                : $"{stderr.TrimEnd()}\nCommand timed out after {timeoutSeconds} seconds.";
            return new MomSandboxExecResult(stdout, stderr, -1, TimedOut: true);
        }

        return new MomSandboxExecResult(stdout, stderr, process.ExitCode);
    }

    private static async Task<string> ReadCompletedAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cancellation/timeout cleanup.
        }
    }
}
