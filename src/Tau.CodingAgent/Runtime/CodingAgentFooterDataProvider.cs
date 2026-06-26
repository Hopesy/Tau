using System.Diagnostics;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentFooterDataProvider : IDisposable
{
    private const int WatchDebounceMilliseconds = 500;

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _extensionStatuses = new(StringComparer.Ordinal);
    private readonly List<Action> _branchChangeCallbacks = [];
    private IReadOnlyList<string>? _customFooterLines;
    private string _cwd;
    private CodingAgentGitPaths? _gitPaths;
    private string? _cachedBranch;
    private bool _branchResolved;
    private int _availableProviderCount;
    private FileSystemWatcher? _headWatcher;
    private FileSystemWatcher? _reftableWatcher;
    private FileSystemWatcher? _reftableTablesListWatcher;
    private Timer? _refreshTimer;
    private Timer? _headPollingTimer;
    private Timer? _gitWatcherRetryTimer;
    private CodingAgentFileSnapshot? _lastHeadSnapshot;
    private bool _disposed;

    public CodingAgentFooterDataProvider(string cwd)
    {
        _cwd = NormalizeDirectory(cwd);
        _gitPaths = FindGitPaths(_cwd);
        SetupGitWatcher();
    }

    public string? GetGitBranch()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_branchResolved)
            {
                _cachedBranch = ResolveGitBranch();
                _branchResolved = true;
            }

            return _cachedBranch;
        }
    }

    public string GetCwd()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _cwd;
        }
    }

    public IReadOnlyDictionary<string, string> GetExtensionStatuses()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return new Dictionary<string, string>(_extensionStatuses, StringComparer.Ordinal);
        }
    }

    public IReadOnlyList<string>? GetCustomFooterLines()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _customFooterLines?.ToArray();
        }
    }

    public IDisposable OnBranchChange(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            ThrowIfDisposed();
            _branchChangeCallbacks.Add(callback);
        }

        return new Subscription(this, callback);
    }

    public void SetExtensionStatus(string key, string? text)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            if (text is null)
            {
                _extensionStatuses.Remove(key);
            }
            else
            {
                _extensionStatuses[key] = text;
            }
        }
    }

    public void ClearExtensionStatuses()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _extensionStatuses.Clear();
        }
    }

    public void SetCustomFooterLines(IReadOnlyList<string>? lines)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _customFooterLines = lines?.ToArray();
        }
    }

    public int GetAvailableProviderCount()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _availableProviderCount;
        }
    }

    public void SetAvailableProviderCount(int count)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _availableProviderCount = Math.Max(0, count);
        }
    }

    public void SetCwd(string cwd)
    {
        var normalized = NormalizeDirectory(cwd);
        List<Action>? callbacks = null;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (string.Equals(_cwd, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _cwd = normalized;
            ClearGitWatcherCore();
            _gitPaths = FindGitPaths(_cwd);
            _cachedBranch = null;
            _branchResolved = false;
            SetupGitWatcherCore();
            callbacks = _branchChangeCallbacks.ToList();
        }

        Notify(callbacks);
    }

    public void RefreshGitBranch()
    {
        List<Action>? callbacks = null;
        lock (_sync)
        {
            ThrowIfDisposed();
            var nextBranch = ResolveGitBranch();
            if (!_branchResolved || string.Equals(_cachedBranch, nextBranch, StringComparison.Ordinal))
            {
                _cachedBranch = nextBranch;
                _branchResolved = true;
                return;
            }

            _cachedBranch = nextBranch;
            _branchResolved = true;
            callbacks = _branchChangeCallbacks.ToList();
        }

        Notify(callbacks);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _branchChangeCallbacks.Clear();
            ClearGitWatcherCore();
        }
    }

    internal static CodingAgentGitPaths? FindGitPaths(string cwd)
    {
        var directory = NormalizeDirectory(cwd);
        while (true)
        {
            var gitPath = Path.Combine(directory, ".git");
            try
            {
                if (Directory.Exists(gitPath))
                {
                    var headPath = Path.Combine(gitPath, "HEAD");
                    return File.Exists(headPath)
                        ? new CodingAgentGitPaths(directory, gitPath, headPath)
                        : null;
                }

                if (File.Exists(gitPath))
                {
                    var content = File.ReadAllText(gitPath).Trim();
                    const string gitDirPrefix = "gitdir: ";
                    if (!content.StartsWith(gitDirPrefix, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var gitDir = ResolveFrom(directory, content[gitDirPrefix.Length..].Trim());
                    var headPath = Path.Combine(gitDir, "HEAD");
                    if (!File.Exists(headPath))
                    {
                        return null;
                    }

                    var commonDirPath = Path.Combine(gitDir, "commondir");
                    var commonGitDir = File.Exists(commonDirPath)
                        ? ResolveFrom(gitDir, File.ReadAllText(commonDirPath).Trim())
                        : gitDir;
                    return new CodingAgentGitPaths(directory, commonGitDir, headPath);
                }
            }
            catch
            {
                return null;
            }

            var parent = Directory.GetParent(directory)?.FullName;
            if (parent is null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            directory = parent;
        }
    }

    private static string ResolveFrom(string directory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(directory, path));

    private static string NormalizeDirectory(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            cwd = Environment.CurrentDirectory;
        }

        return Path.GetFullPath(cwd);
    }

    private void SetupGitWatcher()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            SetupGitWatcherCore();
        }
    }

    private void SetupGitWatcherCore()
    {
        if (_gitPaths is null)
        {
            return;
        }

        try
        {
            var headDirectory = Path.GetDirectoryName(_gitPaths.HeadPath);
            if (string.IsNullOrWhiteSpace(headDirectory))
            {
                return;
            }

            _headWatcher = CodingAgentFsWatch.WatchWithErrorHandler(
                headDirectory,
                (_, fileName) =>
                {
                    if (string.IsNullOrWhiteSpace(fileName) || fileName.Equals("HEAD", StringComparison.Ordinal))
                    {
                        ScheduleRefresh();
                    }
                },
                HandleGitWatcherError);

            if (ShouldPollGitHead(_gitPaths.RepoDir))
            {
                _lastHeadSnapshot = GetFileSnapshot(_gitPaths.HeadPath);
                _headPollingTimer = new Timer(_ => PollGitHead(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }

            if (Directory.Exists(_gitPaths.ReftableDir))
            {
                _reftableWatcher = CodingAgentFsWatch.WatchWithErrorHandler(
                    _gitPaths.ReftableDir,
                    (_, _) => ScheduleRefresh(),
                    HandleGitWatcherError);

                if (File.Exists(_gitPaths.ReftableTablesListPath))
                {
                    _reftableTablesListWatcher = CodingAgentFsWatch.WatchWithErrorHandler(
                        _gitPaths.ReftableTablesListPath,
                        (_, _) => ScheduleRefresh(),
                        HandleGitWatcherError);
                }
            }
        }
        catch
        {
            ClearGitWatcherCore();
        }
    }

    private void HandleGitWatcherError()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ClearGitWatcherCore(clearRetryTimer: false);
            _gitWatcherRetryTimer ??= new Timer(
                _ => SetupGitWatcher(),
                null,
                CodingAgentFsWatch.RetryDelayMilliseconds,
                Timeout.Infinite);
        }
    }

    private void PollGitHead()
    {
        lock (_sync)
        {
            if (_disposed || _gitPaths is null)
            {
                return;
            }

            var current = GetFileSnapshot(_gitPaths.HeadPath);
            if (current is null)
            {
                return;
            }

            if (_lastHeadSnapshot is not null && !_lastHeadSnapshot.Equals(current))
            {
                ScheduleRefresh();
            }

            _lastHeadSnapshot = current;
        }
    }

    private void ScheduleRefresh()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _refreshTimer?.Dispose();
            _refreshTimer = new Timer(_ => RefreshGitBranch(), null, WatchDebounceMilliseconds, Timeout.Infinite);
        }
    }

    private void ClearGitWatcherCore(bool clearRetryTimer = true)
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _headPollingTimer?.Dispose();
        _headPollingTimer = null;
        _lastHeadSnapshot = null;
        CodingAgentFsWatch.CloseWatcher(_headWatcher);
        _headWatcher = null;
        CodingAgentFsWatch.CloseWatcher(_reftableWatcher);
        _reftableWatcher = null;
        CodingAgentFsWatch.CloseWatcher(_reftableTablesListWatcher);
        _reftableTablesListWatcher = null;
        if (clearRetryTimer)
        {
            _gitWatcherRetryTimer?.Dispose();
            _gitWatcherRetryTimer = null;
        }
    }

    private string? ResolveGitBranch()
    {
        try
        {
            if (_gitPaths is null)
            {
                return null;
            }

            var content = File.ReadAllText(_gitPaths.HeadPath).Trim();
            const string branchPrefix = "ref: refs/heads/";
            if (!content.StartsWith(branchPrefix, StringComparison.Ordinal))
            {
                return "detached";
            }

            var branch = content[branchPrefix.Length..];
            return string.Equals(branch, ".invalid", StringComparison.Ordinal)
                ? ResolveBranchWithGit(_gitPaths.RepoDir) ?? "detached"
                : branch;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveBranchWithGit(string repoDir)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.StartInfo.ArgumentList.Add("--no-optional-locks");
            process.StartInfo.ArgumentList.Add("symbolic-ref");
            process.StartInfo.ArgumentList.Add("--quiet");
            process.StartInfo.ArgumentList.Add("--short");
            process.StartInfo.ArgumentList.Add("HEAD");
            process.Start();
            if (!process.WaitForExit(1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort fallback: missing git or a stuck git process should not break footer rendering.
                }

                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool ShouldPollGitHead(
        string repoDir,
        IReadOnlyDictionary<string, string?>? environment = null,
        PlatformID? platform = null) =>
        IsWslEnvironment(environment, platform) && IsWindowsMountedRepoPath(repoDir);

    internal static bool IsWslEnvironment(
        IReadOnlyDictionary<string, string?>? environment = null,
        PlatformID? platform = null)
    {
        platform ??= Environment.OSVersion.Platform;
        if (platform is not PlatformID.Unix)
        {
            return false;
        }

        environment ??= CaptureEnvironment();
        return Has(environment, "WSL_DISTRO_NAME") || Has(environment, "WSL_INTEROP");
    }

    internal static bool IsWindowsMountedRepoPath(string repoDir)
    {
        if (string.IsNullOrWhiteSpace(repoDir))
        {
            return false;
        }

        var normalized = repoDir.Replace('\\', '/');
        return normalized.Length >= 7 &&
            normalized.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) &&
            char.IsLetter(normalized[5]) &&
            normalized[6] == '/';
    }

    private static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            values[(string)entry.Key] = entry.Value?.ToString();
        }

        return values;
    }

    private static bool Has(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value);

    private static CodingAgentFileSnapshot? GetFileSnapshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new CodingAgentFileSnapshot(info.LastWriteTimeUtc, info.CreationTimeUtc, info.Length)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void Unsubscribe(Action callback)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _branchChangeCallbacks.Remove(callback);
            }
        }
    }

    private static void Notify(List<Action>? callbacks)
    {
        if (callbacks is null)
        {
            return;
        }

        foreach (var callback in callbacks)
        {
            callback();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CodingAgentFooterDataProvider));
        }
    }

    private sealed class Subscription(CodingAgentFooterDataProvider owner, Action callback) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Unsubscribe(callback);
        }
    }
}

public sealed record CodingAgentGitPaths(string RepoDir, string CommonGitDir, string HeadPath)
{
    public string ReftableDir => Path.Combine(CommonGitDir, "reftable");
    public string ReftableTablesListPath => Path.Combine(ReftableDir, "tables.list");
}

internal readonly record struct CodingAgentFileSnapshot(DateTime LastWriteTimeUtc, DateTime CreationTimeUtc, long Length);
