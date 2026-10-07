using Tau.AgentCore.Harness.Session;

namespace Tau.CodingAgent.Runtime;

internal sealed record CodingAgentSessionTarget(
    CodingAgentSessionStore? SessionStore,
    CodingAgentTreeSessionController? TreeSessionController,
    bool PreferTreeSession)
{
    /// <summary>【CodingAgent】【会话选择】按显式路径、目录和继续标志选择会话，普通启动创建独立 JSONL。</summary>
    /// <param name="explicitSessionPath">显式文件路径或会话 ID 前缀。</param>
    /// <param name="continueRecent">是否继续当前工作目录最近会话。</param>
    /// <param name="sessionDirectory">可选会话目录。</param>
    /// <param name="forkSessionPath">可选分叉来源。</param>
    /// <param name="noSession">是否禁用持久化。</param>
    /// <param name="workingDirectory">会话工作目录，默认进程目录。</param>
    /// <param name="agentDirectory">默认会话目录所属的 Agent 目录。</param>
    /// <returns>选定的会话存储。</returns>
    public static CodingAgentSessionTarget Resolve(
        string? explicitSessionPath,
        bool continueRecent = false,
        string? sessionDirectory = null,
        string? forkSessionPath = null,
        bool noSession = false,
        string? workingDirectory = null,
        string? agentDirectory = null)
    {
        if (noSession)
        {
            return new CodingAgentSessionTarget(null, null, false);
        }
        var cwd = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);

        if (!string.IsNullOrWhiteSpace(forkSessionPath))
        {
            var sourcePath = ResolveForkSessionPath(forkSessionPath, sessionDirectory);
            var path = ForkSession(sourcePath, sessionDirectory ?? GetDefaultSessionDirectory(cwd, agentDirectory));
            return OpenTree(path, cwd);
        }

        if (!string.IsNullOrWhiteSpace(explicitSessionPath))
        {
            var path = LooksLikeSessionPath(explicitSessionPath)
                ? Path.GetFullPath(explicitSessionPath.Trim(), cwd)
                : ResolveExplicitSessionPath(explicitSessionPath, sessionDirectory);
            return CodingAgentTreeSessionStore.IsJsonlPath(path)
                ? OpenTree(path, cwd)
                : new CodingAgentSessionTarget(new CodingAgentSessionStore(path), null, false);
        }

        if (!string.IsNullOrWhiteSpace(sessionDirectory))
        {
            var path = continueRecent
                ? FindMostRecentSession(sessionDirectory, cwd) ?? CreateSessionPath(sessionDirectory)
                : CreateSessionPath(sessionDirectory);
            return OpenTree(path, cwd);
        }

        // 1. 【CodingAgent】【会话选择】环境指定文件仍视为显式恢复；不再自动续接旧的固定默认文件
        var configuredPath = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE");
        if (string.IsNullOrWhiteSpace(configuredPath))
            configuredPath = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Path.GetFullPath(configuredPath, cwd);
            return CodingAgentTreeSessionStore.IsJsonlPath(path)
                ? OpenTree(path, cwd)
                : new CodingAgentSessionTarget(new CodingAgentSessionStore(path), null, false);
        }

        // 2. 【CodingAgent】【会话选择】默认目录按 cwd 隔离，只有显式 continue 才选择已有文件
        var directory = GetDefaultSessionDirectory(cwd, agentDirectory);
        var selectedPath = continueRecent ? FindMostRecentSession(directory, cwd) : null;
        return OpenTree(selectedPath ?? CreateSessionPath(directory), cwd);
    }

    /// <summary>【CodingAgent】【会话目录】按上游 cwd 编码规则计算 Agent 目录下的会话目录，不创建文件。</summary>
    /// <param name="workingDirectory">工作目录，默认进程目录。</param>
    /// <param name="agentDirectory">Agent 根目录，默认用户的 .tau。</param>
    /// <returns>当前项目专属会话目录。</returns>
    internal static string GetDefaultSessionDirectory(string? workingDirectory = null, string? agentDirectory = null)
    {
        var cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory));
        var agent = Path.GetFullPath(agentDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tau"));
        var safePath = cwd.StartsWith('/') || cwd.StartsWith('\\') ? cwd[1..] : cwd;
        safePath = safePath.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
        return Path.Combine(agent, "sessions", $"--{safePath}--");
    }

    /// <summary>【CodingAgent】【会话选择】打开带正确工作目录的树会话。</summary>
    /// <param name="path">会话文件。</param>
    /// <param name="cwd">会话工作目录。</param>
    /// <returns>只使用 JSONL 的会话目标。</returns>
    private static CodingAgentSessionTarget OpenTree(string path, string cwd) =>
        new(null, new CodingAgentTreeSessionController(new CodingAgentTreeSessionStore(path, cwd)), true);

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

    /// <summary>【CodingAgent】【继续会话】过滤共享目录内其他工作目录的会话。</summary>
    /// <param name="sessionDirectory">待扫描目录。</param>
    /// <param name="cwd">当前工作目录。</param>
    /// <returns>最近的匹配会话路径，没有则为空。</returns>
    private static string? FindMostRecentSession(string sessionDirectory, string cwd)
    {
        return ListSessionsInDirectory(sessionDirectory)
            // 1. 【CodingAgent】【继续会话】新建但尚未运行的空文件不能遮蔽最近的有效对话
            .Where(static session => session.MessageCount > 0)
            .FirstOrDefault(session => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.Cwd)),
                Path.TrimEndingDirectorySeparator(cwd),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            ?.FilePath;
    }

    private static string CreateSessionPath(string sessionDirectory)
    {
        var directory = Path.GetFullPath(sessionDirectory.Trim());
        Directory.CreateDirectory(directory);

        string path;
        do
        {
            path = Path.Combine(directory, $"{UuidV7.Create()}.jsonl");
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

    /// <summary>【CodingAgent】【会话分叉】取得当前项目的默认会话目录。</summary>
    /// <returns>分叉会话的目标目录。</returns>
    private static string GetDefaultForkSessionDirectory()
    {
        return GetDefaultSessionDirectory();
    }
}
