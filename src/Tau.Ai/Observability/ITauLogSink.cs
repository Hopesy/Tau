using System.Collections.Generic;

namespace Tau.Ai.Observability;

public sealed record TauLogEvent(
    string Category,
    string Event,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string?> Fields);

public interface ITauLogSink
{
    void Log(TauLogEvent evt);
}

public sealed class NullTauLogSink : ITauLogSink
{
    public static NullTauLogSink Instance { get; } = new();
    private NullTauLogSink() { }
    public void Log(TauLogEvent evt) { }
}
