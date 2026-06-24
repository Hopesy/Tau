namespace Tau.Ai.Auth.OAuth;

public sealed record OAuthProviderInfo(
    string Id,
    string Name,
    bool Available,
    bool UsesCallbackServer);
