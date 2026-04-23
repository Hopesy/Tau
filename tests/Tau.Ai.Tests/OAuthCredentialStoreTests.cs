using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Tests;

public sealed class OAuthCredentialStoreTests
{
    [Fact]
    public void LoadEntries_ParsesTypedOauthAndApiKeyEntries()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "anthropic": {
                    "type": "oauth",
                    "refresh": "refresh-token",
                    "access": "access-token",
                    "expiresAt": "2030-01-01T00:00:00Z"
                  },
                  "openrouter": {
                    "type": "api_key",
                    "key": "or-key"
                  }
                }
                """);

            var store = new OAuthCredentialStore([authPath]);

            var entries = store.LoadEntries();

            Assert.Equal("access-token", entries["anthropic"].OAuth?.Access);
            Assert.Equal("or-key", entries["openrouter"].ApiKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
