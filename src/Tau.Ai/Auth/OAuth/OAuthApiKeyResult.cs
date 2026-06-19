namespace Tau.Ai.Auth.OAuth;

public sealed record OAuthApiKeyResult(
    OAuthCredentials NewCredentials,
    string ApiKey);
