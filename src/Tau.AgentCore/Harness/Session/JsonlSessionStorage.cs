namespace Tau.AgentCore.Harness.Session;

public sealed class JsonlSessionStorage : ISessionStorage<JsonlSessionMetadata>
{
    private readonly string _filePath;
    private readonly InMemorySessionStorage<JsonlSessionMetadata> _storage;

    private JsonlSessionStorage(
        string filePath,
        JsonlSessionMetadata metadata,
        IEnumerable<SessionTreeEntry> entries)
    {
        _filePath = filePath;
        _storage = new InMemorySessionStorage<JsonlSessionMetadata>(metadata, entries);
    }

    public static async Task<JsonlSessionStorage> CreateAsync(
        string filePath,
        string cwd,
        string sessionId,
        string? parentSessionPath = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(filePath);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var timestamp = SessionRepoUtilities.CreateTimestamp();
        var header = new JsonlSessionHeader(
            "session",
            JsonlSessionSerialization.CurrentVersion,
            sessionId,
            timestamp,
            cwd,
            parentSessionPath);
        await File.WriteAllTextAsync(
            fullPath,
            JsonlSessionSerialization.SerializeHeader(header) + "\n",
            cancellationToken).ConfigureAwait(false);
        return new JsonlSessionStorage(fullPath, ToMetadata(header, fullPath), []);
    }

    public static async Task<JsonlSessionStorage> OpenAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        return new JsonlSessionStorage(loaded.FilePath, ToMetadata(loaded.Header, loaded.FilePath), loaded.Entries);
    }

    public static async Task<JsonlSessionMetadata> LoadMetadataAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(filePath);
        try
        {
            await using var stream = File.OpenRead(fullPath);
            using var reader = new StreamReader(stream);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(line))
                return ToMetadata(JsonlSessionSerialization.ParseHeader(line, fullPath), fullPath);
        }
        catch (FileNotFoundException ex)
        {
            throw new SessionException("not_found", $"Session not found: {fullPath}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new SessionException("not_found", $"Session not found: {fullPath}", ex);
        }

        throw new SessionException("invalid_session", $"Invalid JSONL session file {fullPath}: missing session header");
    }

    public Task<JsonlSessionMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        _storage.GetMetadataAsync(cancellationToken);

    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) =>
        _storage.GetLeafIdAsync(cancellationToken);

    public async Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default)
    {
        if (leafId is not null && await _storage.GetEntryAsync(leafId, cancellationToken).ConfigureAwait(false) is null)
            throw new SessionException("not_found", $"Entry {leafId} not found");

        var entry = new LeafSessionEntry(
            await _storage.CreateEntryIdAsync(cancellationToken).ConfigureAwait(false),
            await _storage.GetLeafIdAsync(cancellationToken).ConfigureAwait(false),
            DateTimeOffset.UtcNow,
            leafId);
        await AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default) =>
        _storage.CreateEntryIdAsync(cancellationToken);

    public async Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default)
    {
        await File.AppendAllTextAsync(
            _filePath,
            JsonlSessionSerialization.SerializeEntry(entry) + "\n",
            cancellationToken).ConfigureAwait(false);
        await _storage.AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetEntryAsync(id, cancellationToken);

    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(
        string type,
        CancellationToken cancellationToken = default) =>
        _storage.FindEntriesAsync(type, cancellationToken);

    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) =>
        _storage.GetLabelAsync(id, cancellationToken);

    public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(
        string? leafId,
        CancellationToken cancellationToken = default) =>
        _storage.GetPathToRootAsync(leafId, cancellationToken);

    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        _storage.GetEntriesAsync(cancellationToken);

    private static async Task<(string FilePath, JsonlSessionHeader Header, IReadOnlyList<SessionTreeEntry> Entries)> LoadAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var fullPath = System.IO.Path.GetFullPath(filePath);
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new SessionException("not_found", $"Session not found: {fullPath}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new SessionException("not_found", $"Session not found: {fullPath}", ex);
        }

        var nonEmptyLines = lines.Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (nonEmptyLines.Length == 0)
            throw new SessionException("invalid_session", $"Invalid JSONL session file {fullPath}: missing session header");

        var header = JsonlSessionSerialization.ParseHeader(nonEmptyLines[0], fullPath);
        var entries = new List<SessionTreeEntry>();
        for (var i = 1; i < nonEmptyLines.Length; i++)
        {
            entries.Add(JsonlSessionSerialization.ParseEntry(nonEmptyLines[i], fullPath, i + 1));
        }

        return (fullPath, header, entries);
    }

    private static JsonlSessionMetadata ToMetadata(JsonlSessionHeader header, string filePath) =>
        new(
            header.Id,
            header.Timestamp,
            header.Cwd,
            filePath,
            header.ParentSession);
}
