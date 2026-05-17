using System.Net;
using System.Text;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

public sealed class BedrockWebIdentityResolverTests
{
    [Fact]
    public void ParseAssumeRoleResponse_ReadsCredentialsFromValidXml()
    {
        const string xml = """
            <AssumeRoleWithWebIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
              <AssumeRoleWithWebIdentityResult>
                <Credentials>
                  <AccessKeyId>ASIAEXAMPLE</AccessKeyId>
                  <SecretAccessKey>secret-example</SecretAccessKey>
                  <SessionToken>session-example</SessionToken>
                  <Expiration>2099-01-01T00:00:00Z</Expiration>
                </Credentials>
              </AssumeRoleWithWebIdentityResult>
            </AssumeRoleWithWebIdentityResponse>
            """;

        var credentials = BedrockWebIdentityResolver.ParseAssumeRoleResponse(xml, () => DateTimeOffset.UtcNow);

        Assert.NotNull(credentials);
        Assert.Equal("ASIAEXAMPLE", credentials!.AccessKeyId);
        Assert.Equal("secret-example", credentials.SecretAccessKey);
        Assert.Equal("session-example", credentials.SessionToken);
        Assert.Equal("web_identity", credentials.Source);
        Assert.NotNull(credentials.ExpiresAt);
    }

    [Fact]
    public void ParseAssumeRoleResponse_ReturnsNullWhenCredentialsMissing()
    {
        const string xml = """
            <AssumeRoleWithWebIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
              <AssumeRoleWithWebIdentityResult />
            </AssumeRoleWithWebIdentityResponse>
            """;

        var credentials = BedrockWebIdentityResolver.ParseAssumeRoleResponse(xml, () => DateTimeOffset.UtcNow);

        Assert.Null(credentials);
    }

    [Fact]
    public void ParseAssumeRoleResponse_RejectsAlreadyExpiredCredentials()
    {
        const string xml = """
            <AssumeRoleWithWebIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
              <AssumeRoleWithWebIdentityResult>
                <Credentials>
                  <AccessKeyId>ASIA</AccessKeyId>
                  <SecretAccessKey>secret</SecretAccessKey>
                  <Expiration>2020-01-01T00:00:00Z</Expiration>
                </Credentials>
              </AssumeRoleWithWebIdentityResult>
            </AssumeRoleWithWebIdentityResponse>
            """;

        var clock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var credentials = BedrockWebIdentityResolver.ParseAssumeRoleResponse(xml, () => clock);

        Assert.Null(credentials);
    }

    [Fact]
    public void ExtractStsError_ReturnsCodeAndMessage()
    {
        const string xml = """
            <ErrorResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
              <Error>
                <Type>Sender</Type>
                <Code>InvalidIdentityToken</Code>
                <Message>The web identity token is malformed.</Message>
              </Error>
              <RequestId>req-1</RequestId>
            </ErrorResponse>
            """;

        var error = BedrockWebIdentityResolver.ExtractStsError(xml);

        Assert.NotNull(error);
        Assert.Contains("InvalidIdentityToken", error!, StringComparison.Ordinal);
        Assert.Contains("malformed", error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_PostsAssumeRoleWithWebIdentityAndReturnsCredentials()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-webidentity-");
        try
        {
            var tokenPath = Path.Combine(tempDir.FullName, "token");
            await File.WriteAllTextAsync(tokenPath, "jwt-token-value\n");

            HttpRequestMessage? capturedRequest = null;
            string? capturedBody = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SuccessResponseXml(), Encoding.UTF8, "text/xml")
                };
            });
            using var client = new HttpClient(handler);

            var outcome = await BedrockWebIdentityResolver.ResolveAsync(
                new BedrockOptions
                {
                    WebIdentityTokenFile = tokenPath,
                    WebIdentityRoleArn = "arn:aws:iam::123456789012:role/sample",
                    WebIdentityRoleSessionName = "tau-session"
                },
                profile: null,
                region: "us-west-2",
                httpClient: client,
                clock: () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasCredentials);
            Assert.Equal("ASIAEXAMPLE", outcome.Credentials!.AccessKeyId);
            Assert.Equal("web_identity", outcome.Credentials.Source);

            capturedRequest = handler.Requests.Single();
            capturedBody = handler.CapturedBody;
            Assert.Equal(HttpMethod.Post, capturedRequest.Method);
            Assert.Equal("sts.us-west-2.amazonaws.com", capturedRequest.RequestUri!.Host);
            Assert.Contains("Action=AssumeRoleWithWebIdentity", capturedBody, StringComparison.Ordinal);
            Assert.Contains("RoleSessionName=tau-session", capturedBody, StringComparison.Ordinal);
            Assert.Contains("WebIdentityToken=jwt-token-value", capturedBody, StringComparison.Ordinal);
            Assert.Contains("RoleArn=arn%3Aaws%3Aiam%3A%3A123456789012%3Arole%2Fsample", capturedBody, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReportsNotConfiguredWhenInputsMissing()
    {
        using var env = new EnvironmentScope(
            ("AWS_WEB_IDENTITY_TOKEN_FILE", null),
            ("AWS_ROLE_ARN", null),
            ("AWS_ROLE_SESSION_NAME", null));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when not configured"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockWebIdentityResolver.ResolveAsync(
            new BedrockOptions(),
            profile: null,
            region: "us-east-1",
            httpClient: client,
            clock: () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.IsNotConfigured);
        Assert.False(outcome.HasError);
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenTokenFileMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"tau-bedrock-webidentity-missing-{Guid.NewGuid():N}");
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when token file is missing"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockWebIdentityResolver.ResolveAsync(
            new BedrockOptions
            {
                WebIdentityTokenFile = missingPath,
                WebIdentityRoleArn = "arn:aws:iam::123456789012:role/sample"
            },
            profile: null,
            region: "us-east-1",
            httpClient: client,
            clock: () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.HasError);
        Assert.Contains("token file not found", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenTokenFileEmpty()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-webidentity-empty-");
        try
        {
            var tokenPath = Path.Combine(tempDir.FullName, "token");
            await File.WriteAllTextAsync(tokenPath, "   \n");
            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                throw new InvalidOperationException("HTTP should not be called for empty token"));
            using var client = new HttpClient(handler);

            var outcome = await BedrockWebIdentityResolver.ResolveAsync(
                new BedrockOptions
                {
                    WebIdentityTokenFile = tokenPath,
                    WebIdentityRoleArn = "arn:aws:iam::123456789012:role/sample"
                },
                profile: null,
                region: "us-east-1",
                httpClient: client,
                clock: () => DateTimeOffset.UtcNow);

            Assert.False(outcome.HasCredentials);
            Assert.True(outcome.HasError);
            Assert.Contains("empty", outcome.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_SurfacesStsErrorCodeAndMessage()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-webidentity-err-");
        try
        {
            var tokenPath = Path.Combine(tempDir.FullName, "token");
            await File.WriteAllTextAsync(tokenPath, "jwt-token");

            const string errorXml = """
                <ErrorResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
                  <Error>
                    <Type>Sender</Type>
                    <Code>InvalidIdentityToken</Code>
                    <Message>The web identity token is malformed.</Message>
                  </Error>
                </ErrorResponse>
                """;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(errorXml, Encoding.UTF8, "text/xml")
                });
            using var client = new HttpClient(handler);

            var outcome = await BedrockWebIdentityResolver.ResolveAsync(
                new BedrockOptions
                {
                    WebIdentityTokenFile = tokenPath,
                    WebIdentityRoleArn = "arn:aws:iam::123456789012:role/sample"
                },
                profile: null,
                region: "us-east-1",
                httpClient: client,
                clock: () => DateTimeOffset.UtcNow);

            Assert.False(outcome.HasCredentials);
            Assert.True(outcome.HasError);
            Assert.Contains("InvalidIdentityToken", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains("malformed", outcome.Error!, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_UsesStsEndpointOverrideWhenProvided()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-webidentity-endpoint-");
        try
        {
            var tokenPath = Path.Combine(tempDir.FullName, "token");
            await File.WriteAllTextAsync(tokenPath, "jwt-token");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SuccessResponseXml(), Encoding.UTF8, "text/xml")
                });
            using var client = new HttpClient(handler);

            var outcome = await BedrockWebIdentityResolver.ResolveAsync(
                new BedrockOptions
                {
                    WebIdentityTokenFile = tokenPath,
                    WebIdentityRoleArn = "arn:aws:iam::123456789012:role/sample",
                    StsEndpoint = "https://sts.fips.us-west-2.amazonaws.com/"
                },
                profile: null,
                region: "us-west-2",
                httpClient: client,
                clock: () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasCredentials);
            Assert.Equal("sts.fips.us-west-2.amazonaws.com", handler.Requests.Single().RequestUri!.Host);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static string SuccessResponseXml() => """
        <AssumeRoleWithWebIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
          <AssumeRoleWithWebIdentityResult>
            <Credentials>
              <AccessKeyId>ASIAEXAMPLE</AccessKeyId>
              <SecretAccessKey>secret-example</SecretAccessKey>
              <SessionToken>session-example</SessionToken>
              <Expiration>2099-01-01T00:00:00Z</Expiration>
            </Credentials>
          </AssumeRoleWithWebIdentityResult>
        </AssumeRoleWithWebIdentityResponse>
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
