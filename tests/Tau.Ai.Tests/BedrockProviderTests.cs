using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Bedrock;

namespace Tau.Ai.Tests;

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
    public async Task Stream_ConvertsToolsAndParsesToolCallEvents()
    {
        HttpRequestMessage? capturedRequest = null;
        using var handler = new OpenAiResponsesProviderTests.StubHandler(request =>
        {
            capturedRequest = request;
            return EventStreamResponse(
                EventJson("messageStart", """{"role":"assistant"}"""),
                EventJson("contentBlockStart", """{"contentBlockIndex":0,"start":{"toolUse":{"toolUseId":"toolu_1","name":"read_file"}}}"""),
                EventJson("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":"{\"path\""}}}"""),
                EventJson("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":":\"README.md\"}"}}}"""),
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
}
