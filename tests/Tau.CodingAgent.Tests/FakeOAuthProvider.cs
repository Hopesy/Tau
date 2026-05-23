using Tau.Ai;
using Tau.Ai.Auth.OAuth;

namespace Tau.CodingAgent.Tests;

public sealed class FakeOAuthProvider : IOAuthProvider
{
    public string Id { get; init; } = "openai";
    public string Name { get; init; } = "OpenAI";
    public int LoginCalls { get; private set; }
    public IOAuthLoginCallbacks? LastCallbacks { get; private set; }
    public OAuthCredentials Credentials { get; init; } = new()
    {
        Refresh = "refresh-token",
        Access = "access-token",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    public Task<OAuthCredentials> LoginAsync(
        IOAuthLoginCallbacks callbacks,
        CancellationToken cancellationToken = default)
    {
        LastCallbacks = callbacks;
        LoginCalls++;
        return Task.FromResult(Credentials);
    }

    public Task<OAuthCredentials> RefreshTokenAsync(
        OAuthCredentials credentials,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(credentials);

    public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

    public Model ModifyModel(Model model, OAuthCredentials credentials) => model;
}
