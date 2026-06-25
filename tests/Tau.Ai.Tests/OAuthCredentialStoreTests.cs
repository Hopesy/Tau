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

    [Fact]
    public void LoadEntries_ParsesApiKeyEntryEnvironment()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "cloudflare-ai-gateway": {
                    "type": "api_key",
                    "key": "cf-key",
                    "env": {
                      "CLOUDFLARE_ACCOUNT_ID": "acct_123",
                      "CLOUDFLARE_GATEWAY_ID": "gw_456"
                    }
                  }
                }
                """);

            var store = new OAuthCredentialStore([authPath]);

            var entry = store.LoadEntries()["cloudflare-ai-gateway"];

            Assert.Equal("cf-key", entry.ApiKey);
            Assert.Equal("acct_123", entry.Env!["CLOUDFLARE_ACCOUNT_ID"]);
            Assert.Equal("gw_456", entry.Env["CLOUDFLARE_GATEWAY_ID"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadEntries_WhenAuthFileIsInvalid_ReturnsEmptyEntries()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{ invalid json");

            var store = new OAuthCredentialStore([authPath]);

            var entries = store.LoadEntries();

            Assert.Empty(entries);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Remove_RemovesMatchingProviderAndPreservesOtherEntries()
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

            var removed = store.Remove("ANTHROPIC");
            var entries = store.LoadEntries();

            Assert.True(removed);
            Assert.False(entries.ContainsKey("anthropic"));
            Assert.Equal("or-key", entries["openrouter"].ApiKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Remove_WhenProviderMissing_ReturnsFalseAndPreservesFile()
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
            var before = File.ReadAllText(authPath);
            var store = new OAuthCredentialStore([authPath]);

            var removed = store.Remove("anthropic");

            Assert.False(removed);
            Assert.Equal(before, File.ReadAllText(authPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
