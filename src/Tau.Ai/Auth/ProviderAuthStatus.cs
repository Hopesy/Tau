namespace Tau.Ai.Auth;

public sealed record ProviderAuthStatus(
    string Provider,
    bool IsConfigured,
    string Source,
    bool UsesOAuth,
    bool CanLogin,
    string Message);
