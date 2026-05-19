using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;

namespace Tau.Ai.Tests;

public sealed class OAuthProviderTests
{
    [Fact]
    public void Pkce_Generate_ProducesValidVerifierAndChallenge()
    {
        var (verifier, challenge) = OAuthPkce.Generate();

        Assert.Equal(43, verifier.Length);
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);

        var expectedChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal(expectedChallenge, challenge);
    }

    [Fact]
    public void Pkce_Generate_ProducesDifferentValuesEachTime()
    {
        var (v1, _) = OAuthPkce.Generate();
        var (v2, _) = OAuthPkce.Generate();
        Assert.NotEqual(v1, v2);
    }

    [Theory]
    [InlineData("github.com", "github.com")]
    [InlineData("https://company.ghe.com", "company.ghe.com")]
    [InlineData("company.ghe.com", "company.ghe.com")]
    [InlineData("http://internal.corp.net/path", "internal.corp.net")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void GitHubCopilot_NormalizeDomain_ReturnsExpected(string input, string? expected)
    {
        Assert.Equal(expected, GitHubCopilotOAuthProvider.NormalizeDomain(input));
    }

    [Theory]
    [InlineData("tid=abc;exp=123;proxy-ep=proxy.individual.githubcopilot.com;sku=free", "https://api.individual.githubcopilot.com")]
    [InlineData("tid=abc;exp=123;proxy-ep=proxy.business.githubcopilot.com;sku=biz", "https://api.business.githubcopilot.com")]
    [InlineData("no-proxy-ep-here", "https://api.individual.githubcopilot.com")]
    public void GitHubCopilot_GetBaseUrl_ExtractsFromToken(string token, string expected)
    {
        Assert.Equal(expected, GitHubCopilotOAuthProvider.GetBaseUrl(token, null));
    }

    [Fact]
    public void GitHubCopilot_GetBaseUrl_FallsBackToEnterprise()
    {
        Assert.Equal("https://copilot-api.company.ghe.com", GitHubCopilotOAuthProvider.GetBaseUrl(null, "company.ghe.com"));
    }

    [Fact]
    public void OpenAICodex_ExtractAccountId_ParsesValidJwt()
    {
        var payload = new { exp = 9999999999, sub = "user", iat = 1000000000 };
        payload = null!;
        var payloadJson = """{"exp":9999999999,"https://api.openai.com/auth":{"chatgpt_account_id":"acct_123"}}""";
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var token = $"{header}.{payloadB64}.fake-signature";

        var accountId = OpenAICodexOAuthProvider.ExtractAccountId(token);
        Assert.Equal("acct_123", accountId);
    }

    [Fact]
    public void OpenAICodex_ExtractAccountId_ReturnsNullForInvalidToken()
    {
        Assert.Null(OpenAICodexOAuthProvider.ExtractAccountId("not-a-jwt"));
        Assert.Null(OpenAICodexOAuthProvider.ExtractAccountId("a.b"));
    }

    [Fact]
    public void CredentialStore_Save_WritesAndReloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """{"existing":{"type":"api_key","key":"keep-me"}}""");

            var store = new OAuthCredentialStore([authPath]);
            var credentials = new OAuthCredentials
            {
                Refresh = "r-token",
                Access = "a-token",
                ExpiresAt = new DateTimeOffset(2030, 6, 15, 12, 0, 0, TimeSpan.Zero),
                Metadata = new Dictionary<string, string> { ["projectId"] = "proj-1" }
            };

            store.Save("anthropic", credentials);

            var reloaded = new OAuthCredentialStore([authPath]);
            var entries = reloaded.LoadEntries();

            Assert.Equal("keep-me", entries["existing"].ApiKey);
            Assert.Equal("r-token", entries["anthropic"].OAuth?.Refresh);
            Assert.Equal("a-token", entries["anthropic"].OAuth?.Access);
            Assert.True(entries["anthropic"].OAuth?.Metadata.ContainsKey("projectId"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CredentialStore_Save_IgnoresReservedMetadataFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-reserved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            var store = new OAuthCredentialStore([authPath]);
            var credentials = new OAuthCredentials
            {
                Refresh = "refresh-token",
                Access = "access-token",
                ExpiresAt = new DateTimeOffset(2030, 6, 15, 12, 0, 0, TimeSpan.Zero),
                Metadata = new Dictionary<string, string>
                {
                    ["projectId"] = "proj-1",
                    ["type"] = "api_key",
                    ["access"] = "metadata-access",
                    ["key"] = "metadata-key",
                    ["expiresAt"] = "metadata-expiry"
                }
            };

            store.Save("google-gemini-cli", credentials);

            var rawJson = File.ReadAllText(authPath);
            Assert.DoesNotContain("metadata-access", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("metadata-key", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("metadata-expiry", rawJson, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(rawJson);
            var entry = document.RootElement.GetProperty("google-gemini-cli");
            Assert.Equal("oauth", entry.GetProperty("type").GetString());
            Assert.Equal("access-token", entry.GetProperty("access").GetString());
            Assert.Equal("proj-1", entry.GetProperty("projectId").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CredentialStore_Save_RestrictsUnixFileMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            var store = new OAuthCredentialStore([authPath]);

            store.Save("anthropic", new OAuthCredentials
            {
                Refresh = "refresh-token",
                Access = "access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(authPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CredentialStore_Save_CreatesFileIfMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-auth-new-{Guid.NewGuid():N}");

        try
        {
            var authPath = Path.Combine(tempDir, ".tau", "auth.json");
            var store = new OAuthCredentialStore([authPath]);

            store.Save("test-provider", new OAuthCredentials
            {
                Refresh = "r",
                Access = "a",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });

            Assert.True(File.Exists(authPath));
            var content = File.ReadAllText(authPath);
            Assert.Contains("test-provider", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
