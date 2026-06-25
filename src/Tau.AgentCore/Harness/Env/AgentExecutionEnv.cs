using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tau.AgentCore.Harness.Env;

public interface IAgentExecutionEnv
{
    string WorkingDirectory { get; }

    string GetAbsolutePath(string path);

    string JoinPath(params string[] parts);

    Task<AgentExecutionResult> ExecAsync(
        string command,
        AgentExecutionOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<string> ReadTextFileAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ReadTextLinesAsync(
        string path,
        int? maxLines = null,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default);

    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);

    Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default);

    Task AppendFileAsync(string path, string content, CancellationToken cancellationToken = default);

    Task<AgentFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentFileInfo>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task<string> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string path,
        bool recursive = false,
        bool force = false,
        CancellationToken cancellationToken = default);

    Task<string> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default);

    Task<string> CreateTempFileAsync(
        string? prefix = null,
        string? suffix = null,
        CancellationToken cancellationToken = default);

    Task CleanupAsync(CancellationToken cancellationToken = default);
}

public sealed record AgentExecutionEnvOptions(
    string WorkingDirectory,
    string? ShellPath = null,
    IReadOnlyDictionary<string, string?>? ShellEnvironment = null);

public sealed record AgentExecutionOptions(
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    Action<string>? OnStdout = null,
    Action<string>? OnStderr = null);

public sealed record AgentExecutionResult(
    string Stdout,
    string Stderr,
    int ExitCode);

public sealed record AgentFileInfo(
    string Name,
    string Path,
    AgentFileKind Kind,
    long Size,
    double ModifiedTimeMilliseconds);

public enum AgentFileKind
{
    File,
    Directory,
    Symlink
}

public enum AgentExecutionErrorCode
{
    ShellUnavailable,
    SpawnError,
    Timeout,
    Aborted,
    CallbackError
}

public enum AgentFileErrorCode
{
    Aborted,
    NotFound,
    PermissionDenied,
    NotDirectory,
    IsDirectory,
    Invalid,
    Unknown
}

public sealed class AgentExecutionException : Exception
{
    public AgentExecutionException(AgentExecutionErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public AgentExecutionErrorCode Code { get; }
}

public sealed class AgentFileException : IOException
{
    public AgentFileException(
        AgentFileErrorCode code,
        string message,
        string? path = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        FilePath = path;
    }

    public AgentFileErrorCode Code { get; }

    public string? FilePath { get; }
}

public sealed class SystemAgentExecutionEnv : IAgentExecutionEnv
{
    private readonly string? _shellPath;
    private readonly IReadOnlyDictionary<string, string?>? _shellEnvironment;

    public SystemAgentExecutionEnv(AgentExecutionEnvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
            throw new ArgumentException("Working directory cannot be empty.", nameof(options));

        WorkingDirectory = Path.GetFullPath(options.WorkingDirectory);
        _shellPath = options.ShellPath;
        _shellEnvironment = options.ShellEnvironment;
    }

    public string WorkingDirectory { get; }

    public string GetAbsolutePath(string path) =>
        Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(WorkingDirectory, path));

    public string JoinPath(params string[] parts) => Path.Combine(parts);

    public async Task<AgentExecutionResult> ExecAsync(
        string command,
        AgentExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Shell command cannot be empty.", nameof(command));

        cancellationToken.ThrowIfCancellationRequested();

        var shell = await ResolveShellAsync(_shellPath, cancellationToken).ConfigureAwait(false);
        using var timeoutSource = options?.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : null;
        using var linkedSource = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var startInfo = new ProcessStartInfo(shell.Path)
        {
            WorkingDirectory = options?.WorkingDirectory is { Length: > 0 } cwd
                ? GetAbsolutePath(cwd)
                : WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = shell.CommandTransport == AgentShellCommandTransport.Stdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in shell.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (shell.CommandTransport == AgentShellCommandTransport.Arguments)
            startInfo.ArgumentList.Add(command);

        MergeEnvironment(startInfo, _shellEnvironment);
        MergeEnvironment(startInfo, options?.Environment);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        AgentExecutionException? callbackError = null;

        try
        {
            if (!process.Start())
                throw new AgentExecutionException(AgentExecutionErrorCode.SpawnError, "Failed to start shell process.");
        }
        catch (AgentExecutionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new AgentExecutionException(AgentExecutionErrorCode.SpawnError, ex.Message, ex);
        }

        if (shell.CommandTransport == AgentShellCommandTransport.Stdin)
        {
            try
            {
                await process.StandardInput.WriteAsync(command.AsMemory(), linkedSource.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(linkedSource.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                if (ex is OperationCanceledException)
                    KillProcessTree(process);
            }
        }

        var stdoutTask = ReadProcessStreamAsync(process.StandardOutput, stdout, options?.OnStdout, ErrorStream.Stdout, linkedSource.Token);
        var stderrTask = ReadProcessStreamAsync(process.StandardError, stderr, options?.OnStderr, ErrorStream.Stderr, linkedSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (AgentExecutionException ex) when (ex.Code == AgentExecutionErrorCode.CallbackError)
        {
            callbackError = ex;
            KillProcessTree(process);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        if (callbackError is not null)
            throw callbackError;

        if (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            throw new AgentExecutionException(AgentExecutionErrorCode.Timeout, $"Command timed out after {options?.Timeout}.");

        if (cancellationToken.IsCancellationRequested)
            throw new AgentExecutionException(AgentExecutionErrorCode.Aborted, "Command was aborted.");

        return new AgentExecutionResult(stdout.ToString(), stderr.ToString(), process.HasExited ? process.ExitCode : -1);
    }

    public async Task<string> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = GetAbsolutePath(path);
        try
        {
            return await File.ReadAllTextAsync(resolved, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public async Task<IReadOnlyList<string>> ReadTextLinesAsync(
        string path,
        int? maxLines = null,
        CancellationToken cancellationToken = default)
    {
        if (maxLines <= 0)
            return [];

        var lines = new List<string>();
        var resolved = GetAbsolutePath(path);
        await foreach (var line in EnumerateLinesAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            lines.Add(line);
            if (maxLines is not null && lines.Count >= maxLines)
                break;
        }

        return lines;
    }

    public async Task<byte[]> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = GetAbsolutePath(path);
        try
        {
            return await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var resolved = GetAbsolutePath(path);
        try
        {
            EnsureParentDirectory(resolved);
            await File.WriteAllTextAsync(resolved, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public async Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        var resolved = GetAbsolutePath(path);
        try
        {
            EnsureParentDirectory(resolved);
            await File.WriteAllBytesAsync(resolved, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public async Task AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var resolved = GetAbsolutePath(path);
        try
        {
            EnsureParentDirectory(resolved);
            await File.AppendAllTextAsync(resolved, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public Task<AgentFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = GetAbsolutePath(path);
        try
        {
            return Task.FromResult(ToAgentFileInfo(resolved, File.GetAttributes(resolved)));
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public Task<IReadOnlyList<AgentFileInfo>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = GetAbsolutePath(path);
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(resolved)
                .Select(entry =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ToAgentFileInfo(entry, File.GetAttributes(entry));
                })
                .ToArray();
            return Task.FromResult<IReadOnlyList<AgentFileInfo>>(entries);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public Task<string> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = GetAbsolutePath(path);
        try
        {
            if (File.Exists(resolved))
                return Task.FromResult(new FileInfo(resolved).FullName);
            if (Directory.Exists(resolved))
                return Task.FromResult(new DirectoryInfo(resolved).FullName);

            throw new FileNotFoundException("File or directory not found.", resolved);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = GetAbsolutePath(path);
        return Task.FromResult(File.Exists(resolved) || Directory.Exists(resolved));
    }

    public Task CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = GetAbsolutePath(path);
        try
        {
            if (!recursive)
            {
                var parent = Directory.GetParent(resolved);
                if (parent is not null && !parent.Exists)
                    throw new DirectoryNotFoundException($"Could not find a part of the path '{parent.FullName}'.");
            }

            Directory.CreateDirectory(resolved);
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public Task RemoveAsync(
        string path,
        bool recursive = false,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = GetAbsolutePath(path);
        try
        {
            if (File.Exists(resolved))
            {
                File.Delete(resolved);
            }
            else if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive);
            }
            else if (!force)
            {
                throw new FileNotFoundException("File or directory not found.", resolved);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }
    }

    public Task<string> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return Task.FromResult(path);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex);
        }
    }

    public async Task<string> CreateTempFileAsync(
        string? prefix = null,
        string? suffix = null,
        CancellationToken cancellationToken = default)
    {
        var directory = await CreateTempDirectoryAsync("tmp-", cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(directory, $"{prefix ?? string.Empty}{Guid.NewGuid():N}{suffix ?? string.Empty}");
        try
        {
            await File.WriteAllTextAsync(path, string.Empty, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, path);
        }
    }

    public Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> EnumerateLinesAsync(
        string resolved,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StreamReader reader;
        try
        {
            reader = new StreamReader(File.OpenRead(resolved), Encoding.UTF8);
        }
        catch (Exception ex) when (ShouldWrapFileException(ex))
        {
            throw ToFileException(ex, resolved);
        }

        using (reader)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ShouldWrapFileException(ex))
                {
                    throw ToFileException(ex, resolved);
                }

                if (line is null)
                    break;

                yield return line;
            }
        }
    }

    private static async Task ReadProcessStreamAsync(
        StreamReader reader,
        StringBuilder builder,
        Action<string>? callback,
        ErrorStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
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
                break;

            var chunk = new string(buffer, 0, read);
            builder.Append(chunk);
            if (callback is null)
                continue;

            try
            {
                callback(chunk);
            }
            catch (Exception ex)
            {
                throw new AgentExecutionException(
                    AgentExecutionErrorCode.CallbackError,
                    $"{stream} callback failed: {ex.Message}",
                    ex);
            }
        }
    }

    private static async Task<AgentShellConfig> ResolveShellAsync(
        string? customShellPath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(customShellPath))
        {
            if (File.Exists(customShellPath))
                return GetBashShellConfig(customShellPath);

            throw new AgentExecutionException(
                AgentExecutionErrorCode.ShellUnavailable,
                $"Custom shell path not found: {customShellPath}");
        }

        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } programFiles
                    ? Path.Combine(programFiles, "Git", "bin", "bash.exe")
                    : null,
                Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programFilesX86
                    ? Path.Combine(programFilesX86, "Git", "bin", "bash.exe")
                    : null
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    return GetBashShellConfig(candidate);
            }

            var bashOnPath = await FindCommandOnPathAsync("where", "bash.exe", cancellationToken).ConfigureAwait(false);
            if (bashOnPath is not null)
                return GetBashShellConfig(bashOnPath);

            throw new AgentExecutionException(AgentExecutionErrorCode.ShellUnavailable, "No bash shell found.");
        }

        if (File.Exists("/bin/bash"))
            return GetBashShellConfig("/bin/bash");

        var bash = await FindCommandOnPathAsync("which", "bash", cancellationToken).ConfigureAwait(false);
        if (bash is not null)
            return GetBashShellConfig(bash);

        return new AgentShellConfig("sh", ["-c"], AgentShellCommandTransport.Arguments);
    }

    private static AgentShellConfig GetBashShellConfig(string shellPath) =>
        IsLegacyWslBashPath(shellPath)
            ? new AgentShellConfig(shellPath, ["-s"], AgentShellCommandTransport.Stdin)
            : new AgentShellConfig(shellPath, ["-c"], AgentShellCommandTransport.Arguments);

    private static bool IsLegacyWslBashPath(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        return normalized.EndsWith(@"\windows\system32\bash.exe", StringComparison.Ordinal)
            || normalized.EndsWith(@"\windows\sysnative\bash.exe", StringComparison.Ordinal);
    }

    private static async Task<string?> FindCommandOnPathAsync(
        string command,
        string argument,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command, argument)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            if (!process.Start())
                return null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var stdout = await process.StandardOutput.ReadToEndAsync(linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            var first = stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return first is not null && File.Exists(first) ? first : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    private static void MergeEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null)
            return;

        foreach (var pair in environment)
        {
            if (pair.Value is null)
                startInfo.Environment.Remove(pair.Key);
            else
                startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    private static AgentFileInfo ToAgentFileInfo(string path, FileAttributes attributes)
    {
        var kind = (attributes & FileAttributes.ReparsePoint) != 0
            ? AgentFileKind.Symlink
            : (attributes & FileAttributes.Directory) != 0
                ? AgentFileKind.Directory
                : AgentFileKind.File;

        var info = kind == AgentFileKind.Directory
            ? new DirectoryInfo(path) as FileSystemInfo
            : new FileInfo(path);

        return new AgentFileInfo(
            info.Name,
            info.FullName,
            kind,
            kind == AgentFileKind.Directory ? 0 : ((FileInfo)info).Length,
            info.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalMilliseconds);
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private static bool ShouldWrapFileException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException;

    private static AgentFileException ToFileException(Exception error, string? path = null)
    {
        if (error is AgentFileException fileError)
            return fileError;

        if (error is OperationCanceledException)
            return new AgentFileException(AgentFileErrorCode.Aborted, "Operation was aborted.", path, error);
        if (error is FileNotFoundException or DirectoryNotFoundException)
            return new AgentFileException(AgentFileErrorCode.NotFound, error.Message, path, error);
        if (error is UnauthorizedAccessException)
            return new AgentFileException(AgentFileErrorCode.PermissionDenied, error.Message, path, error);
        if (error is ArgumentException or NotSupportedException)
            return new AgentFileException(AgentFileErrorCode.Invalid, error.Message, path, error);

        var hresult = error.HResult;
        return hresult switch
        {
            unchecked((int)0x80070003) => new AgentFileException(AgentFileErrorCode.NotFound, error.Message, path, error),
            unchecked((int)0x80070005) => new AgentFileException(AgentFileErrorCode.PermissionDenied, error.Message, path, error),
            unchecked((int)0x800700B7) => new AgentFileException(AgentFileErrorCode.Invalid, error.Message, path, error),
            _ when error.Message.Contains("part of the path", StringComparison.OrdinalIgnoreCase) =>
                new AgentFileException(AgentFileErrorCode.NotDirectory, error.Message, path, error),
            _ when error.Message.Contains("directory", StringComparison.OrdinalIgnoreCase) &&
                   error.Message.Contains("file", StringComparison.OrdinalIgnoreCase) =>
                new AgentFileException(AgentFileErrorCode.IsDirectory, error.Message, path, error),
            _ => new AgentFileException(AgentFileErrorCode.Unknown, error.Message, path, error)
        };
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record AgentShellConfig(
        string Path,
        string[] Arguments,
        AgentShellCommandTransport CommandTransport);

    private enum AgentShellCommandTransport
    {
        Arguments,
        Stdin
    }

    private enum ErrorStream
    {
        Stdout,
        Stderr
    }
}
