using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

public sealed class BedrockSsoResolverTests
{
    [Fact]
    public async Task ResolveAsync_NotConfiguredWhenProfileHasNoSsoFields()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when SSO is not configured"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockSsoResolver.ResolveAsync(
            new BedrockOptions(),
            new BedrockProfileSnapshot { Name = "base" },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.True(outcome.IsNotConfigured);
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenStartUrlOrRegionMissing()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when SSO is incomplete"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockSsoResolver.ResolveAsync(
            new BedrockOptions(),
            new BedrockProfileSnapshot
            {
                Name = "dev",
                SsoAccountId = "123",
                SsoRoleName = "Admin"
            },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasError);
        Assert.Contains("sso_start_url or sso_region", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenTokenCacheMissing()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-empty-");
        try
        {
            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                throw new InvalidOperationException("HTTP should not be called when token cache is missing"));
            using var client = new HttpClient(handler);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions { SsoTokenCacheDirectory = tempDir.FullName },
                new BedrockProfileSnapshot
                {
                    Name = "dev",
                    SsoAccountId = "123",
                    SsoRoleName = "Admin",
                    SsoStartUrl = "https://example.awsapps.com/start",
                    SsoRegion = "us-east-1"
                },
                client,
                () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasError);
            Assert.Contains("aws sso login", outcome.Error!, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenAccessTokenExpired()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-expired-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            var tokenPath = WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """{ "accessToken": "stale", "expiresAt": "2020-01-01T00:00:00Z" }""");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                throw new InvalidOperationException("HTTP should not be called when token expired"));
            using var client = new HttpClient(handler);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions { SsoTokenCacheDirectory = tempDir.FullName },
                new BedrockProfileSnapshot
                {
                    Name = "dev",
                    SsoAccountId = "123456789012",
                    SsoRoleName = "Admin",
                    SsoStartUrl = startUrl,
                    SsoRegion = "us-east-1"
                },
                client,
                () => new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.True(outcome.HasError);
            Assert.Contains("expired", outcome.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(tokenPath);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsRoleCredentialsForLegacyInlineSso()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-legacy-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """{ "accessToken": "fresh-token", "expiresAt": "2099-01-01T00:00:00Z" }""");

            HttpRequestMessage? captured = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                captured = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "roleCredentials": {
                            "accessKeyId": "ASIASSO",
                            "secretAccessKey": "sso-secret",
                            "sessionToken": "sso-token",
                            "expiration": {{DateTimeOffset.Parse("2099-01-01T00:00:00Z").ToUnixTimeMilliseconds()}}
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            });
            using var client = new HttpClient(handler);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions
                {
                    SsoTokenCacheDirectory = tempDir.FullName,
                    SsoPortalEndpoint = "https://portal.sso.us-west-2.amazonaws.test"
                },
                new BedrockProfileSnapshot
                {
                    Name = "dev",
                    SsoAccountId = "123456789012",
                    SsoRoleName = "Admin",
                    SsoStartUrl = startUrl,
                    SsoRegion = "us-west-2"
                },
                client,
                () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasCredentials);
            Assert.Equal("ASIASSO", outcome.Credentials!.AccessKeyId);
            Assert.Equal("sso", outcome.Credentials.Source);

            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Get, captured!.Method);
            Assert.Equal("portal.sso.us-west-2.amazonaws.test", captured.RequestUri!.Host);
            Assert.Contains("role_name=Admin", captured.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("account_id=123456789012", captured.RequestUri.Query, StringComparison.Ordinal);
            Assert.Equal("fresh-token", captured.Headers.GetValues("x-amz-sso_bearer_token").Single());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_UsesSessionNameHashWhenSsoSessionSet()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-session-");
        try
        {
            var sessionName = "my-sso-session";
            // Write cache file under session-name SHA1 hash
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sessionName))).ToLowerInvariant();
            var tokenPath = Path.Combine(tempDir.FullName, hash + ".json");
            await File.WriteAllTextAsync(
                tokenPath,
                """{ "accessToken": "session-token", "expiresAt": "2099-01-01T00:00:00Z" }""");

            HttpRequestMessage? captured = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                captured = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "roleCredentials": {
                            "accessKeyId": "ASIASESS",
                            "secretAccessKey": "sess-secret",
                            "sessionToken": "sess-token"
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            });
            using var client = new HttpClient(handler);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions { SsoTokenCacheDirectory = tempDir.FullName },
                new BedrockProfileSnapshot
                {
                    Name = "dev",
                    SsoSession = sessionName,
                    SsoAccountId = "123",
                    SsoRoleName = "Reader",
                    SsoStartUrl = "https://example.awsapps.com/start",
                    SsoRegion = "us-east-1"
                },
                client,
                () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasCredentials);
            Assert.Equal("session-token", captured!.Headers.GetValues("x-amz-sso_bearer_token").Single());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_SurfacesPortalErrorBody()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-err-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """{ "accessToken": "tok", "expiresAt": "2099-01-01T00:00:00Z" }""");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("UnauthorizedException", Encoding.UTF8, "text/plain")
                });
            using var client = new HttpClient(handler);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions { SsoTokenCacheDirectory = tempDir.FullName },
                new BedrockProfileSnapshot
                {
                    Name = "dev",
                    SsoAccountId = "123",
                    SsoRoleName = "Admin",
                    SsoStartUrl = startUrl,
                    SsoRegion = "us-east-1"
                },
                client,
                () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasError);
            Assert.Contains("HTTP 401", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains("UnauthorizedException", outcome.Error!, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static string WriteTokenCache(string directory, string startUrl, string jsonContent)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(startUrl))).ToLowerInvariant();
        var tokenPath = Path.Combine(directory, hash + ".json");
        File.WriteAllText(tokenPath, jsonContent);
        return tokenPath;
    }
}
