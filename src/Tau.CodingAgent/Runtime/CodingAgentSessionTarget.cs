namespace Tau.CodingAgent.Runtime;

internal sealed record CodingAgentSessionTarget(
    CodingAgentSessionStore? SessionStore,
    CodingAgentTreeSessionController? TreeSessionController,
    bool PreferTreeSession)
{
    public static CodingAgentSessionTarget Resolve(
        string? explicitSessionPath,
        bool continueRecent = false,
        string? sessionDirectory = null,
        string? forkSessionPath = null,
        bool noSession = false)
    {
        if (noSession)
        {
            return new CodingAgentSessionTarget(null, null, false);
        }

        if (!string.IsNullOrWhiteSpace(forkSessionPath))
        {
            var sourcePath = ResolveForkSessionPath(forkSessionPath, sessionDirectory);
            var path = ForkSession(sourcePath, sessionDirectory);
            return new CodingAgentSessionTarget(null, CodingAgentTreeSessionController.OpenOrCreate(path), true);
        }

        if (!string.IsNullOrWhiteSpace(explicitSessionPath))
        {
            var path = ResolveExplicitSessionPath(explicitSessionPath, sessionDirectory);
            return CodingAgentTreeSessionStore.IsJsonlPath(path)
                ? new CodingAgentSessionTarget(null, CodingAgentTreeSessionController.OpenOrCreate(path), true)
                : new CodingAgentSessionTarget(new CodingAgentSessionStore(path), null, false);
        }

        if (!string.IsNullOrWhiteSpace(sessionDirectory))
        {
            var path = continueRecent
                ? FindMostRecentSession(sessionDirectory) ?? CreateSessionPath(sessionDirectory)
                : CreateSessionPath(sessionDirectory);
            return new CodingAgentSessionTarget(null, CodingAgentTreeSessionController.OpenOrCreate(path), true);
        }

        if (continueRecent)
        {
            var path = CodingAgentTreeSessionStore.FindMostRecentSession() ?? CodingAgentTreeSessionStore.GetDefaultPath();
            return new CodingAgentSessionTarget(null, CodingAgentTreeSessionController.OpenOrCreate(path), true);
        }

        var sessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        var sessionStore = CodingAgentTreeSessionStore.IsJsonlPath(sessionFile)
            ? null
            : new CodingAgentSessionStore();

        return new CodingAgentSessionTarget(
            sessionStore,
            CodingAgentTreeSessionController.OpenOrCreate(),
            CodingAgentTreeSessionStore.HasExplicitTreeSessionPath);
    }

    public CodingAgentSessionSnapshot LoadInitialSnapshot()
    {
        var flatSession = SessionStore?.Load() ?? new CodingAgentSessionSnapshot([], null, null, null);
        if (TreeSessionController is null)
        {
            return flatSession;
        }

        var treeSession = TreeSessionController.LoadSnapshot();
        return PreferTreeSession || treeSession.Messages.Count > 0
            ? treeSession.ToFlatSnapshot()
            : flatSession;
    }

    public static IReadOnlyList<CodingAgentResumeSessionInfo> ListAvailableSessions(
        string? sessionDirectory = null,
        string? currentPath = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new List<CodingAgentResumeSessionInfo>();

        if (!string.IsNullOrWhiteSpace(sessionDirectory))
        {
            foreach (var session in ListSessionsInDirectory(sessionDirectory))
            {
                if (seen.Add(session.FilePath))
                {
                    sessions.Add(session);
                }
            }
        }

        foreach (var session in CodingAgentTreeSessionStore.ListAvailableSessions(currentPath))
        {
            if (seen.Add(session.FilePath))
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    private static string ResolveExplicitSessionPath(string sessionReference, string? sessionDirectory)
    {
        var trimmed = sessionReference.Trim();
        if (LooksLikeSessionPath(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        return ResolveTreeSessionReference(trimmed, sessionDirectory);
    }

    private static string ResolveForkSessionPath(string sessionReference, string? sessionDirectory)
    {
        var trimmed = sessionReference.Trim();
        if (LooksLikeSessionPath(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        return ResolveTreeSessionReference(trimmed, sessionDirectory);
    }

    private static bool LooksLikeSessionPath(string value) =>
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar) ||
        Path.HasExtension(value);

    private static string ResolveTreeSessionReference(string sessionReference, string? sessionDirectory)
    {
        var match = FindSessionByIdPrefix(sessionReference, sessionDirectory);
        if (match is null)
        {
            throw new IOException($"No session found matching '{sessionReference}'");
        }

        return match.FilePath;
    }

    private static CodingAgentResumeSessionInfo? FindSessionByIdPrefix(string sessionReference, string? sessionDirectory)
    {
        foreach (var session in EnumerateSessionLookupCandidates(sessionDirectory))
        {
            if (!string.IsNullOrWhiteSpace(session.SessionId) &&
                session.SessionId.StartsWith(sessionReference, StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }
        }

        return null;
    }

    private static IEnumerable<CodingAgentResumeSessionInfo> EnumerateSessionLookupCandidates(string? sessionDirectory)
    {
        foreach (var session in ListAvailableSessions(sessionDirectory))
        {
            yield return session;
        }
    }

    private static IReadOnlyList<CodingAgentResumeSessionInfo> ListSessionsInDirectory(string sessionDirectory)
    {
        try
        {
            var directory = Path.GetFullPath(sessionDirectory);
            if (!Directory.Exists(directory))
            {
                return [];
            }

            return [.. Directory
                .EnumerateFiles(directory, "*.jsonl")
                .Select(static path => CodingAgentTreeSessionStore.TryGetResumeSessionInfo(path))
                .Where(static info => info is not null)
                .Select(static info => info!)
                .OrderByDescending(static info => info.LastModifiedUtc)
                .ThenBy(static info => info.FilePath, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return [];
        }
    }

    private static string? FindMostRecentSession(string sessionDirectory)
    {
        return ListSessionsInDirectory(sessionDirectory)
            .FirstOrDefault()
            ?.FilePath;
    }

    private static string CreateSessionPath(string sessionDirectory)
    {
        var directory = Path.GetFullPath(sessionDirectory.Trim());
        Directory.CreateDirectory(directory);

        string path;
        do
        {
            path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.jsonl");
        }
        while (File.Exists(path));

        return path;
    }

    private static string ForkSession(string sourceSessionPath, string? sessionDirectory)
    {
        var sourcePath = Path.GetFullPath(sourceSessionPath);
        if (!File.Exists(sourcePath))
        {
            throw new IOException($"session file not found: {sourcePath}");
        }

        var targetDirectory = string.IsNullOrWhiteSpace(sessionDirectory)
            ? GetDefaultForkSessionDirectory()
            : sessionDirectory;
        var targetPath = CreateSessionPath(targetDirectory);
        var sourceStore = new CodingAgentTreeSessionStore(sourcePath);
        sourceStore.ExportCurrentBranch(targetPath);
        return targetPath;
    }

    private static string GetDefaultForkSessionDirectory()
    {
        var defaultPath = CodingAgentTreeSessionStore.GetDefaultPath();
        var defaultDirectory = Path.GetDirectoryName(defaultPath);
        return Path.Combine(
            string.IsNullOrWhiteSpace(defaultDirectory) ? Environment.CurrentDirectory : defaultDirectory,
            "coding-agent-sessions");
    }
}
