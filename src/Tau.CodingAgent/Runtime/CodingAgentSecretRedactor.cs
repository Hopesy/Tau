using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// Thin shim retained for backwards compatibility with callers that still
/// reference <see cref="CodingAgentSecretRedactor"/>. Delegates to the shared
/// <see cref="TauSecretRedactor"/> in Tau.Ai so both the coding agent and the
/// Other exporters use the same patterns.
/// </summary>
public sealed class CodingAgentSecretRedactor
{
    public const string Placeholder = TauSecretRedactor.Placeholder;

    public static readonly CodingAgentSecretRedactor Default = new(IsDefaultEnabled());

    private readonly TauSecretRedactor _inner;

    public CodingAgentSecretRedactor(bool enabled)
    {
        _inner = new TauSecretRedactor(enabled);
    }

    public bool Enabled => _inner.Enabled;

    public string Redact(string? input) => _inner.Redact(input);

    public static bool IsDefaultEnabled()
    {
        return IsEnabledFromEnvironment(Environment.GetEnvironmentVariable(TauSecretRedactor.CodingAgentEnvironmentVariable));
    }

    internal static bool IsEnabledFromEnvironment(string? raw)
        => TauSecretRedactor.IsEnabledFromEnvironment(raw);
}
