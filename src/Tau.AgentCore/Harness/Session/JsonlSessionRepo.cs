using System.Text.Json;

namespace Tau.AgentCore.Harness.Session;

public sealed class JsonlSessionRepo
{
    private readonly string _sessionsRoot;

    public JsonlSessionRepo(string sessionsRoot)
    {
        _sessionsRoot = System.IO.Path.GetFullPath(sessionsRoot);
    }

    public async Task<AgentHarnessSession<JsonlSessionMetadata>> CreateAsync(
        string cwd,
        string? id = null,
        string? parentSessionPath = null,
        string? parentSessionId = null,
        JsonElement? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = id ?? SessionRepoUtilities.CreateSessionId();
        ValidateSessionId(sessionId);
        var createdAt = SessionRepoUtilities.CreateTimestamp();
        var sessionDirectory = GetSessionDirectory(cwd);
        Directory.CreateDirectory(sessionDirectory);
        if (Directory.EnumerateFiles(sessionDirectory, $"*_{sessionId}.jsonl").Any())
            throw new SessionException("already_exists", $"Session already exists: {sessionId}");
        var filePath = CreateSessionFilePath(sessionDirectory, sessionId, createdAt);
        var storage = await JsonlSessionStorage.CreateAsync(
            filePath,
            cwd,
            sessionId,
            parentSessionPath,
            parentSessionId,
            metadata,
            cancellationToken).ConfigureAwait(false);
        return new AgentHarnessSession<JsonlSessionMetadata>(storage);
    }

    /// <summary>
    /// 保留旧版参数顺序的 session 创建入口，避免旧调用把取消令牌误解析为 parentSessionId。
    /// </summary>
    /// <param name="cwd">session 工作目录。</param>
    /// <param name="id">可选的 session id。</param>
    /// <param name="parentSessionPath">旧版父 session 文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已创建的 JSONL harness session。</returns>
    public Task<AgentHarnessSession<JsonlSessionMetadata>> CreateAsync(
        string cwd,
        string? id,
        string? parentSessionPath,
        CancellationToken cancellationToken) =>
        CreateAsync(
            cwd,
            id,
            parentSessionPath,
            parentSessionId: null,
            metadata: null,
            cancellationToken: cancellationToken);

    public async Task<AgentHarnessSession<JsonlSessionMetadata>> OpenAsync(
        JsonlSessionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(metadata.Path))
            throw new SessionException("not_found", $"Session not found: {metadata.Path}");

        return new AgentHarnessSession<JsonlSessionMetadata>(
            await JsonlSessionStorage.OpenAsync(metadata.Path, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<JsonlSessionMetadata>> ListAsync(
        string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        var directories = cwd is null
            ? ListSessionDirectories()
            : [GetSessionDirectory(cwd)];
        var sessions = new List<JsonlSessionMetadata>();

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var filePath in Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                try
                {
                    sessions.Add(await JsonlSessionStorage.LoadMetadataAsync(filePath, cancellationToken).ConfigureAwait(false));
                }
                catch (SessionException ex) when (ex.Code == "invalid_session")
                {
                }
            }
        }

        return sessions
            .OrderByDescending(static metadata => DateTimeOffset.TryParse(metadata.CreatedAt, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue)
            .ToArray();
    }

    public Task DeleteAsync(JsonlSessionMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (File.Exists(metadata.Path))
            File.Delete(metadata.Path);

        return Task.CompletedTask;
    }

    public async Task<AgentHarnessSession<JsonlSessionMetadata>> ForkAsync(
        JsonlSessionMetadata sourceMetadata,
        string cwd,
        SessionForkOptions options,
        string? parentSessionPath = null,
        CancellationToken cancellationToken = default)
    {
        var source = await OpenAsync(sourceMetadata, cancellationToken).ConfigureAwait(false);
        var forkedEntries = await SessionRepoUtilities.GetEntriesToForkAsync(
            source.GetStorage(),
            options,
            cancellationToken).ConfigureAwait(false);
        var session = await CreateAsync(
            cwd,
            options.Id,
            parentSessionPath,
            parentSessionId: parentSessionPath is null ? sourceMetadata.Id : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var entry in forkedEntries)
        {
            await session.GetStorage().AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    private string GetSessionDirectory(string cwd) =>
        System.IO.Path.Combine(_sessionsRoot, EncodeCwd(cwd));

    private string[] ListSessionDirectories() =>
        Directory.Exists(_sessionsRoot)
            ? Directory.EnumerateDirectories(_sessionsRoot).ToArray()
            : [];

    private static string CreateSessionFilePath(string sessionDirectory, string sessionId, string createdAt) =>
        System.IO.Path.Combine(sessionDirectory, $"{createdAt.Replace(':', '-').Replace('.', '-')}_{sessionId}.jsonl");

    private static string EncodeCwd(string cwd)
    {
        var trimmed = cwd.TrimStart('/', '\\');
        var encoded = new string(trimmed
            .Select(static character => character is '/' or '\\' or ':' ? '-' : character)
            .ToArray());
        return $"--{encoded}--";
    }

    /// <summary>校验 v4 session id 是否可安全用于 JSONL 文件名。</summary>
    /// <param name="id">待校验的 session id。</param>
    private static void ValidateSessionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            id[0] is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') ||
            id[^1] is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') ||
            id.Any(static character => !char.IsLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new SessionException("invalid_payload", "Session id must start and end with an alphanumeric character and contain only alphanumeric characters, '-', '_', and '.'.");
        }
    }
}
