using System.Net;
using System.Text;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

public sealed class BedrockAssumeRoleResolverTests
{
    [Fact]
    public async Task ResolveAsync_NotConfiguredWhenProfileLacksRoleArn()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when AssumeRole is not configured"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockAssumeRoleResolver.ResolveAsync(
            new BedrockOptions(),
            new BedrockProfileSnapshot { Name = "base", AccessKeyId = "AKIA", SecretAccessKey = "secret" },
            region: "us-east-1",
            httpClient: client,
            clock: () => DateTimeOffset.UtcNow);

        Assert.True(outcome.IsNotConfigured);
        Assert.False(outcome.HasCredentials);
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenRoleArnSetButNoSourceProfile()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called without source_profile"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockAssumeRoleResolver.ResolveAsync(
            new BedrockOptions(),
            new BedrockProfileSnapshot { Name = "dev", RoleArn = "arn:aws:iam::123:role/r" },
            region: "us-east-1",
            httpClient: client,
            clock: () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasError);
        Assert.Contains("credential_source-based AssumeRole is not yet supported", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_PostsSignedAssumeRoleAndParsesCredentials()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-assume-");
        try
        {
            var credentialsPath = Path.Combine(tempDir.FullName, "credentials");
            var configPath = Path.Combine(tempDir.FullName, "config");
            await File.WriteAllTextAsync(
                credentialsPath,
                """
                [base]
                aws_access_key_id = AKIABASE
                aws_secret_access_key = base-secret
                """);
            await File.WriteAllTextAsync(
                configPath,
                """
                [profile dev]
                role_arn = arn:aws:iam::123456789012:role/dev
                source_profile = base
                role_session_name = tau-dev
                external_id = ext-1
                """);

            HttpRequestMessage? captured = null;
            string? body = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                captured = request;
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <AssumeRoleResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
                          <AssumeRoleResult>
                            <Credentials>
                              <AccessKeyId>ASIAASSUME</AccessKeyId>
                              <SecretAccessKey>assume-secret</SecretAccessKey>
                              <SessionToken>assume-token</SessionToken>
                              <Expiration>2099-01-01T00:00:00Z</Expiration>
                            </Credentials>
                          </AssumeRoleResult>
                        </AssumeRoleResponse>
                        """,
                        Encoding.UTF8,
                        "text/xml")
                };
            });
            using var client = new HttpClient(handler);

            var devProfile = BedrockProfileCredentialsResolver.Load(
                new BedrockOptions { Profile = "dev", CredentialsFile = credentialsPath, ConfigFile = configPath });
            Assert.NotNull(devProfile);

            var clock = new DateTimeOffset(2026, 4, 29, 1, 2, 3, TimeSpan.Zero);
            var outcome = await BedrockAssumeRoleResolver.ResolveAsync(
                new BedrockOptions { Profile = "dev", CredentialsFile = credentialsPath, ConfigFile = configPath },
                devProfile,
                region: "us-east-1",
                httpClient: client,
                clock: () => clock);

            Assert.True(outcome.HasCredentials);
            Assert.Equal("ASIAASSUME", outcome.Credentials!.AccessKeyId);
            Assert.Equal("assume-secret", outcome.Credentials.SecretAccessKey);
            Assert.Equal("assume-token", outcome.Credentials.SessionToken);
            Assert.Equal("assume_role", outcome.Credentials.Source);

            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured!.Method);
            Assert.Equal("sts.us-east-1.amazonaws.com", captured.RequestUri!.Host);
            var authorization = captured.Headers.GetValues("Authorization").Single();
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIABASE/20260429/us-east-1/sts/aws4_request", authorization, StringComparison.Ordinal);
            Assert.Contains("Action=AssumeRole", body, StringComparison.Ordinal);
            Assert.Contains("RoleSessionName=tau-dev", body, StringComparison.Ordinal);
            Assert.Contains("ExternalId=ext-1", body, StringComparison.Ordinal);
            Assert.Contains("RoleArn=arn%3Aaws%3Aiam%3A%3A123456789012%3Arole%2Fdev", body, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenSourceProfileMissing()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-assume-nosrc-");
        try
        {
            var credentialsPath = Path.Combine(tempDir.FullName, "credentials");
            var configPath = Path.Combine(tempDir.FullName, "config");
            await File.WriteAllTextAsync(credentialsPath, "");
            await File.WriteAllTextAsync(
                configPath,
                """
                [profile dev]
                role_arn = arn:aws:iam::123456789012:role/dev
                source_profile = missing
                """);

            var devProfile = BedrockProfileCredentialsResolver.Load(
                new BedrockOptions { Profile = "dev", CredentialsFile = credentialsPath, ConfigFile = configPath });
            Assert.NotNull(devProfile);

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                throw new InvalidOperationException("HTTP should not be called when source_profile is missing"));
            using var client = new HttpClient(handler);

            var outcome = await BedrockAssumeRoleResolver.ResolveAsync(
                new BedrockOptions { Profile = "dev", CredentialsFile = credentialsPath, ConfigFile = configPath },
                devProfile,
                region: "us-east-1",
                httpClient: client,
                clock: () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasError);
            Assert.Contains("did not provide static credentials", outcome.Error!, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_SurfacesStsErrorCodeAndMessage()
    {
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-assume-err-");
        try
        {
            var credentialsPath = Path.Combine(tempDir.FullName, "credentials");
            var configPath = Path.Combine(tempDir.FullName, "config");
            await File.WriteAllTextAsync(
                credentialsPath,
                """
                [base]
                aws_access_key_id = AKIABASE
                aws_secret_access_key = base-secret
                """);
            await File.WriteAllTextAsync(
                configPath,
                """
                [profile dev]
                role_arn = arn:aws:iam::123456789012:role/dev
                source_profile = base
                """);

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """
                        <ErrorResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
                          <Error>
                            <Type>Sender</Type>
                            <Code>AccessDenied</Code>
                            <Message>not authorized to perform sts:AssumeRole</Message>
                          </Error>
                        </ErrorResponse>
                        """,
                        Encoding.UTF8,
                        "text/xml")
                });
            using var client = new HttpClient(handler);

            var devProfile = BedrockProfileCredentialsResolver.Load(
                new BedrockOptions { Profile = "dev", CredentialsFile = credentialsPath, ConfigFile = configPath });
            var outcome = await BedrockAssumeRoleResolver.ResolveAsync(
                new BedrockOptions { Profile = "dev", CredentialsFile = credentialsPath, ConfigFile = configPath },
                devProfile,
                region: "us-east-1",
                httpClient: client,
                clock: () => DateTimeOffset.UtcNow);

            Assert.True(outcome.HasError);
            Assert.Contains("AccessDenied", outcome.Error!, StringComparison.Ordinal);
            Assert.Contains("sts:AssumeRole", outcome.Error!, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
