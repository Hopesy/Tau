using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

[Collection("BedrockEnvironment")]
public sealed class BedrockProviderTests
{
    [Fact]
    public void RegisterAll_UsesDedicatedBedrockProvider()
    {
        var registry = new ProviderRegistry();

        BuiltInProviders.RegisterAll(registry);

        Assert.IsType<BedrockProvider>(registry.Get("bedrock-converse-stream"));
    }

    [Fact]
    public async Task Stream_UsesBearerTokenAndParsesTextUsageEvents()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            capturedRequest = request;
            return EventStreamResponse(
                EventJson("messageStart", """{"role":"assistant"}"""),
                EventJson("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"hello"}}"""),
                EventJson("contentBlockStop", """{"contentBlockIndex":0}"""),
                EventJson("messageStop", """{"stopReason":"end_turn"}"""),
                EventJson("metadata", """{"usage":{"inputTokens":3,"outputTokens":4,"cacheReadInputTokens":1,"cacheWriteInputTokens":2}}"""));
        });
        using var client = new HttpClient(handler);
        var provider = new BedrockProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            new LlmContext
            {
                SystemPrompt = "be concise",
                Messages = [new UserMessage("hi")]
            },
            new BedrockOptions
            {
                Region = "us-west-2",
                BearerToken = "bedrock-token",
                MaxTokens = 64,
                Temperature = 0.2f
            }));

        Assert.NotNull(capturedRequest);
        Assert.Equal("/model/anthropic.claude-3-7-sonnet-20250219-v1%3A0/converse-stream", capturedRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("bedrock-token", capturedRequest.Headers.Authorization.Parameter);
        Assert.False(capturedRequest.Headers.Contains("x-amz-date"));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var root = doc.RootElement;
        Assert.Equal("be concise", root.GetProperty("system")[0].GetProperty("text").GetString());
        Assert.Equal(64, root.GetProperty("inferenceConfig").GetProperty("maxTokens").GetInt32());
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("hi", root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());

        Assert.Contains(events, evt => evt is StartEvent);
        Assert.Contains(events, evt => evt is TextStartEvent);
        Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "hello" });
        Assert.Contains(events, evt => evt is TextEndEvent);
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("hello", Assert.IsType<TextContent>(Assert.Single(done.Message.Content)).Text);
        Assert.Equal(StopReason.EndTurn, done.Message.StopReason);
        Assert.Equal(new Usage(3, 4, 1, 2), done.Message.Usage);
    }

    [Fact]
    public async Task Stream_SignsRequestWithSigV4WhenAwsCredentialsAreProvided()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            capturedRequest = request;
            return EventStreamResponse(
                EventJson("messageStart", """{"role":"assistant"}"""),
                EventJson("messageStop", """{"stopReason":"end_turn"}"""));
        });
        using var client = new HttpClient(handler);
        var clock = new DateTimeOffset(2026, 4, 29, 1, 2, 3, TimeSpan.Zero);
        var provider = new BedrockProvider(client, () => clock);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("sign")] },
            new BedrockOptions
            {
                Region = "us-east-1",
                AccessKeyId = "AKIAEXAMPLE",
                SecretAccessKey = "secret-example",
                SessionToken = "session-token"
            }));

        Assert.NotNull(capturedRequest);
        Assert.Contains(events, evt => evt is DoneEvent);
        Assert.Equal("20260429T010203Z", capturedRequest!.Headers.GetValues("x-amz-date").Single());
        Assert.Equal("session-token", capturedRequest.Headers.GetValues("x-amz-security-token").Single());
        Assert.Matches("^[a-f0-9]{64}$", capturedRequest.Headers.GetValues("x-amz-content-sha256").Single());
        var authorization = capturedRequest.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE/20260429/us-east-1/bedrock/aws4_request", authorization, StringComparison.Ordinal);
        Assert.Contains("SignedHeaders=", authorization, StringComparison.Ordinal);
        Assert.Contains("content-type", authorization, StringComparison.Ordinal);
        Assert.Contains("host", authorization, StringComparison.Ordinal);
        Assert.Contains("x-amz-content-sha256", authorization, StringComparison.Ordinal);
        Assert.Contains("x-amz-date", authorization, StringComparison.Ordinal);
        Assert.Contains("x-amz-security-token", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_LoadsSharedCredentialsProfileAndRegion()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_REGION",
            "AWS_DEFAULT_REGION",
            "AWS_PROFILE",
            "AWS_SHARED_CREDENTIALS_FILE",
            "AWS_CONFIG_FILE");
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-profile-");
        try
        {
            var credentialsPath = Path.Combine(tempDir.FullName, "credentials");
            var configPath = Path.Combine(tempDir.FullName, "config");
            await File.WriteAllTextAsync(
                credentialsPath,
                """
                [dev]
                aws_access_key_id = AKIAFROMFILE
                aws_secret_access_key = secret-from-file
                aws_session_token = token-from-file
                """);
            await File.WriteAllTextAsync(
                configPath,
                """
                [profile dev]
                region = us-west-2
                """);

            HttpRequestMessage? capturedRequest = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                capturedRequest = request;
                return EventStreamResponse(
                    EventJson("messageStart", """{"role":"assistant"}"""),
                    EventJson("messageStop", """{"stopReason":"end_turn"}"""));
            });
            using var client = new HttpClient(handler);
            var clock = new DateTimeOffset(2026, 4, 29, 2, 3, 4, TimeSpan.Zero);
            var provider = new BedrockProvider(client, () => clock);

            await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                BuildModel(baseUrl: null),
                new LlmContext { Messages = [new UserMessage("profile")] },
                new BedrockOptions
                {
                    Profile = "dev",
                    CredentialsFile = credentialsPath,
                    ConfigFile = configPath
                }));

            Assert.NotNull(capturedRequest);
            Assert.Equal("bedrock-runtime.us-west-2.amazonaws.com", capturedRequest!.RequestUri!.Host);
            Assert.Equal("token-from-file", capturedRequest.Headers.GetValues("x-amz-security-token").Single());
            var authorization = capturedRequest.Headers.GetValues("Authorization").Single();
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAFROMFILE/20260429/us-west-2/bedrock/aws4_request", authorization, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stream_ReturnsCleanErrorWhenCredentialsAreMissing()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_PROFILE");
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called without credentials"));
        using var client = new HttpClient(handler);
        var provider = new BedrockProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new BedrockOptions()));

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Contains("AWS_BEARER_TOKEN_BEDROCK", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("SigV4 request signing is not yet implemented", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_SignsRequestUsingCredentialProcessOutputWhenStaticCredentialsAreMissing()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_PROFILE",
            "AWS_REGION",
            "AWS_DEFAULT_REGION");
        var processOutput = """
            {
              "Version": 1,
              "AccessKeyId": "AKIAFROMPROC",
              "SecretAccessKey": "proc-secret",
              "SessionToken": "proc-session",
              "Expiration": "2099-01-01T00:00:00Z"
            }
            """;
        var runner = new RecordingProcessRunner(new BedrockProcessResult(0, processOutput, "", TimedOut: false));
        HttpRequestMessage? capturedRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            capturedRequest = request;
            return EventStreamResponse(
                EventJson("messageStart", """{"role":"assistant"}"""),
                EventJson("messageStop", """{"stopReason":"end_turn"}"""));
        });
        using var client = new HttpClient(handler);
        var clock = new DateTimeOffset(2026, 4, 29, 4, 5, 6, TimeSpan.Zero);
        var provider = new BedrockProvider(client, () => clock, runner);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("process")] },
            new BedrockOptions
            {
                Region = "us-east-1",
                CredentialProcess = "helper --profile dev"
            }));

        Assert.NotNull(capturedRequest);
        Assert.Equal("proc-session", capturedRequest!.Headers.GetValues("x-amz-security-token").Single());
        var authorization = capturedRequest.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAFROMPROC/20260429/us-east-1/bedrock/aws4_request", authorization, StringComparison.Ordinal);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("helper", runner.LastRequest!.FileName);
        Assert.Equal(new[] { "--profile", "dev" }, runner.LastRequest.Arguments);
    }

    [Fact]
    public async Task Stream_LoadsCredentialProcessFromProfileWhenStaticCredentialsAreMissing()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_REGION",
            "AWS_DEFAULT_REGION",
            "AWS_PROFILE",
            "AWS_SHARED_CREDENTIALS_FILE",
            "AWS_CONFIG_FILE");
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-credprocess-");
        try
        {
            var credentialsPath = Path.Combine(tempDir.FullName, "credentials");
            var configPath = Path.Combine(tempDir.FullName, "config");
            await File.WriteAllTextAsync(credentialsPath, "");
            await File.WriteAllTextAsync(
                configPath,
                """
                [profile dev]
                region = us-west-2
                credential_process = "C:/bin/helper.exe" --profile dev
                """);

            var processOutput = """
                {
                  "Version": 1,
                  "AccessKeyId": "AKIAFROMPROFILEPROC",
                  "SecretAccessKey": "profile-proc-secret",
                  "Expiration": "2099-01-01T00:00:00Z"
                }
                """;
            var runner = new RecordingProcessRunner(new BedrockProcessResult(0, processOutput, "", TimedOut: false));
            HttpRequestMessage? capturedRequest = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                capturedRequest = request;
                return EventStreamResponse(
                    EventJson("messageStart", """{"role":"assistant"}"""),
                    EventJson("messageStop", """{"stopReason":"end_turn"}"""));
            });
            using var client = new HttpClient(handler);
            var clock = new DateTimeOffset(2026, 4, 29, 5, 6, 7, TimeSpan.Zero);
            var provider = new BedrockProvider(client, () => clock, runner);

            await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                BuildModel(baseUrl: null),
                new LlmContext { Messages = [new UserMessage("profile-process")] },
                new BedrockOptions
                {
                    Profile = "dev",
                    CredentialsFile = credentialsPath,
                    ConfigFile = configPath
                }));

            Assert.NotNull(capturedRequest);
            Assert.Equal("bedrock-runtime.us-west-2.amazonaws.com", capturedRequest!.RequestUri!.Host);
            Assert.False(capturedRequest.Headers.Contains("x-amz-security-token"));
            var authorization = capturedRequest.Headers.GetValues("Authorization").Single();
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAFROMPROFILEPROC/20260429/us-west-2/bedrock/aws4_request", authorization, StringComparison.Ordinal);
            Assert.NotNull(runner.LastRequest);
            Assert.Equal("C:/bin/helper.exe", runner.LastRequest!.FileName);
            Assert.Equal(new[] { "--profile", "dev" }, runner.LastRequest.Arguments);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stream_SurfacesCredentialProcessErrorVerbatim()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_PROFILE");
        var runner = new RecordingProcessRunner(new BedrockProcessResult(7, "", "helper exploded", TimedOut: false));
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ =>
            throw new InvalidOperationException("HTTP should not be called when credential_process fails"));
        using var client = new HttpClient(handler);
        var provider = new BedrockProvider(client, clock: null, runner);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("fail")] },
            new BedrockOptions
            {
                Region = "us-east-1",
                CredentialProcess = "helper"
            }));

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Contains("credential_process exited with status 7", error.Error, StringComparison.Ordinal);
        Assert.Contains("helper exploded", error.Error, StringComparison.Ordinal);
        Assert.NotNull(runner.LastRequest);
    }

    [Fact]
    public async Task Stream_SignsRequestWithCredentialsFromAssumeRoleWithWebIdentity()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_PROFILE",
            "AWS_REGION",
            "AWS_DEFAULT_REGION",
            "AWS_WEB_IDENTITY_TOKEN_FILE",
            "AWS_ROLE_ARN",
            "AWS_ROLE_SESSION_NAME");
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-webidentity-integration-");
        try
        {
            var tokenPath = Path.Combine(tempDir.FullName, "token");
            await File.WriteAllTextAsync(tokenPath, "jwt-from-pod");

            HttpRequestMessage? bedrockRequest = null;
            HttpRequestMessage? stsRequest = null;
            string? stsBody = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                if (request.RequestUri!.Host.StartsWith("sts.", StringComparison.Ordinal))
                {
                    stsRequest = request;
                    stsBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            <AssumeRoleWithWebIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
                              <AssumeRoleWithWebIdentityResult>
                                <Credentials>
                                  <AccessKeyId>ASIAWEBID</AccessKeyId>
                                  <SecretAccessKey>webid-secret</SecretAccessKey>
                                  <SessionToken>webid-session</SessionToken>
                                  <Expiration>2099-01-01T00:00:00Z</Expiration>
                                </Credentials>
                              </AssumeRoleWithWebIdentityResult>
                            </AssumeRoleWithWebIdentityResponse>
                            """,
                            Encoding.UTF8,
                            "text/xml")
                    };
                }

                bedrockRequest = request;
                return EventStreamResponse(
                    EventJson("messageStart", """{"role":"assistant"}"""),
                    EventJson("messageStop", """{"stopReason":"end_turn"}"""));
            });
            using var client = new HttpClient(handler);
            var clock = new DateTimeOffset(2026, 4, 29, 6, 7, 8, TimeSpan.Zero);
            var provider = new BedrockProvider(client, () => clock);

            await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                BuildModel(),
                new LlmContext { Messages = [new UserMessage("webidentity")] },
                new BedrockOptions
                {
                    Region = "us-east-1",
                    WebIdentityTokenFile = tokenPath,
                    WebIdentityRoleArn = "arn:aws:iam::123456789012:role/tau",
                    WebIdentityRoleSessionName = "tau-pod"
                }));

            Assert.NotNull(stsRequest);
            Assert.Equal("sts.us-east-1.amazonaws.com", stsRequest!.RequestUri!.Host);
            Assert.Contains("WebIdentityToken=jwt-from-pod", stsBody, StringComparison.Ordinal);
            Assert.Contains("RoleSessionName=tau-pod", stsBody, StringComparison.Ordinal);

            Assert.NotNull(bedrockRequest);
            Assert.Equal("webid-session", bedrockRequest!.Headers.GetValues("x-amz-security-token").Single());
            var authorization = bedrockRequest.Headers.GetValues("Authorization").Single();
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=ASIAWEBID/20260429/us-east-1/bedrock/aws4_request", authorization, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stream_SurfacesStsErrorWhenAssumeRoleWithWebIdentityFails()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_PROFILE",
            "AWS_REGION",
            "AWS_DEFAULT_REGION",
            "AWS_WEB_IDENTITY_TOKEN_FILE",
            "AWS_ROLE_ARN",
            "AWS_ROLE_SESSION_NAME");
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-webidentity-err-");
        try
        {
            var tokenPath = Path.Combine(tempDir.FullName, "token");
            await File.WriteAllTextAsync(tokenPath, "jwt");
            var bedrockCalled = false;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                if (request.RequestUri!.Host.StartsWith("sts.", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            """
                            <ErrorResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
                              <Error>
                                <Type>Sender</Type>
                                <Code>InvalidIdentityToken</Code>
                                <Message>token expired</Message>
                              </Error>
                            </ErrorResponse>
                            """,
                            Encoding.UTF8,
                            "text/xml")
                    };
                }

                bedrockCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var provider = new BedrockProvider(client);

            var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                BuildModel(),
                new LlmContext { Messages = [new UserMessage("err")] },
                new BedrockOptions
                {
                    Region = "us-east-1",
                    WebIdentityTokenFile = tokenPath,
                    WebIdentityRoleArn = "arn:aws:iam::123456789012:role/tau"
                }));

            var error = Assert.Single(events.OfType<ErrorEvent>());
            Assert.Contains("InvalidIdentityToken", error.Error, StringComparison.Ordinal);
            Assert.Contains("token expired", error.Error, StringComparison.Ordinal);
            Assert.False(bedrockCalled);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stream_SignsRequestWithEcsContainerCredentials()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_PROFILE",
            "AWS_REGION",
            "AWS_DEFAULT_REGION",
            "AWS_WEB_IDENTITY_TOKEN_FILE",
            "AWS_ROLE_ARN",
            "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
            "AWS_CONTAINER_CREDENTIALS_FULL_URI",
            "AWS_CONTAINER_AUTHORIZATION_TOKEN",
            "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE",
            "AWS_EC2_METADATA_DISABLED");

        HttpRequestMessage? ecsRequest = null;
        HttpRequestMessage? bedrockRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            if (request.RequestUri!.Host == "169.254.170.2")
            {
                ecsRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "AccessKeyId": "ASIAECS",
                          "SecretAccessKey": "ecs-secret",
                          "Token": "ecs-token",
                          "Expiration": "2099-01-01T00:00:00Z"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            bedrockRequest = request;
            return EventStreamResponse(
                EventJson("messageStart", """{"role":"assistant"}"""),
                EventJson("messageStop", """{"stopReason":"end_turn"}"""));
        });
        using var client = new HttpClient(handler);
        var clock = new DateTimeOffset(2026, 4, 29, 7, 8, 9, TimeSpan.Zero);
        var provider = new BedrockProvider(client, () => clock);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("ecs")] },
            new BedrockOptions
            {
                Region = "us-east-1",
                ContainerCredentialsRelativeUri = "/v2/credentials/abc",
                ContainerAuthorizationToken = "Bearer pod-token"
            }));

        Assert.NotNull(ecsRequest);
        Assert.Equal("/v2/credentials/abc", ecsRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer pod-token", ecsRequest.Headers.GetValues("Authorization").Single());

        Assert.NotNull(bedrockRequest);
        Assert.Equal("ecs-token", bedrockRequest!.Headers.GetValues("x-amz-security-token").Single());
        var authorization = bedrockRequest.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=ASIAECS/20260429/us-east-1/bedrock/aws4_request", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_SignsRequestWithAssumeRoleCredentialsFromProfile()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_PROFILE",
            "AWS_REGION",
            "AWS_DEFAULT_REGION",
            "AWS_WEB_IDENTITY_TOKEN_FILE",
            "AWS_ROLE_ARN");
        var tempDir = Directory.CreateTempSubdirectory("tau-bedrock-assume-integration-");
        try
        {
            var credentialsPath = Path.Combine(tempDir.FullName, "credentials");
            var configPath = Path.Combine(tempDir.FullName, "config");
            await File.WriteAllTextAsync(
                credentialsPath,
                """
                [base]
                aws_access_key_id = AKIASRC
                aws_secret_access_key = src-secret
                """);
            await File.WriteAllTextAsync(
                configPath,
                """
                [profile dev]
                region = us-west-2
                role_arn = arn:aws:iam::123456789012:role/dev
                source_profile = base
                """);

            HttpRequestMessage? stsRequest = null;
            HttpRequestMessage? bedrockRequest = null;
            using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
            {
                if (request.RequestUri!.Host.StartsWith("sts.", StringComparison.Ordinal))
                {
                    stsRequest = request;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            <AssumeRoleResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
                              <AssumeRoleResult>
                                <Credentials>
                                  <AccessKeyId>ASIAASSUMED</AccessKeyId>
                                  <SecretAccessKey>assumed-secret</SecretAccessKey>
                                  <SessionToken>assumed-token</SessionToken>
                                  <Expiration>2099-01-01T00:00:00Z</Expiration>
                                </Credentials>
                              </AssumeRoleResult>
                            </AssumeRoleResponse>
                            """,
                            Encoding.UTF8,
                            "text/xml")
                    };
                }

                bedrockRequest = request;
                return EventStreamResponse(
                    EventJson("messageStart", """{"role":"assistant"}"""),
                    EventJson("messageStop", """{"stopReason":"end_turn"}"""));
            });
            using var client = new HttpClient(handler);
            var clock = new DateTimeOffset(2026, 4, 29, 11, 12, 13, TimeSpan.Zero);
            var provider = new BedrockProvider(client, () => clock);

            await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                BuildModel(baseUrl: null),
                new LlmContext { Messages = [new UserMessage("assume")] },
                new BedrockOptions
                {
                    Profile = "dev",
                    CredentialsFile = credentialsPath,
                    ConfigFile = configPath
                }));

            Assert.NotNull(stsRequest);
            Assert.Equal("sts.us-west-2.amazonaws.com", stsRequest!.RequestUri!.Host);
            var stsAuth = stsRequest.Headers.GetValues("Authorization").Single();
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIASRC/20260429/us-west-2/sts/aws4_request", stsAuth, StringComparison.Ordinal);

            Assert.NotNull(bedrockRequest);
            Assert.Equal("bedrock-runtime.us-west-2.amazonaws.com", bedrockRequest!.RequestUri!.Host);
            Assert.Equal("assumed-token", bedrockRequest.Headers.GetValues("x-amz-security-token").Single());
            var bedrockAuth = bedrockRequest.Headers.GetValues("Authorization").Single();
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=ASIAASSUMED/20260429/us-west-2/bedrock/aws4_request", bedrockAuth, StringComparison.Ordinal);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stream_SignsRequestWithImdsInstanceProfileCredentials()
    {
        using var env = new TemporaryEnvironment(
            "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AWS_SESSION_TOKEN",
            "AWS_PROFILE",
            "AWS_REGION",
            "AWS_DEFAULT_REGION",
            "AWS_WEB_IDENTITY_TOKEN_FILE",
            "AWS_ROLE_ARN",
            "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
            "AWS_CONTAINER_CREDENTIALS_FULL_URI",
            "AWS_EC2_METADATA_DISABLED",
            "AWS_EC2_METADATA_V1_DISABLED",
            "AWS_EC2_METADATA_SERVICE_ENDPOINT");

        HttpRequestMessage? bedrockRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            if (request.RequestUri!.Host == "169.254.169.254")
            {
                if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath == "/latest/api/token")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("imds-token-value", Encoding.UTF8, "text/plain")
                    };
                }

                if (request.RequestUri.AbsolutePath == "/latest/meta-data/iam/security-credentials/")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("ec2-role\n", Encoding.UTF8, "text/plain")
                    };
                }

                if (request.RequestUri.AbsolutePath == "/latest/meta-data/iam/security-credentials/ec2-role")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            {
                              "Code": "Success",
                              "AccessKeyId": "ASIAIMDSINT",
                              "SecretAccessKey": "imds-int-secret",
                              "Token": "imds-int-token",
                              "Expiration": "2099-01-01T00:00:00Z"
                            }
                            """,
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                throw new InvalidOperationException($"Unexpected IMDS request: {request.Method} {request.RequestUri}");
            }

            bedrockRequest = request;
            return EventStreamResponse(
                EventJson("messageStart", """{"role":"assistant"}"""),
                EventJson("messageStop", """{"stopReason":"end_turn"}"""));
        });
        using var client = new HttpClient(handler);
        var clock = new DateTimeOffset(2026, 4, 29, 9, 10, 11, TimeSpan.Zero);
        var provider = new BedrockProvider(client, () => clock);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            new LlmContext { Messages = [new UserMessage("imds")] },
            new BedrockOptions
            {
                Region = "us-east-1",
                Ec2MetadataDisabled = false
            }));

        Assert.NotNull(bedrockRequest);
        Assert.Equal("imds-int-token", bedrockRequest!.Headers.GetValues("x-amz-security-token").Single());
        var authorization = bedrockRequest.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=ASIAIMDSINT/20260429/us-east-1/bedrock/aws4_request", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_ConvertsToolsAndParsesToolCallEvents()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            capturedRequest = request;
            return EventStreamResponse(
                EventJson("messageStart", """{"role":"assistant"}"""),
                EventJson("contentBlockStart", """{"contentBlockIndex":0,"start":{"toolUse":{"toolUseId":"toolu_1","name":"read_file"}}}"""),
                EventJson("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":"{\"path\":\"README"}}}"""),
                EventJson("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":".md\"}"}}}"""),
                EventJson("contentBlockStop", """{"contentBlockIndex":0}"""),
                EventJson("messageStop", """{"stopReason":"tool_use"}"""));
        });
        using var client = new HttpClient(handler);
        var provider = new BedrockProvider(client);
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");
        var context = new LlmContext
        {
            Messages =
            [
                new AssistantMessage([new ToolCallContent("call id/1", "read_file", """{"path":"AGENTS.md"}""")]),
                new ToolResultMessage("call id/1", [new TextContent("ok")])
            ],
            Tools = [new Tool("read_file", "Read file", schema.RootElement.Clone())]
        };

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildModel(),
            context,
            new BedrockOptions { BearerToken = "bedrock-token", ToolChoice = "auto" }));

        Assert.NotNull(capturedRequest);
        using var bodyDoc = JsonDocument.Parse(handler.CapturedBody);
        var root = bodyDoc.RootElement;
        Assert.Equal("read_file", root.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("name").GetString());
        Assert.True(root.GetProperty("toolConfig").GetProperty("toolChoice").TryGetProperty("auto", out _));
        Assert.Equal("call_id_1", root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("toolUse").GetProperty("toolUseId").GetString());
        Assert.Equal("call_id_1", root.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("toolResult").GetProperty("toolUseId").GetString());

        Assert.Contains(events, evt => evt is ToolCallStartEvent);
        Assert.Contains(events, evt => evt is ToolCallDeltaEvent);
        Assert.Contains(events, evt => evt is ToolCallEndEvent);
        var firstDelta = Assert.IsType<ToolCallDeltaEvent>(events.First(evt => evt is ToolCallDeltaEvent));
        var partialToolCall = Assert.IsType<ToolCallContent>(Assert.Single(firstDelta.Partial.Content));
        Assert.Equal("""{"path":"README"}""", partialToolCall.Arguments);
        var done = Assert.Single(events.OfType<DoneEvent>());
        var toolCall = Assert.IsType<ToolCallContent>(Assert.Single(done.Message.Content));
        Assert.Equal("toolu_1", toolCall.Id);
        Assert.Equal("read_file", toolCall.Name);
        Assert.Equal("""{"path":"README.md"}""", toolCall.Arguments);
        Assert.Equal(StopReason.ToolUse, done.Message.StopReason);
    }

    [Fact]
    public async Task StreamSimple_AddsClaudeReasoningFields()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("stop after payload", Encoding.UTF8, "text/plain")
        });
        using var client = new HttpClient(handler);
        var provider = new BedrockProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            BuildModel(reasoning: true),
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = "bedrock-token",
                Reasoning = ThinkingLevel.High
            }));

        using var doc = JsonDocument.Parse(handler.CapturedBody);
        var thinking = doc.RootElement.GetProperty("additionalModelRequestFields").GetProperty("thinking");
        Assert.Equal("enabled", thinking.GetProperty("type").GetString());
        Assert.Equal(16_384, thinking.GetProperty("budget_tokens").GetInt32());
    }

    private static Model BuildModel(bool reasoning = false, string? baseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com") => new()
    {
        Id = "anthropic.claude-3-7-sonnet-20250219-v1:0",
        Name = "Claude 3.7 Sonnet",
        Api = "bedrock-converse-stream",
        Provider = "amazon-bedrock",
        BaseUrl = baseUrl,
        Reasoning = reasoning,
        MaxOutputTokens = 4096
    };

    private static HttpResponseMessage EventStreamResponse(params byte[][] frames) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(frames.SelectMany(static frame => frame).ToArray())
        {
            Headers =
            {
                ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.amazon.eventstream")
            }
        }
    };

    private static byte[] EventJson(string eventType, string payload)
    {
        var headers = new List<byte>();
        AddStringHeader(headers, ":message-type", "event");
        AddStringHeader(headers, ":event-type", eventType);
        AddStringHeader(headers, ":content-type", "application/json");
        return Frame(headers.ToArray(), Encoding.UTF8.GetBytes(payload));
    }

    private static byte[] Frame(byte[] headers, byte[] payload)
    {
        var totalLength = 12 + headers.Length + payload.Length + 4;
        var frame = new byte[totalLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), (uint)headers.Length);
        headers.CopyTo(frame.AsSpan(12));
        payload.CopyTo(frame.AsSpan(12 + headers.Length));
        return frame;
    }

    private static void AddStringHeader(List<byte> headers, string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        headers.Add((byte)nameBytes.Length);
        headers.AddRange(nameBytes);
        headers.Add(7);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)valueBytes.Length);
        headers.Add(length[0]);
        headers.Add(length[1]);
        headers.AddRange(valueBytes);
    }

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public TemporaryEnvironment(params string[] names)
        {
            foreach (var name in names)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private sealed class RecordingProcessRunner : IBedrockProcessRunner
    {
        private readonly BedrockProcessResult _result;

        public RecordingProcessRunner(BedrockProcessResult result)
        {
            _result = result;
        }

        public BedrockProcessRequest? LastRequest { get; private set; }

        public Task<BedrockProcessResult> RunAsync(BedrockProcessRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }
}
