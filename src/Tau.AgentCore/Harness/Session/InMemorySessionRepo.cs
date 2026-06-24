namespace Tau.AgentCore.Harness.Session;

public sealed class InMemorySessionRepo
{
    private readonly Dictionary<string, AgentHarnessSession<SessionMetadata>> _sessions = new(StringComparer.Ordinal);

    public Task<AgentHarnessSession<SessionMetadata>> CreateAsync(
        string? id = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = new SessionMetadata(id ?? SessionRepoUtilities.CreateSessionId(), SessionRepoUtilities.CreateTimestamp());
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
        var forkedEntries = await SessionRepoUtilities.GetEntriesToForkAsync(
            source.GetStorage(),
            options,
            cancellationToken).ConfigureAwait(false);
        var metadata = new SessionMetadata(options.Id ?? SessionRepoUtilities.CreateSessionId(), SessionRepoUtilities.CreateTimestamp());
        var session = new AgentHarnessSession<SessionMetadata>(
            new InMemorySessionStorage<SessionMetadata>(metadata, forkedEntries));
        _sessions[metadata.Id] = session;
        return session;
    }
}
