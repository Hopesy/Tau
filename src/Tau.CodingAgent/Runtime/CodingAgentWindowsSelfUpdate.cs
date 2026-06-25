using System.Diagnostics;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentWindowsSelfUpdate
{
    public const string QuarantineDirectoryName = ".pi-native-quarantine";

    public static void CleanupQuarantine(string packageDirectory)
    {
        var quarantineRoot = GetQuarantineRoot(packageDirectory);
        if (quarantineRoot is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(quarantineRoot))
            {
                Directory.Delete(quarantineRoot, recursive: true);
            }
        }
        catch
        {
            // A previous process may still be exiting and holding a native dependency.
        }
    }

    public static void QuarantineNativeDependencies(
        string packageDirectory,
        Func<string, IReadOnlyList<string>>? loadedFilesProvider = null)
    {
        var resolvedPackageDirectory = Path.GetFullPath(packageDirectory);
        var quarantineRoot = GetQuarantineRoot(resolvedPackageDirectory);
        if (quarantineRoot is null)
        {
            return;
        }

        var loadedFiles = loadedFilesProvider?.Invoke(resolvedPackageDirectory) ??
            GetLoadedNativeDependenciesInPackageDirectory(resolvedPackageDirectory);
        if (loadedFiles.Count == 0)
        {
            return;
        }

        var runDirectory = Path.Combine(
            quarantineRoot,
            $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        foreach (var loadedFile in loadedFiles)
        {
            if (!File.Exists(loadedFile) ||
                !IsPathUnder(loadedFile, resolvedPackageDirectory))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(resolvedPackageDirectory, Path.GetFullPath(loadedFile));
            var quarantinePath = Path.Combine(runDirectory, relativePath);
            var quarantineDirectory = Path.GetDirectoryName(quarantinePath);
            if (!string.IsNullOrWhiteSpace(quarantineDirectory))
            {
                Directory.CreateDirectory(quarantineDirectory);
            }

            File.Move(loadedFile, quarantinePath);
            File.Copy(quarantinePath, loadedFile);
        }
    }

    internal static string? GetQuarantineRoot(string packageDirectory)
    {
        var current = Path.GetFullPath(packageDirectory);
        while (true)
        {
            if (Path.GetFileName(current).Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(current, QuarantineDirectoryName);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current = parent;
        }
    }

    internal static IReadOnlyList<string> GetLoadedNativeDependenciesInPackageDirectory(string packageDirectory)
    {
        try
        {
            var root = Path.GetFullPath(packageDirectory);
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var fileName = module.FileName;
                if (string.IsNullOrWhiteSpace(fileName) ||
                    !IsPathUnder(fileName, root) ||
                    !seen.Add(Path.GetFullPath(fileName)))
                {
                    continue;
                }

                results.Add(Path.GetFullPath(fileName));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static bool IsPathUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
