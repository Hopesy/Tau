using System.Net;
using System.Text;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

[Collection("BedrockEnvironment")]
public sealed class BedrockEcsContainerResolverTests
{
    [Fact]
    public void ResolveCredentialUri_BuildsRelativeUriFromDefaultBase()
    {
        var uri = BedrockEcsContainerResolver.ResolveCredentialUri(
            new BedrockOptions { ContainerCredentialsRelativeUri = "/v2/credentials/abc" },
            out var error);

        Assert.Null(error);
        Assert.NotNull(uri);
        Assert.Equal("http", uri!.Scheme);
        Assert.Equal("169.254.170.2", uri.Host);
        Assert.Equal("/v2/credentials/abc", uri.AbsolutePath);
    }

    [Fact]
    public void ResolveCredentialUri_AddsLeadingSlashWhenMissing()
    {
        var uri = BedrockEcsContainerResolver.ResolveCredentialUri(
            new BedrockOptions { ContainerCredentialsRelativeUri = "v2/credentials/abc" },
            out _);

        Assert.NotNull(uri);
        Assert.Equal("/v2/credentials/abc", uri!.AbsolutePath);
    }

    [Fact]
    public void ResolveCredentialUri_PrefersFullUri()
    {
        var uri = BedrockEcsContainerResolver.ResolveCredentialUri(
            new BedrockOptions
            {
                ContainerCredentialsRelativeUri = "/ignored",
                ContainerCredentialsFullUri = "https://example.invalid/full"
            },
            out _);

        Assert.NotNull(uri);
        Assert.Equal("example.invalid", uri!.Host);
    }

    [Fact]
    public void ResolveCredentialUri_FailsOnInvalidFullUri()
    {
        var uri = BedrockEcsContainerResolver.ResolveCredentialUri(
            new BedrockOptions { ContainerCredentialsFullUri = "not a uri" },
            out var error);

        Assert.Null(uri);
        Assert.NotNull(error);
        Assert.Contains("not a valid", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://169.254.170.2/v2/credentials/abc")]
    [InlineData("http://169.254.170.23/v1/credentials")]
    [InlineData("http://127.0.0.1:8080/creds")]
    [InlineData("http://localhost/creds")]
    [InlineData("https://example.com/creds")]
    public void IsAllowedContainerEndpoint_AcceptsAllowedHosts(string url)
    {
        Assert.True(BedrockEcsContainerResolver.IsAllowedContainerEndpoint(new Uri(url), out var error), error);
    }

    [Theory]
    [InlineData("http://example.com/creds")]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/role")]
    [InlineData("ftp://localhost/creds")]
    public void IsAllowedContainerEndpoint_RejectsDisallowedHosts(string url)
    {
        Assert.False(BedrockEcsContainerResolver.IsAllowedContainerEndpoint(new Uri(url), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseEcsCredentialsJson_ReadsValidResponse()
    {
        const string json = """
            {
              "AccessKeyId": "ASIAECSEXAMPLE",
              "SecretAccessKey": "ecs-secret",
              "Token": "ecs-token",
              "Expiration": "2099-01-01T00:00:00Z",
              "RoleArn": "arn:aws:iam::123456789012:role/ecs-task"
            }
            """;

        var outcome = BedrockEcsContainerResolver.ParseEcsCredentialsJson(json, () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasCredentials);
        var credentials = outcome.Credentials!;
        Assert.Equal("ASIAECSEXAMPLE", credentials.AccessKeyId);
        Assert.Equal("ecs-secret", credentials.SecretAccessKey);
        Assert.Equal("ecs-token", credentials.SessionToken);
        Assert.Equal("ecs", credentials.Source);
        Assert.NotNull(credentials.ExpiresAt);
    }

    [Fact]
    public void ParseEcsCredentialsJson_RejectsExpiredCredentials()
    {
        const string json = """
            {
              "AccessKeyId": "ASIA",
              "SecretAccessKey": "secret",
              "Token": "t",
              "Expiration": "2020-01-01T00:00:00Z"
            }
            """;
        var clock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var outcome = BedrockEcsContainerResolver.ParseEcsCredentialsJson(json, () => clock);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("already-expired", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseEcsCredentialsJson_FailsOnMissingFields()
    {
        const string json = """{ "AccessKeyId": "ASIA" }""";

        var outcome = BedrockEcsContainerResolver.ParseEcsCredentialsJson(json, () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("missing AccessKeyId or SecretAccessKey", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_FetchesAndParsesCredentialsFromRelativeUri()
    {
        using var env = new EnvironmentScope(
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", null));

        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessJson(), Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler);

        var outcome = await BedrockEcsContainerResolver.ResolveAsync(
            new BedrockOptions { ContainerCredentialsRelativeUri = "/v2/credentials/abc" },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasCredentials);
        Assert.Equal("ASIAECSEXAMPLE", outcome.Credentials!.AccessKeyId);
        Assert.Equal("ecs", outcome.Credentials.Source);
        var captured = handler.Requests.Single();
        Assert.Equal("169.254.170.2", captured.RequestUri!.Host);
        Assert.Equal("/v2/credentials/abc", captured.RequestUri.AbsolutePath);
        Assert.False(captured.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task ResolveAsync_AppliesAuthorizationTokenWhenProvided()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessJson(), Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler);

        var outcome = await BedrockEcsContainerResolver.ResolveAsync(
            new BedrockOptions
            {
                ContainerCredentialsFullUri = "https://example.test/creds",
                ContainerAuthorizationToken = "Bearer abc"
            },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasCredentials);
        var captured = handler.Requests.Single();
        Assert.Equal("Bearer abc", captured.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task ResolveAsync_ReadsAuthorizationTokenFromFile()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-ecs-token-");
        try
        {
            var tokenPath = Path.Combine(tempDir.FullName, "token");
            await File.WriteAllTextAsync(tokenPath, "token-from-file\n");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SuccessJson(), Encoding.UTF8, "application/json")
                });
            using var client = new HttpClient(handler);

            var outcome = await BedrockEcsContainerResolver.ResolveAsync(
                new BedrockOptions
                {
                    ContainerCredentialsFullUri = "http://localhost:8888/creds",
                    ContainerAuthorizationTokenFile = tokenPath
                },
                client,
                () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasCredentials);
            var captured = handler.Requests.Single();
            Assert.Equal("token-from-file", captured.Headers.GetValues("Authorization").Single());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_RejectsNonLoopbackHttpEndpoint()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when endpoint is rejected"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockEcsContainerResolver.ResolveAsync(
            new BedrockOptions { ContainerCredentialsFullUri = "http://example.com/creds" },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.HasError);
        Assert.Contains("not allowed", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_ReportsNotConfiguredWhenInputsMissing()
    {
        using var env = new EnvironmentScope(
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", null));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when not configured"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockEcsContainerResolver.ResolveAsync(
            new BedrockOptions(),
            client,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.IsNotConfigured);
        Assert.False(outcome.HasError);
    }

    [Fact]
    public async Task ResolveAsync_PassesThroughHttpErrors()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("forbidden body", Encoding.UTF8, "text/plain")
            });
        using var client = new HttpClient(handler);

        var outcome = await BedrockEcsContainerResolver.ResolveAsync(
            new BedrockOptions { ContainerCredentialsRelativeUri = "/v2/credentials/abc" },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("HTTP 403", outcome.Error!, StringComparison.Ordinal);
        Assert.Contains("forbidden body", outcome.Error!, StringComparison.Ordinal);
    }

    private static string SuccessJson() => """
        {
          "AccessKeyId": "ASIAECSEXAMPLE",
          "SecretAccessKey": "ecs-secret",
          "Token": "ecs-token",
          "Expiration": "2099-01-01T00:00:00Z"
        }
        """;

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly List<(string Name, string? Previous)> _entries = [];

        public EnvironmentScope(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _entries.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, previous) in _entries)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}
