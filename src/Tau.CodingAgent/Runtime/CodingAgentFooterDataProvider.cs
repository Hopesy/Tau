using System.Diagnostics;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentFooterDataProvider : IDisposable
{
    private const int WatchDebounceMilliseconds = 500;

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _extensionStatuses = new(StringComparer.Ordinal);
    private readonly List<Action> _branchChangeCallbacks = [];
    private string _cwd;
    private CodingAgentGitPaths? _gitPaths;
    private string? _cachedBranch;
    private bool _branchResolved;
    private int _availableProviderCount;
    private FileSystemWatcher? _headWatcher;
    private Timer? _refreshTimer;
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

    public IReadOnlyDictionary<string, string> GetExtensionStatuses()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return new Dictionary<string, string>(_extensionStatuses, StringComparer.Ordinal);
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
            if (string.IsNullOrEmpty(headDirectory) || !Directory.Exists(headDirectory))
            {
                return;
            }

            _headWatcher = new FileSystemWatcher(headDirectory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            _headWatcher.Changed += OnGitHeadChanged;
            _headWatcher.Created += OnGitHeadChanged;
            _headWatcher.Deleted += OnGitHeadChanged;
            _headWatcher.Renamed += OnGitHeadChanged;
            _headWatcher.Error += (_, _) => ScheduleRefresh();
        }
        catch
        {
            ClearGitWatcherCore();
        }
    }

    private void OnGitHeadChanged(object sender, FileSystemEventArgs args)
    {
        if (string.IsNullOrEmpty(args.Name) ||
            string.Equals(args.Name, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleRefresh();
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

    private void ClearGitWatcherCore()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        if (_headWatcher is not null)
        {
            _headWatcher.Changed -= OnGitHeadChanged;
            _headWatcher.Created -= OnGitHeadChanged;
            _headWatcher.Deleted -= OnGitHeadChanged;
            _headWatcher.Renamed -= OnGitHeadChanged;
            _headWatcher.Dispose();
            _headWatcher = null;
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

public sealed record CodingAgentGitPaths(string RepoDir, string CommonGitDir, string HeadPath);
