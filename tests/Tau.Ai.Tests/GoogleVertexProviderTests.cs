using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Google;

namespace Tau.Ai.Tests;

public sealed class GoogleVertexProviderTests
{
    [Fact]
    public void RegisterAll_UsesDedicatedVertexProvider()
    {
        var registry = new ProviderRegistry();

        BuiltInProviders.RegisterAll(registry);

        Assert.IsType<GoogleVertexProvider>(registry.Get("google-vertex"));
    }

    [Fact]
    public async Task Stream_PreservesApiKeyPath()
    {
        using var handler = new RecordingHandler((_, _) => VertexSseResponse("hello"));
        using var client = new HttpClient(handler);
        var provider = new GoogleVertexProvider(client);

        var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildVertexModel(),
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions { ApiKey = "vertex-api-key" }));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://vertex.example/v1/projects/tau/locations/us-central1/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse", request.Uri.ToString());
        Assert.Equal("vertex-api-key", Assert.Single(request.Headers["x-goog-api-key"]));
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.Contains("\"contents\"", request.Body, StringComparison.Ordinal);
        Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "hello" });
        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal(new Usage(2, 3), done.Message.Usage);
        Assert.Equal(StopReason.EndTurn, done.Message.StopReason);
    }

    [Fact]
    public async Task Stream_ExchangesServiceAccountAdcForBearerToken()
    {
        using var rsa = RSA.Create(2048);
        var credentialsPath = WriteServiceAccountCredentials(rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            string? assertion = null;
            using var handler = new RecordingHandler((request, body) =>
            {
                if (request.RequestUri!.Host.Equals("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase))
                {
                    var form = ParseForm(body);
                    Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
                    assertion = form["assertion"];
                    return JsonResponse("""{"access_token":"service-account-access-token","expires_in":3600,"token_type":"Bearer"}""");
                }

                return VertexSseResponse("from adc");
            });
            using var client = new HttpClient(handler);
            var provider = new GoogleVertexProvider(client);

            var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                BuildVertexModel(),
                new LlmContext { Messages = [new UserMessage("hi")] },
                new GoogleVertexOptions
                {
                    ApiKey = EnvironmentApiKeyResolver.AuthenticatedMarker,
                    CredentialsFile = credentialsPath
                }));

            Assert.NotNull(assertion);
            var payloadJson = DecodeJwtPayload(assertion!);
            Assert.Contains("\"iss\":\"tau-service@tau-project.iam.gserviceaccount.com\"", payloadJson, StringComparison.Ordinal);
            Assert.Contains("\"scope\":\"https://www.googleapis.com/auth/cloud-platform\"", payloadJson, StringComparison.Ordinal);
            Assert.Contains("\"aud\":\"https://oauth2.googleapis.com/token\"", payloadJson, StringComparison.Ordinal);

            Assert.Equal(2, handler.Requests.Count);
            var vertexRequest = handler.Requests[1];
            Assert.Equal("Bearer service-account-access-token", Assert.Single(vertexRequest.Headers["Authorization"]));
            Assert.False(vertexRequest.Headers.ContainsKey("x-goog-api-key"));
            Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "from adc" });
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesVertexOptionsThroughAuthResolution()
    {
        using var rsa = RSA.Create(2048);
        var credentialsPath = WriteServiceAccountCredentials(rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var handler = new RecordingHandler((request, _) =>
            {
                return request.RequestUri!.Host.Equals("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase)
                    ? JsonResponse("""{"access_token":"stream-functions-access-token","expires_in":3600,"token_type":"Bearer"}""")
                    : VertexSseResponse("through stream functions");
            });
            using var client = new HttpClient(handler);
            var registry = new ProviderRegistry();
            registry.Register("google-vertex", () => new GoogleVertexProvider(client), sourceId: "test");

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                BuildVertexModel(),
                new LlmContext { Messages = [new UserMessage("hi")] },
                new GoogleVertexOptions
                {
                    ApiKey = EnvironmentApiKeyResolver.AuthenticatedMarker,
                    CredentialsFile = credentialsPath
                }));

            Assert.Equal("Bearer stream-functions-access-token", Assert.Single(handler.Requests[1].Headers["Authorization"]));
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public async Task Stream_ExchangesAuthorizedUserAdcForBearerToken()
    {
        var credentialsPath = WriteAuthorizedUserCredentials();
        try
        {
            using var handler = new RecordingHandler((request, body) =>
            {
                if (request.RequestUri!.Host.Equals("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase))
                {
                    var form = ParseForm(body);
                    Assert.Equal("refresh_token", form["grant_type"]);
                    Assert.Equal("tau-client-id", form["client_id"]);
                    Assert.Equal("tau-client-secret", form["client_secret"]);
                    Assert.Equal("tau-refresh-token", form["refresh_token"]);
                    return JsonResponse("""{"access_token":"authorized-user-access-token","expires_in":3600,"token_type":"Bearer"}""");
                }

                return VertexSseResponse("from refresh");
            });
            using var client = new HttpClient(handler);
            var provider = new GoogleVertexProvider(client);

            var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                BuildVertexModel(),
                new LlmContext { Messages = [new UserMessage("hi")] },
                new GoogleVertexOptions
                {
                    ApiKey = EnvironmentApiKeyResolver.AuthenticatedMarker,
                    CredentialsFile = credentialsPath
                }));

            Assert.Equal(2, handler.Requests.Count);
            var vertexRequest = handler.Requests[1];
            Assert.Equal("Bearer authorized-user-access-token", Assert.Single(vertexRequest.Headers["Authorization"]));
            Assert.Contains(events, evt => evt is TextDeltaEvent { Delta: "from refresh" });
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public async Task Stream_ReportsUnsupportedAdcTypeAsErrorEvent()
    {
        var credentialsPath = Path.Combine(Path.GetTempPath(), $"tau-vertex-adc-{Guid.NewGuid():N}.json");
        File.WriteAllText(credentialsPath, """{"type":"external_account"}""");
        try
        {
            using var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("HTTP should not be called"));
            using var client = new HttpClient(handler);
            var provider = new GoogleVertexProvider(client);

            var events = await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                BuildVertexModel(),
                new LlmContext { Messages = [new UserMessage("hi")] },
                new GoogleVertexOptions
                {
                    ApiKey = EnvironmentApiKeyResolver.AuthenticatedMarker,
                    CredentialsFile = credentialsPath
                }));

            var error = Assert.Single(events.OfType<ErrorEvent>());
            Assert.Contains("Unsupported Vertex ADC credential type 'external_account'", error.Error, StringComparison.Ordinal);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public async Task Stream_UsesCredentialProjectIdAndOptionLocationForDefaultEndpoint()
    {
        using var rsa = RSA.Create(2048);
        var credentialsPath = WriteServiceAccountCredentials(rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var handler = new RecordingHandler((request, _) =>
            {
                if (request.RequestUri!.Host.Equals("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""{"access_token":"access-token","expires_in":3600,"token_type":"Bearer"}""");
                }

                return VertexSseResponse("project from adc");
            });
            using var client = new HttpClient(handler);
            var provider = new GoogleVertexProvider(client);
            var model = BuildVertexModel(baseUrl: string.Empty);

            await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new GoogleVertexOptions
                {
                    ApiKey = EnvironmentApiKeyResolver.AuthenticatedMarker,
                    CredentialsFile = credentialsPath,
                    Location = "europe-west4"
                }));

            Assert.Equal("https://europe-west4-aiplatform.googleapis.com/v1/projects/tau-project/locations/europe-west4/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse", handler.Requests[1].Uri.ToString());
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public async Task StreamSimple_UsesCustomThinkingBudget()
    {
        using var handler = new RecordingHandler((_, _) => VertexSseResponse("budget"));
        using var client = new HttpClient(handler);
        var provider = new GoogleVertexProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.StreamSimple(
            BuildVertexModel(),
            new LlmContext { Messages = [new UserMessage("think")] },
            new SimpleStreamOptions
            {
                ApiKey = "vertex-api-key",
                Reasoning = ThinkingLevel.High,
                ThinkingBudgets = new ThinkingBudgets { High = 33_333 }
            }));

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var thinking = doc.RootElement
            .GetProperty("generationConfig")
            .GetProperty("thinkingConfig");
        Assert.Equal(33_333, thinking.GetProperty("thinkingBudget").GetInt32());
    }

    [Fact]
    public async Task Stream_AddsToolChoiceAndExplicitThinkingOptions()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""");
        using var handler = new RecordingHandler((_, _) => VertexSseResponse("tool choice"));
        using var client = new HttpClient(handler);
        var provider = new GoogleVertexProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BuildVertexModel(),
            new LlmContext
            {
                Messages = [new UserMessage("think")],
                Tools = [new Tool("read_file", "Read file", schema.RootElement.Clone())]
            },
            new GoogleVertexOptions
            {
                ApiKey = "vertex-api-key",
                ToolChoice = "any",
                Thinking = new GoogleThinkingOptions
                {
                    Enabled = true,
                    BudgetTokens = 7_654
                }
            }));

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var root = doc.RootElement;
        Assert.Equal("ANY", root
            .GetProperty("toolConfig")
            .GetProperty("functionCallingConfig")
            .GetProperty("mode")
            .GetString());
        Assert.Equal(7_654, root
            .GetProperty("generationConfig")
            .GetProperty("thinkingConfig")
            .GetProperty("thinkingBudget")
            .GetInt32());
    }

    private static Model BuildVertexModel(string? baseUrl = "https://vertex.example/v1/projects/tau/locations/us-central1/publishers/google") => new()
    {
        Id = "gemini-2.5-flash",
        Name = "Gemini 2.5 Flash Vertex",
        Api = "google-vertex",
        Provider = "google-vertex",
        BaseUrl = baseUrl,
        Reasoning = true
    };

    private static string WriteServiceAccountCredentials(string privateKey)
    {
        var credentialsPath = Path.Combine(Path.GetTempPath(), $"tau-vertex-adc-{Guid.NewGuid():N}.json");
        var normalizedPrivateKey = privateKey.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        File.WriteAllText(credentialsPath, $$"""
        {
          "type": "service_account",
          "project_id": "tau-project",
          "private_key": "{{normalizedPrivateKey}}",
          "client_email": "tau-service@tau-project.iam.gserviceaccount.com",
          "token_uri": "https://oauth2.googleapis.com/token"
        }
        """);
        return credentialsPath;
    }

    private static string WriteAuthorizedUserCredentials()
    {
        var credentialsPath = Path.Combine(Path.GetTempPath(), $"tau-vertex-adc-{Guid.NewGuid():N}.json");
        File.WriteAllText(credentialsPath, """
        {
          "type": "authorized_user",
          "client_id": "tau-client-id",
          "client_secret": "tau-client-secret",
          "refresh_token": "tau-refresh-token",
          "token_uri": "https://oauth2.googleapis.com/token",
          "quota_project_id": "tau-quota-project"
        }
        """);
        return credentialsPath;
    }

    private static HttpResponseMessage VertexSseResponse(string text) => OpenAiResponsesProviderTests.SseResponse(
        "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + text + "\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":2,\"candidatesTokenCount\":3}}\n\n");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static Dictionary<string, string> ParseForm(string body)
    {
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var index = part.IndexOf('=', StringComparison.Ordinal);
                return index < 0
                    ? (Key: WebUtility.UrlDecode(part), Value: string.Empty)
                    : (Key: WebUtility.UrlDecode(part[..index]), Value: WebUtility.UrlDecode(part[(index + 1)..]));
            })
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static string DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        Assert.True(parts.Length >= 2);
        return Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = header.Value.ToList();
                }
            }

            if (request.Headers.Authorization is AuthenticationHeaderValue authorization)
            {
                headers["Authorization"] = [authorization.ToString()];
            }

            Requests.Add(new RecordedRequest(request.RequestUri!, body, headers));
            return _responseFactory(request, body);
        }
    }

    private sealed record RecordedRequest(Uri Uri, string Body, IReadOnlyDictionary<string, List<string>> Headers);
}
