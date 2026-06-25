namespace Tau.CodingAgent.Runtime;

public delegate void CodingAgentFsWatchListener(string eventType, string? fileName);

internal static class CodingAgentFsWatch
{
    public const int RetryDelayMilliseconds = 5_000;

    public static void CloseWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Dispose();
        }
        catch
        {
            // Ignore watcher close errors.
        }
    }

    public static FileSystemWatcher? WatchWithErrorHandler(
        string path,
        CodingAgentFsWatchListener listener,
        Action onError)
    {
        try
        {
            var (directory, filter) = ResolveWatchTarget(path);
            if (!Directory.Exists(directory))
            {
                onError();
                return null;
            }

            var watcher = new FileSystemWatcher(directory, filter)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, args) => listener("change", args.Name);
            watcher.Created += (_, args) => listener("rename", args.Name);
            watcher.Deleted += (_, args) => listener("rename", args.Name);
            watcher.Renamed += (_, args) => listener("rename", args.Name);
            watcher.Error += (_, _) => onError();
            return watcher;
        }
        catch
        {
            onError();
            return null;
        }
    }

    private static (string Directory, string Filter) ResolveWatchTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Watch path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            return (fullPath, "*");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Environment.CurrentDirectory;
        }

        return (directory, Path.GetFileName(fullPath));
    }
}
