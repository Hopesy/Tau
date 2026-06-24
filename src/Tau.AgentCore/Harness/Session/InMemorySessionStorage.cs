namespace Tau.AgentCore.Harness.Session;

public sealed class InMemorySessionStorage<TMetadata> : ISessionStorage<TMetadata>
    where TMetadata : SessionMetadata
{
    private readonly object _gate = new();
    private readonly TMetadata _metadata;
    private readonly List<SessionTreeEntry> _entries;
    private readonly Dictionary<string, SessionTreeEntry> _byId;
    private readonly Dictionary<string, string> _labelsById;
    private string? _leafId;

    public InMemorySessionStorage(
        TMetadata? metadata = null,
        IEnumerable<SessionTreeEntry>? entries = null)
    {
        _metadata = metadata ?? (TMetadata)(object)new SessionMetadata(UuidV7.Create(), CreateTimestamp());
        _entries = entries?.ToList() ?? [];
        _byId = _entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        _labelsById = [];

        foreach (var entry in _entries)
        {
            UpdateLabelCache(_labelsById, entry);
            _leafId = LeafIdAfterEntry(entry);
        }

        if (_leafId is not null && !_byId.ContainsKey(_leafId))
            throw new SessionException("invalid_session", $"Entry {_leafId} not found");
    }

    public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_metadata);

    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_leafId is not null && !_byId.ContainsKey(_leafId))
                throw new SessionException("invalid_session", $"Entry {_leafId} not found");

            return Task.FromResult(_leafId);
        }
    }

    public Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (leafId is not null && !_byId.ContainsKey(leafId))
                throw new SessionException("not_found", $"Entry {leafId} not found");

            var entry = new LeafSessionEntry(
                GenerateEntryId(_byId),
                _leafId,
                DateTimeOffset.UtcNow,
                leafId);
            AppendEntryCore(entry);
            _leafId = leafId;
            return Task.CompletedTask;
        }
    }

    public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(GenerateEntryId(_byId));
        }
    }

    public Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            AppendEntryCore(entry);
            return Task.CompletedTask;
        }
    }

    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _byId.TryGetValue(id, out var entry);
            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(
        string type,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SessionTreeEntry>>(
                _entries.Where(entry => entry.Type == type).ToArray());
        }
    }

    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _labelsById.TryGetValue(id, out var label);
            return Task.FromResult(label);
        }
    }

    public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(
        string? leafId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (leafId is null)
                return Task.FromResult<IReadOnlyList<SessionTreeEntry>>([]);

            var path = new List<SessionTreeEntry>();
            if (!_byId.TryGetValue(leafId, out var current))
                throw new SessionException("not_found", $"Entry {leafId} not found");

            while (current is not null)
            {
                path.Insert(0, current);
                if (current.ParentId is null)
                    break;

                if (!_byId.TryGetValue(current.ParentId, out current))
                    throw new SessionException("invalid_session", $"Entry {path[0].ParentId} not found");
            }

            return Task.FromResult<IReadOnlyList<SessionTreeEntry>>(path);
        }
    }

    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SessionTreeEntry>>(_entries.ToArray());
        }
    }

    private void AppendEntryCore(SessionTreeEntry entry)
    {
        _entries.Add(entry);
        _byId[entry.Id] = entry;
        UpdateLabelCache(_labelsById, entry);
        _leafId = LeafIdAfterEntry(entry);
    }

    private static void UpdateLabelCache(IDictionary<string, string> labelsById, SessionTreeEntry entry)
    {
        if (entry is not LabelSessionEntry labelEntry)
            return;

        var label = labelEntry.Label?.Trim();
        if (!string.IsNullOrEmpty(label))
            labelsById[labelEntry.TargetId] = label;
        else
            labelsById.Remove(labelEntry.TargetId);
    }

    private static string? LeafIdAfterEntry(SessionTreeEntry entry) =>
        entry is LeafSessionEntry leaf ? leaf.TargetId : entry.Id;

    private static string GenerateEntryId(IReadOnlyDictionary<string, SessionTreeEntry> byId)
    {
        for (var i = 0; i < 100; i++)
        {
            var id = UuidV7.Create()[..8];
            if (!byId.ContainsKey(id))
                return id;
        }

        return UuidV7.Create();
    }

    private static string CreateTimestamp() =>
        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
