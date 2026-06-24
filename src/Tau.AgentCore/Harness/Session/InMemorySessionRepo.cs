using Tau.Ai;

namespace Tau.AgentCore.Harness.Session;

public sealed class InMemorySessionRepo
{
    private readonly Dictionary<string, AgentHarnessSession<SessionMetadata>> _sessions = new(StringComparer.Ordinal);

    public Task<AgentHarnessSession<SessionMetadata>> CreateAsync(
        string? id = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = new SessionMetadata(id ?? CreateSessionId(), CreateTimestamp());
        var session = new AgentHarnessSession<SessionMetadata>(
            new InMemorySessionStorage<SessionMetadata>(metadata));
        _sessions[metadata.Id] = session;
        return Task.FromResult(session);
    }

    public Task<AgentHarnessSession<SessionMetadata>> OpenAsync(
        SessionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(metadata.Id, out var session))
            throw new SessionException("not_found", $"Session not found: {metadata.Id}");

        return Task.FromResult(session);
    }

    public async Task<IReadOnlyList<SessionMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        var metadata = new List<SessionMetadata>(_sessions.Count);
        foreach (var session in _sessions.Values)
        {
            metadata.Add(await session.GetMetadataAsync(cancellationToken).ConfigureAwait(false));
        }

        return metadata;
    }

    public Task DeleteAsync(SessionMetadata metadata, CancellationToken cancellationToken = default)
    {
        _sessions.Remove(metadata.Id);
        return Task.CompletedTask;
    }

    public async Task<AgentHarnessSession<SessionMetadata>> ForkAsync(
        SessionMetadata sourceMetadata,
        SessionForkOptions options,
        CancellationToken cancellationToken = default)
    {
        var source = await OpenAsync(sourceMetadata, cancellationToken).ConfigureAwait(false);
        var forkedEntries = await GetEntriesToForkAsync(
            source.GetStorage(),
            options,
            cancellationToken).ConfigureAwait(false);
        var metadata = new SessionMetadata(options.Id ?? CreateSessionId(), CreateTimestamp());
        var session = new AgentHarnessSession<SessionMetadata>(
            new InMemorySessionStorage<SessionMetadata>(metadata, forkedEntries));
        _sessions[metadata.Id] = session;
        return session;
    }

    private static async Task<IReadOnlyList<SessionTreeEntry>> GetEntriesToForkAsync(
        ISessionStorage<SessionMetadata> storage,
        SessionForkOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.EntryId))
            return await storage.GetEntriesAsync(cancellationToken).ConfigureAwait(false);

        var target = await storage.GetEntryAsync(options.EntryId, cancellationToken).ConfigureAwait(false);
        if (target is null)
            throw new SessionException("invalid_fork_target", $"Entry {options.EntryId} not found");

        string? effectiveLeafId;
        if (string.Equals(options.Position, "at", StringComparison.Ordinal))
        {
            effectiveLeafId = target.Id;
        }
        else
        {
            if (target is not MessageSessionEntry { Message: UserMessage })
                throw new SessionException("invalid_fork_target", $"Entry {options.EntryId} is not a user message");

            effectiveLeafId = target.ParentId;
        }

        return await storage.GetPathToRootAsync(effectiveLeafId, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateSessionId() => UuidV7.Create();

    private static string CreateTimestamp() =>
        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
