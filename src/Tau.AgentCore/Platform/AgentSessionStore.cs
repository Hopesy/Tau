using Tau.Ai;

namespace Tau.AgentCore.Platform;

public sealed record AgentSessionSnapshot
{
    public required string SessionId { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? LogReference { get; init; }
}

public interface IAgentSessionStore
{
    AgentSessionSnapshot? Load(string sessionId);
    void Save(AgentSessionSnapshot snapshot);
}

public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AgentSessionSnapshot> _sessions = new(StringComparer.Ordinal);

    public AgentSessionSnapshot? Load(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var snapshot)
                ? Clone(snapshot)
                : null;
        }
    }

    public void Save(AgentSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.SessionId);

        lock (_sync)
        {
            _sessions[snapshot.SessionId] = Clone(snapshot);
        }
    }

    private static AgentSessionSnapshot Clone(AgentSessionSnapshot snapshot) =>
        snapshot with
        {
            Messages = snapshot.Messages.ToArray(),
            Metadata = new Dictionary<string, string>(snapshot.Metadata, StringComparer.Ordinal)
        };
}
