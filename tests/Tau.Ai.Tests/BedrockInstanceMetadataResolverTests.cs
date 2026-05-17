using System.Net;
using System.Text;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

public sealed class BedrockInstanceMetadataResolverTests
{
    [Fact]
    public void ParseInstanceCredentialsJson_ReadsSuccessfulResponse()
    {
        const string json = """
            {
              "Code": "Success",
              "LastUpdated": "2026-04-29T00:00:00Z",
              "Type": "AWS-HMAC",
              "AccessKeyId": "ASIAIMDS",
              "SecretAccessKey": "imds-secret",
              "Token": "imds-token",
              "Expiration": "2099-01-01T00:00:00Z"
            }
            """;

        var outcome = BedrockInstanceMetadataResolver.ParseInstanceCredentialsJson(json, () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasCredentials);
        Assert.Equal("ASIAIMDS", outcome.Credentials!.AccessKeyId);
        Assert.Equal("ec2_instance_metadata", outcome.Credentials.Source);
    }

    [Fact]
    public void ParseInstanceCredentialsJson_FailsOnErrorCode()
    {
        const string json = """
            {
              "Code": "ErrorRetrievingCredentials",
              "Message": "No credentials available"
            }
            """;

        var outcome = BedrockInstanceMetadataResolver.ParseInstanceCredentialsJson(json, () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("ErrorRetrievingCredentials", outcome.Error!, StringComparison.Ordinal);
        Assert.Contains("No credentials available", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_DefaultIsOptOutWhenEnvVarUnset()
    {
        using var env = new EnvironmentScope(
            ("AWS_EC2_METADATA_DISABLED", null),
            ("AWS_EC2_METADATA_V1_DISABLED", null),
            ("AWS_EC2_METADATA_SERVICE_ENDPOINT", null));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when IMDS is not opt-in"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockInstanceMetadataResolver.ResolveAsync(
            new BedrockOptions(),
            client,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.IsNotConfigured);
    }

    [Fact]
    public async Task ResolveAsync_PerformsImdsV2FlowWhenOptedIn()
    {
        using var env = new EnvironmentScope(
            ("AWS_EC2_METADATA_DISABLED", "false"),
            ("AWS_EC2_METADATA_V1_DISABLED", null),
            ("AWS_EC2_METADATA_SERVICE_ENDPOINT", null));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            if (request.Method == HttpMethod.Put &&
                request.RequestUri!.AbsolutePath == "/latest/api/token")
            {
                var ttl = request.Headers.GetValues("X-aws-ec2-metadata-token-ttl-seconds").Single();
                Assert.Equal("21600", ttl);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("the-token", Encoding.UTF8, "text/plain")
                };
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath == "/latest/meta-data/iam/security-credentials/")
            {
                Assert.Equal("the-token", request.Headers.GetValues("X-aws-ec2-metadata-token").Single());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("tau-role\n", Encoding.UTF8, "text/plain")
                };
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath == "/latest/meta-data/iam/security-credentials/tau-role")
            {
                Assert.Equal("the-token", request.Headers.GetValues("X-aws-ec2-metadata-token").Single());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "Code": "Success",
                          "AccessKeyId": "ASIAIMDS",
                          "SecretAccessKey": "imds-secret",
                          "Token": "imds-token",
                          "Expiration": "2099-01-01T00:00:00Z"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidOperationException($"Unexpected IMDS request: {request.Method} {request.RequestUri}");
        });
        using var client = new HttpClient(handler);

        var outcome = await BedrockInstanceMetadataResolver.ResolveAsync(
            new BedrockOptions(),
            client,
            () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasCredentials);
        Assert.Equal("ASIAIMDS", outcome.Credentials!.AccessKeyId);
        Assert.Equal("ec2_instance_metadata", outcome.Credentials.Source);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("169.254.169.254", handler.Requests[0].RequestUri!.Host);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToImdsV1WhenTokenFails()
    {
        using var env = new EnvironmentScope(
            ("AWS_EC2_METADATA_DISABLED", "false"),
            ("AWS_EC2_METADATA_V1_DISABLED", null),
            ("AWS_EC2_METADATA_SERVICE_ENDPOINT", null));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            if (request.Method == HttpMethod.Put &&
                request.RequestUri!.AbsolutePath == "/latest/api/token")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (request.RequestUri!.AbsolutePath == "/latest/meta-data/iam/security-credentials/")
            {
                Assert.False(request.Headers.Contains("X-aws-ec2-metadata-token"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("legacy-role", Encoding.UTF8, "text/plain")
                };
            }

            if (request.RequestUri!.AbsolutePath == "/latest/meta-data/iam/security-credentials/legacy-role")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "Code": "Success",
                          "AccessKeyId": "ASIALEGACY",
                          "SecretAccessKey": "legacy-secret",
                          "Token": "legacy-token"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidOperationException();
        });
        using var client = new HttpClient(handler);

        var outcome = await BedrockInstanceMetadataResolver.ResolveAsync(
            new BedrockOptions(),
            client,
            () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasCredentials);
        Assert.Equal("ASIALEGACY", outcome.Credentials!.AccessKeyId);
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenV1FallbackIsDisabled()
    {
        using var env = new EnvironmentScope(
            ("AWS_EC2_METADATA_DISABLED", "false"),
            ("AWS_EC2_METADATA_V1_DISABLED", "true"),
            ("AWS_EC2_METADATA_SERVICE_ENDPOINT", null));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);

        var outcome = await BedrockInstanceMetadataResolver.ResolveAsync(
            new BedrockOptions(),
            client,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.Contains("IMDSv1 fallback is disabled", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_HonorsExplicitOptOutOverEnvVar()
    {
        using var env = new EnvironmentScope(("AWS_EC2_METADATA_DISABLED", "false"));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when option disables IMDS"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockInstanceMetadataResolver.ResolveAsync(
            new BedrockOptions { Ec2MetadataDisabled = true },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.IsNotConfigured);
    }

    [Fact]
    public async Task ResolveAsync_UsesEndpointOverride()
    {
        using var env = new EnvironmentScope(
            ("AWS_EC2_METADATA_DISABLED", "false"),
            ("AWS_EC2_METADATA_V1_DISABLED", null),
            ("AWS_EC2_METADATA_SERVICE_ENDPOINT", null));
        Uri? observedHost = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            observedHost ??= request.RequestUri;
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/latest/api/token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("tok") };
            }
            if (request.RequestUri!.AbsolutePath == "/latest/meta-data/iam/security-credentials/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("role") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"Code":"Success","AccessKeyId":"k","SecretAccessKey":"s","Token":"t"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var client = new HttpClient(handler);

        var outcome = await BedrockInstanceMetadataResolver.ResolveAsync(
            new BedrockOptions { Ec2MetadataServiceEndpoint = "http://127.0.0.1:8888" },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.True(outcome.HasCredentials);
        Assert.NotNull(observedHost);
        Assert.Equal("127.0.0.1", observedHost!.Host);
        Assert.Equal(8888, observedHost.Port);
    }

    [Fact]
    public async Task ResolveAsync_RejectsNonAllowlistedEndpoint()
    {
        using var env = new EnvironmentScope(
            ("AWS_EC2_METADATA_DISABLED", "false"),
            ("AWS_EC2_METADATA_SERVICE_ENDPOINT", null));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when endpoint is rejected"));
        using var client = new HttpClient(handler);

        var outcome = await BedrockInstanceMetadataResolver.ResolveAsync(
            new BedrockOptions { Ec2MetadataServiceEndpoint = "http://attacker.example.com" },
            client,
            () => DateTimeOffset.UtcNow);

        Assert.False(outcome.HasCredentials);
        Assert.True(outcome.HasError);
        Assert.Contains("not allowed", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

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
