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
        CancellationToken cancellationToken = default)
    {
        var sessionId = id ?? SessionRepoUtilities.CreateSessionId();
        var createdAt = SessionRepoUtilities.CreateTimestamp();
        var sessionDirectory = GetSessionDirectory(cwd);
        Directory.CreateDirectory(sessionDirectory);
        var filePath = CreateSessionFilePath(sessionDirectory, sessionId, createdAt);
        var storage = await JsonlSessionStorage.CreateAsync(
            filePath,
            cwd,
            sessionId,
            parentSessionPath,
            cancellationToken).ConfigureAwait(false);
        return new AgentHarnessSession<JsonlSessionMetadata>(storage);
    }

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
            parentSessionPath ?? sourceMetadata.Path,
            cancellationToken).ConfigureAwait(false);
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
}
