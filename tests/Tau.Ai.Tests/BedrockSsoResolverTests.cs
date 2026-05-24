using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tau.Ai;
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
    public async Task ResolveAsync_FailsWhenSsoSessionAndStartUrlMissing()
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
        Assert.Contains("sso_session or sso_start_url", outcome.Error!, StringComparison.Ordinal);
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
            Assert.Contains("missing refreshToken", outcome.Error!, StringComparison.Ordinal);
            Assert.NotNull(tokenPath);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_RefreshesExpiredTokenAndWritesCache()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-refresh-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            var tokenPath = WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """
                {
                  "startUrl": "https://example.awsapps.com/start",
                  "region": "us-west-2",
                  "accessToken": "expired-token",
                  "expiresAt": "2020-01-01T00:00:00Z",
                  "clientId": "client-id",
                  "clientSecret": "client-secret",
                  "registrationExpiresAt": "2099-01-01T00:00:00Z",
                  "refreshToken": "refresh-token"
                }
                """);

            HttpRequestMessage? tokenRequest = null;
            string? tokenBody = null;
            HttpRequestMessage? portalRequest = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/token")
                {
                    tokenRequest = request;
                    tokenBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            {
                              "accessToken": "refreshed-token",
                              "expiresIn": 3600,
                              "refreshToken": "next-refresh-token",
                              "tokenType": "Bearer"
                            }
                            """,
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                portalRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "roleCredentials": {
                            "accessKeyId": "ASIAREFRESH",
                            "secretAccessKey": "refresh-secret",
                            "sessionToken": "refresh-session",
                            "expiration": {{DateTimeOffset.Parse("2099-01-01T00:00:00Z").ToUnixTimeMilliseconds()}}
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            });
            using var client = new HttpClient(handler);
            var clock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions
                {
                    SsoTokenCacheDirectory = tempDir.FullName,
                    SsoOidcEndpoint = "https://oidc.us-west-2.amazonaws.test",
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
                () => clock);

            Assert.True(outcome.HasCredentials);
            Assert.Equal("ASIAREFRESH", outcome.Credentials!.AccessKeyId);
            Assert.Equal("sso", outcome.Credentials.Source);

            Assert.NotNull(tokenRequest);
            Assert.Equal(HttpMethod.Post, tokenRequest!.Method);
            Assert.Equal("oidc.us-west-2.amazonaws.test", tokenRequest.RequestUri!.Host);
            using (var requestDoc = JsonDocument.Parse(tokenBody!))
            {
                var root = requestDoc.RootElement;
                Assert.Equal("client-id", root.GetProperty("clientId").GetString());
                Assert.Equal("client-secret", root.GetProperty("clientSecret").GetString());
                Assert.Equal("refresh_token", root.GetProperty("grantType").GetString());
                Assert.Equal("refresh-token", root.GetProperty("refreshToken").GetString());
            }

            Assert.NotNull(portalRequest);
            Assert.Equal("refreshed-token", portalRequest!.Headers.GetValues("x-amz-sso_bearer_token").Single());

            using var cacheDoc = JsonDocument.Parse(await File.ReadAllTextAsync(tokenPath));
            var cache = cacheDoc.RootElement;
            Assert.Equal("refreshed-token", cache.GetProperty("accessToken").GetString());
            Assert.Equal("next-refresh-token", cache.GetProperty("refreshToken").GetString());
            Assert.Equal("client-id", cache.GetProperty("clientId").GetString());
            Assert.Equal("client-secret", cache.GetProperty("clientSecret").GetString());
            Assert.Equal("https://example.awsapps.com/start", cache.GetProperty("startUrl").GetString());
            Assert.Equal("us-west-2", cache.GetProperty("region").GetString());
            Assert.True(DateTimeOffset.Parse(cache.GetProperty("expiresAt").GetString()!) > clock);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_FailsExpiredTokenWhenRefreshTokenMissing()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-refresh-missing-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """
                {
                  "accessToken": "expired-token",
                  "expiresAt": "2020-01-01T00:00:00Z",
                  "clientId": "client-id"
                }
                """);

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                throw new InvalidOperationException("HTTP should not be called when SSO refresh metadata is incomplete"));
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
            Assert.Contains("missing refreshToken", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains("aws sso login --profile dev", outcome.Error!, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_RenewsExpiredClientRegistrationAndWritesCache()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-registration-expired-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            var tokenPath = WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """
                {
                  "startUrl": "https://example.awsapps.com/start",
                  "region": "us-east-1",
                  "accessToken": "expired-token",
                  "expiresAt": "2020-01-01T00:00:00Z",
                  "clientId": "old-client-id",
                  "clientSecret": "old-client-secret",
                  "registrationExpiresAt": "2020-01-02T00:00:00Z",
                  "refreshToken": "refresh-token"
                }
                """);

            var paths = new List<string>();
            string? registerBody = null;
            string? tokenBody = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                if (request.RequestUri.AbsolutePath == "/client/register")
                {
                    registerBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $$"""
                            {
                              "clientId": "new-client-id",
                              "clientSecret": "new-client-secret",
                              "clientIdIssuedAt": {{DateTimeOffset.Parse("2030-01-01T00:00:00Z").ToUnixTimeSeconds()}},
                              "clientSecretExpiresAt": {{DateTimeOffset.Parse("2099-01-01T00:00:00Z").ToUnixTimeSeconds()}}
                            }
                            """,
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                if (request.RequestUri.AbsolutePath == "/token")
                {
                    tokenBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            {
                              "accessToken": "registered-refreshed-token",
                              "expiresIn": 7200,
                              "refreshToken": "registered-next-refresh-token",
                              "tokenType": "Bearer"
                            }
                            """,
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "roleCredentials": {
                            "accessKeyId": "ASIAREGISTERED",
                            "secretAccessKey": "registered-secret",
                            "sessionToken": "registered-session",
                            "expiration": {{DateTimeOffset.Parse("2099-01-01T00:00:00Z").ToUnixTimeMilliseconds()}}
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            });
            using var client = new HttpClient(handler);
            var clock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions
                {
                    SsoTokenCacheDirectory = tempDir.FullName,
                    SsoOidcEndpoint = "https://oidc.us-east-1.amazonaws.test",
                    SsoPortalEndpoint = "https://portal.sso.us-east-1.amazonaws.test"
                },
                new BedrockProfileSnapshot
                {
                    Name = "dev",
                    SsoAccountId = "123456789012",
                    SsoRoleName = "Admin",
                    SsoStartUrl = startUrl,
                    SsoRegion = "us-east-1",
                    SsoRegistrationScopes = "sso:account:access codewhisperer:completions"
                },
                client,
                () => clock);

            Assert.True(outcome.HasCredentials);
            Assert.Equal("ASIAREGISTERED", outcome.Credentials!.AccessKeyId);
            Assert.Equal(["/client/register", "/token", "/federation/credentials"], paths);

            using (var registerDoc = JsonDocument.Parse(registerBody!))
            {
                var root = registerDoc.RootElement;
                Assert.Equal("tau-bedrock-sso", root.GetProperty("clientName").GetString());
                Assert.Equal("public", root.GetProperty("clientType").GetString());
                Assert.Equal("refresh_token", root.GetProperty("grantTypes")[0].GetString());
                Assert.Equal("sso:account:access", root.GetProperty("scopes")[0].GetString());
                Assert.Equal("codewhisperer:completions", root.GetProperty("scopes")[1].GetString());
            }

            using (var tokenDoc = JsonDocument.Parse(tokenBody!))
            {
                var root = tokenDoc.RootElement;
                Assert.Equal("new-client-id", root.GetProperty("clientId").GetString());
                Assert.Equal("new-client-secret", root.GetProperty("clientSecret").GetString());
                Assert.Equal("refresh-token", root.GetProperty("refreshToken").GetString());
                Assert.Equal("refresh_token", root.GetProperty("grantType").GetString());
            }

            using var cacheDoc = JsonDocument.Parse(await File.ReadAllTextAsync(tokenPath));
            var cache = cacheDoc.RootElement;
            Assert.Equal("registered-refreshed-token", cache.GetProperty("accessToken").GetString());
            Assert.Equal("registered-next-refresh-token", cache.GetProperty("refreshToken").GetString());
            Assert.Equal("new-client-id", cache.GetProperty("clientId").GetString());
            Assert.Equal("new-client-secret", cache.GetProperty("clientSecret").GetString());
            Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z").ToUnixTimeSeconds(), cache.GetProperty("clientIdIssuedAt").GetInt64());
            Assert.True(DateTimeOffset.Parse(cache.GetProperty("registrationExpiresAt").GetString()!) > clock);
            Assert.Equal("https://example.awsapps.com/start", cache.GetProperty("startUrl").GetString());
            Assert.Equal("us-east-1", cache.GetProperty("region").GetString());
            Assert.Equal("sso:account:access", cache.GetProperty("registrationScopes")[0].GetString());
            Assert.Equal("codewhisperer:completions", cache.GetProperty("registrationScopes")[1].GetString());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_RegistersClientWhenClientMetadataMissing()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-registration-missing-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            var tokenPath = WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """
                {
                  "accessToken": "expired-token",
                  "expiresAt": "2020-01-01T00:00:00Z",
                  "refreshToken": "refresh-token",
                  "registrationScopes": ["sso:account:access"]
                }
                """);

            var paths = new List<string>();
            string? registerBody = null;
            string? tokenBody = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                if (request.RequestUri.AbsolutePath == "/client/register")
                {
                    registerBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $$"""
                            {
                              "clientId": "registered-client-id",
                              "clientSecret": "registered-client-secret",
                              "clientSecretExpiresAt": {{DateTimeOffset.Parse("2099-01-01T00:00:00Z").ToUnixTimeSeconds()}}
                            }
                            """,
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                if (request.RequestUri.AbsolutePath == "/token")
                {
                    tokenBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{ "accessToken": "registered-token", "expiresIn": 1800, "tokenType": "Bearer" }""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "roleCredentials": {
                            "accessKeyId": "ASIAMISSINGMETA",
                            "secretAccessKey": "missing-meta-secret",
                            "expiration": {{DateTimeOffset.Parse("2099-01-01T00:00:00Z").ToUnixTimeMilliseconds()}}
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            });
            using var client = new HttpClient(handler);
            var clock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions
                {
                    SsoTokenCacheDirectory = tempDir.FullName,
                    SsoOidcEndpoint = "https://oidc.us-west-2.amazonaws.test",
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
                () => clock);

            Assert.True(outcome.HasCredentials);
            Assert.Equal("ASIAMISSINGMETA", outcome.Credentials!.AccessKeyId);
            Assert.Equal(["/client/register", "/token", "/federation/credentials"], paths);

            using (var registerDoc = JsonDocument.Parse(registerBody!))
            {
                var root = registerDoc.RootElement;
                Assert.Equal("tau-bedrock-sso", root.GetProperty("clientName").GetString());
                Assert.Equal("public", root.GetProperty("clientType").GetString());
                Assert.Equal("refresh_token", root.GetProperty("grantTypes")[0].GetString());
                Assert.Equal("sso:account:access", root.GetProperty("scopes")[0].GetString());
            }

            using (var tokenDoc = JsonDocument.Parse(tokenBody!))
            {
                var root = tokenDoc.RootElement;
                Assert.Equal("registered-client-id", root.GetProperty("clientId").GetString());
                Assert.Equal("registered-client-secret", root.GetProperty("clientSecret").GetString());
                Assert.Equal("refresh-token", root.GetProperty("refreshToken").GetString());
            }

            using var cacheDoc = JsonDocument.Parse(await File.ReadAllTextAsync(tokenPath));
            var cache = cacheDoc.RootElement;
            Assert.Equal("registered-token", cache.GetProperty("accessToken").GetString());
            Assert.Equal("refresh-token", cache.GetProperty("refreshToken").GetString());
            Assert.Equal("registered-client-id", cache.GetProperty("clientId").GetString());
            Assert.Equal("registered-client-secret", cache.GetProperty("clientSecret").GetString());
            Assert.Equal("https://example.awsapps.com/start", cache.GetProperty("startUrl").GetString());
            Assert.Equal("us-west-2", cache.GetProperty("region").GetString());
            Assert.True(DateTimeOffset.Parse(cache.GetProperty("registrationExpiresAt").GetString()!) > clock);
            Assert.True(DateTimeOffset.Parse(cache.GetProperty("expiresAt").GetString()!) > clock);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_SurfacesRegisterClientErrorWithoutCallingCreateToken()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-register-error-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """
                {
                  "accessToken": "expired-token",
                  "expiresAt": "2020-01-01T00:00:00Z",
                  "refreshToken": "refresh-token-secret-value"
                }
                """);

            var paths = new List<string>();
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                if (request.RequestUri.AbsolutePath != "/client/register")
                {
                    throw new InvalidOperationException("CreateToken or portal should not be called when RegisterClient fails");
                }

                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{ "error": "invalid_request", "error_description": "bad registration for refresh-token-secret-value" }""",
                        Encoding.UTF8,
                        "application/json")
                };
            });
            using var client = new HttpClient(handler);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions
                {
                    SsoTokenCacheDirectory = tempDir.FullName,
                    SsoOidcEndpoint = "https://oidc.us-east-1.amazonaws.test"
                },
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
            Assert.Equal(["/client/register"], paths);
            Assert.Contains("RegisterClient returned HTTP 400", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains("invalid_request", outcome.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-token-secret-value", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains(TauSecretRedactor.Placeholder, outcome.Error!, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_SurfacesOidcRefreshError()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-sso-refresh-error-");
        try
        {
            var startUrl = "https://example.awsapps.com/start";
            WriteTokenCache(
                tempDir.FullName,
                startUrl,
                """
                {
                  "accessToken": "expired-token",
                  "expiresAt": "2020-01-01T00:00:00Z",
                  "clientId": "client-id",
                  "clientSecret": "client-secret-leak",
                  "registrationExpiresAt": "2099-01-01T00:00:00Z",
                  "refreshToken": "refresh-token-secret-value"
                }
                """);

            var portalCalled = false;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/token")
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            """{ "error": "invalid_grant", "error_description": "refresh token expired for refresh-token-secret-value and client-secret-leak" }""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                portalCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);

            var outcome = await BedrockSsoResolver.ResolveAsync(
                new BedrockOptions
                {
                    SsoTokenCacheDirectory = tempDir.FullName,
                    SsoOidcEndpoint = "https://oidc.us-east-1.amazonaws.test"
                },
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
            Assert.Contains("CreateToken refresh returned HTTP 400", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains("invalid_grant", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains("refresh token expired", outcome.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-token-secret-value", outcome.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("client-secret-leak", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains(TauSecretRedactor.Placeholder, outcome.Error!, StringComparison.Ordinal);
            Assert.False(portalCalled);
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
