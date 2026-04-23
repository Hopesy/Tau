using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Tests;

public sealed class ProviderAuthResolverTests
{
    [Fact]
    public void ResolveApiKey_PrefersExplicitValue()
    {
        var resolver = new ProviderAuthResolver();

        var result = resolver.ResolveApiKey("openai", "explicit-key");

        Assert.Equal("explicit-key", result);
    }

    [Fact]
    public void ResolveApiKey_UsesAuthFileApiKeyEntry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "openrouter": {
                    "type": "api_key",
                    "key": "or-key"
                  }
                }
                """);

            var resolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]));

            var result = resolver.ResolveApiKey("openrouter");

            Assert.Equal("or-key", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveApiKey_RefreshesExpiredOAuthCredentials()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "custom-oauth": {
                    "type": "oauth",
                    "refresh": "refresh-token",
                    "access": "expired-access",
                    "expiresAt": "2000-01-01T00:00:00Z"
                  }
                }
                """);

            var oauthRegistry = new OAuthProviderRegistry([new StubOAuthProvider()]);
            var resolver = new ProviderAuthResolver(
                oauthRegistry,
                new OAuthCredentialStore([authPath]));

            var result = resolver.ResolveApiKey("custom-oauth");

            Assert.Equal("refreshed-access", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveModel_AppliesOAuthModelMutation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "custom-oauth": {
                    "type": "oauth",
                    "refresh": "refresh-token",
                    "access": "access-token",
                    "expiresAt": "2030-01-01T00:00:00Z"
                  }
                }
                """);

            var oauthRegistry = new OAuthProviderRegistry([new StubOAuthProvider()]);
            var resolver = new ProviderAuthResolver(
                oauthRegistry,
                new OAuthCredentialStore([authPath]));

            var model = new Model
            {
                Id = "model-1",
                Name = "Model 1",
                Api = "openai-responses",
                Provider = "custom-oauth",
                BaseUrl = "https://original.example.com"
            };

            var resolved = resolver.ResolveModel(model);

            Assert.Equal("https://mutated.example.com", resolved.BaseUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubOAuthProvider : IOAuthProvider
    {
        public string Id => "custom-oauth";
        public string Name => "Custom OAuth";

        public Task<OAuthCredentials> LoginAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(credentials with
            {
                Access = "refreshed-access",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
        }

        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;

        public Model ModifyModel(Model model, OAuthCredentials credentials) =>
            model with { BaseUrl = "https://mutated.example.com" };
    }
}
