namespace Tau.Ai.Auth.OAuth;

public interface IOAuthProvider
{
    string Id { get; }
    string Name { get; }

    Task<OAuthCredentials> LoginAsync(CancellationToken cancellationToken = default);
    Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default);
    string GetApiKey(OAuthCredentials credentials);
    Model ModifyModel(Model model, OAuthCredentials credentials) => model;
}
