using System.Collections.Generic;

namespace Tau.Ai.Observability;

public sealed record TauRuntimeLogContext(
    string? CorrelationId = null,
    string? SessionId = null,
    string? MessageId = null)
{
    public TauRuntimeLogContext EnsureCorrelationId() =>
        string.IsNullOrWhiteSpace(CorrelationId)
            ? this with { CorrelationId = Guid.NewGuid().ToString("N") }
            : this with { CorrelationId = CorrelationId.Trim() };

    public void AddTo(IDictionary<string, string?> fields)
    {
        AddField(fields, "correlationId", CorrelationId);
        AddField(fields, "sessionId", SessionId);
        AddField(fields, "messageId", MessageId);
    }

    private static void AddField(IDictionary<string, string?> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[name] = value.Trim();
        }
    }
}
