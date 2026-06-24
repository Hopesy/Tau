using System.Text.Json;
using System.Net;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Faux;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

public sealed class AiPublicApiCompileSampleTests
{
    [Fact]
    public async Task PublicApiSample_CompilesAndRunsCoreStreamAuthModelAndValidationSurface()
    {
        var schema = CreateSchema();
        var tool = new Tool("count", "Counts items.", schema);
        var toolCall = new ToolCallContent("call-1", "count", """{"count":"7","enabled":"true"}""");

        var validated = ToolArgumentValidator.ValidateToolCall([tool], toolCall);
        Assert.Equal(7, validated.GetProperty("count").GetInt32());
        Assert.True(validated.GetProperty("enabled").GetBoolean());

        var enumSchema = JsonSchemaHelpers.StringEnum(["small", "large"], "Sample size.", "small");
        Assert.Equal("small", enumSchema.GetProperty("default").GetString());
        Assert.Equal("1h6qa0qrowduu", ShortHash.Compute("hello"));
        var partialJson = StreamingJsonParser.ParseStreamingJson("""{"size":"large","count":2,"partial":""");
        Assert.Equal("large", partialJson.GetProperty("size").GetString());
        Assert.False(partialJson.TryGetProperty("partial", out _));

        using var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        response.Headers.TryAddWithoutValidation("X-Sample", "sample");
        Assert.Equal("sample", AiHeaderUtilities.ToDictionary(response)["x-sample"]);

        var provider = new SampleProvider();
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider, sourceId: "sample-source");
        Assert.Contains(provider.Api, registry.RegisteredApis);

        var model = new Model
        {
            Provider = "sample-public-ai-provider",
            Id = "sample-model",
            Name = "Sample Model",
            Api = provider.Api,
            Reasoning = true,
            ContextWindow = 128_000,
            MaxOutputTokens = 8_192,
            Cost = new ModelCost(1m, 2m, 0.1m, 0.5m),
            Compat = new ModelCompatibility
            {
                SupportsReasoningEffort = true,
                MaxTokensField = "max_completion_tokens",
                SupportsUsageInStreaming = true
            }
        };

        var catalog = new ModelCatalog();
        catalog.RegisterModel(model);
        Assert.True(ModelCatalog.ModelsAreEqual(model, catalog.GetModel(model.Provider, model.Id)));
        Assert.True(ModelCatalog.SupportsXhigh(new Model
        {
            Provider = "sample",
            Id = "gpt-5.4-sample",
            Name = "Sample GPT",
            Api = provider.Api
        }));

        var cost = ModelCatalog.CalculateCost(
            model,
            new Usage(InputTokens: 1_000_000, OutputTokens: 2_000_000, ServiceTier: "priority"));
        Assert.Equal(10m, cost.Total);

        var context = new LlmContext(
            SystemPrompt: "You are a sample.",
            Messages: [new UserMessage([new TextContent("hello"), new ImageContent("base64", "image/png")])],
            Tools: [tool]);
        var options = new SimpleStreamOptions
        {
            ApiKey = "explicit-sample-key",
            Temperature = 0.2f,
            MaxTokens = 64,
            Reasoning = ThinkingLevel.Low,
            Transport = StreamTransport.Sse,
            CacheRetention = CacheRetention.Short,
            SessionId = "sample-session",
            Signal = CancellationToken.None,
            OnResponse = (_, _) => ValueTask.CompletedTask,
            Headers = new Dictionary<string, string> { ["x-sample"] = "1" },
            Metadata = new Dictionary<string, object> { ["source"] = "public-api-sample" }
        };

        var openAiOptions = new OpenAiOptions
        {
            ToolChoice = OpenAiToolChoice.Function("count"),
            ReasoningEffort = "high"
        };
        Assert.Equal("function:count", openAiOptions.ToolChoice.ToString());

        var anthropicOptions = new AnthropicOptions
        {
            ThinkingEnabled = true,
            ThinkingBudgetTokens = 1024,
            Effort = "high",
            ThinkingDisplay = "summarized",
            InterleavedThinking = true,
            ToolChoice = AnthropicToolChoice.Tool("count")
        };
        Assert.Equal("tool:count", anthropicOptions.ToolChoice.ToString());

        var googleOptions = new GoogleOptions
        {
            ToolChoice = "any",
            Thinking = new GoogleThinkingOptions
            {
                Enabled = true,
                BudgetTokens = 1024
            }
        };
        Assert.Equal("any", googleOptions.ToolChoice);

        var vertexOptions = new GoogleVertexOptions
        {
            Project = "sample-project",
            Location = "us-central1",
            ToolChoice = "auto",
            Thinking = new GoogleThinkingOptions
            {
                Enabled = false
            }
        };
        Assert.Equal("us-central1", vertexOptions.Location);

        var geminiCliOptions = new GoogleGeminiCliOptions
        {
            ProjectId = "sample-cli-project",
            ToolChoice = "none",
            Thinking = new GoogleThinkingOptions
            {
                Enabled = true,
                Level = "HIGH"
            }
        };
        Assert.Equal("sample-cli-project", geminiCliOptions.ProjectId);

        var stream = StreamFunctions.StreamSimple(registry, model, context, options);
        var events = await CollectEventsAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
        var result = await stream.ResultAsync.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<StartEvent>(events[0]);
        Assert.IsType<DoneEvent>(events[^1]);
        Assert.Equal("sample-model", result.Model);
        Assert.Equal("sample-public-ai-provider", result.Provider);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal("hello from sample-model", ReadText(result.Content));
        Assert.False(ContextOverflowDetector.IsContextOverflow(result, contextWindow: 128_000));
        Assert.Equal("explicit-sample-key", provider.LastOptions?.ApiKey);
        Assert.Equal("sample-session", provider.LastOptions?.SessionId);
        Assert.Equal("You are a sample.", provider.LastContext?.SystemPrompt);

        var faux = Faux.Register(registry);
        faux.SetResponses([
            Faux.AssistantMessage(
                [Faux.Thinking("sample thought"), Faux.ToolCall("count", """{"count":1}"""), Faux.Text("sample done")],
                stopReason: StopReason.ToolUse)
        ]);
        var fauxResponse = await StreamFunctions.CompleteAsync(
            registry,
            faux.GetModel(),
            new LlmContext(null, [new UserMessage("use faux")], null),
            new StreamOptions()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StopReason.ToolUse, fauxResponse.StopReason);
        Assert.Equal("sample done", ReadText(fauxResponse.Content.OfType<TextContent>()));
        Assert.Equal(1, faux.State.CallCount);
        faux.Unregister();

        var completed = await StreamFunctions.CompleteSimpleAsync(registry, model, context, options)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello from sample-model", ReadText(completed.Content));

        var authStatus = new ProviderAuthResolver(logSink: NullTauLogSink.Instance)
            .GetStatus(model, explicitApiKey: "explicit-sample-key");
        Assert.True(authStatus.IsConfigured);
        Assert.Equal("explicit", authStatus.Source);

        var builtIns = BuiltInOAuthProviders.GetAll();
        Assert.NotEmpty(builtIns);
        var oauthRegistry = new OAuthProviderRegistry(builtIns);
        Assert.NotEmpty(oauthRegistry.GetProviderInfoList());
        Assert.Contains(oauthRegistry.GetProviderInfoList(), static info => info.UsesCallbackServer);
        var emptyOAuthRegistry = new OAuthProviderRegistry([]);
        Assert.Empty(emptyOAuthRegistry.Providers);

        var stubOAuthProvider = new SampleOAuthProvider();
        emptyOAuthRegistry.Register(stubOAuthProvider);
        var refreshed = await OAuthHelpers.RefreshOAuthTokenAsync(
            emptyOAuthRegistry,
            stubOAuthProvider.Id,
            new OAuthCredentials
            {
                Refresh = "refresh-token",
                Access = "expired-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
            });
        Assert.Equal("refreshed-access-token", refreshed.Access);

        var apiKeyResult = await OAuthHelpers.GetOAuthApiKeyAsync(
            emptyOAuthRegistry,
            stubOAuthProvider.Id,
            new Dictionary<string, OAuthCredentials>(StringComparer.OrdinalIgnoreCase)
            {
                [stubOAuthProvider.Id] = new OAuthCredentials
                {
                    Refresh = "refresh-token",
                    Access = "expired-access-token",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
                }
            });
        Assert.NotNull(apiKeyResult);
        Assert.Equal("refreshed-access-token", apiKeyResult.NewCredentials.Access);
        Assert.Equal("oauth:refreshed-access-token", apiKeyResult.ApiKey);
        emptyOAuthRegistry.Unregister(stubOAuthProvider.Id);
        Assert.Null(emptyOAuthRegistry.TryGet(stubOAuthProvider.Id));

        registry.UnregisterBySource("sample-source");
        Assert.Null(registry.TryGet(provider.Api));
    }

    [Fact]
    public async Task PublicApiSample_SupportsExplicitConfigurationStoreAndAuthResolver()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-ai-public-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "sample-config-provider": {
                      "apiKey": "TAU_SAMPLE_PUBLIC_CONFIG_API_KEY",
                      "authHeader": true,
                      "headers": {
                        "X-From-Provider": "TAU_SAMPLE_PUBLIC_CONFIG_HEADER"
                      },
                      "models": [
                        {
                          "id": "sample-config-model",
                          "headers": {
                            "X-From-Model": "TAU_SAMPLE_PUBLIC_MODEL_HEADER"
                          }
                        }
                      ]
                    }
                  }
                }
                """);

            scope.Set("TAU_SAMPLE_PUBLIC_CONFIG_API_KEY", "public-config-key");
            scope.Set("TAU_SAMPLE_PUBLIC_CONFIG_HEADER", "provider-header-value");
            scope.Set("TAU_SAMPLE_PUBLIC_MODEL_HEADER", "model-header-value");

            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore,
                logSink: NullTauLogSink.Instance);

            var provider = new CapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register(provider.Api, () => provider, sourceId: "sample-config-source");

            var model = new Model
            {
                Provider = "sample-config-provider",
                Id = "sample-config-model",
                Name = "Configured Model",
                Api = provider.Api
            };

            var status = authResolver.GetStatus(model);
            Assert.True(status.IsConfigured);
            Assert.Equal("models.json", status.Source);

            var completed = await StreamFunctions.CompleteSimpleAsync(
                registry,
                model,
                new LlmContext(null, [new UserMessage("configured request")], null),
                new SimpleStreamOptions
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-Explicit"] = "explicit-header",
                        ["X-From-Provider"] = "explicit-provider-header"
                    }
                },
                configurationStore,
                authResolver).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("hello from sample-config-model", ReadText(completed.Content));
            Assert.Equal("public-config-key", provider.LastOptions?.ApiKey);
            Assert.Equal("Bearer public-config-key", provider.LastOptions?.Headers?["Authorization"]);
            Assert.Equal("explicit-provider-header", provider.LastOptions?.Headers?["X-From-Provider"]);
            Assert.Equal("model-header-value", provider.LastOptions?.Headers?["X-From-Model"]);
            Assert.Equal("explicit-header", provider.LastOptions?.Headers?["X-Explicit"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string ReadText(IEnumerable<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

    private static async Task<List<StreamEvent>> CollectEventsAsync(IAsyncEnumerable<StreamEvent> events)
    {
        var collected = new List<StreamEvent>();
        await foreach (var evt in events)
        {
            collected.Add(evt);
        }

        return collected;
    }

    private static JsonElement CreateSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "count": { "type": "integer" },
                "enabled": { "type": "boolean" }
              },
              "required": ["count", "enabled"]
            }
            """);
        return document.RootElement.Clone();
    }

    private sealed class SampleProvider : IStreamProvider
    {
        public string Api => "sample-public-ai-api";

        public StreamOptions? LastOptions { get; private set; }

        public LlmContext? LastContext { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete(model, context, options);

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete(model, context, options);

        private AssistantMessageStream Complete(Model model, LlmContext context, StreamOptions options)
        {
            LastOptions = options;
            LastContext = context;

            var message = new AssistantMessage([new TextContent($"hello from {model.Id}")])
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                StopReason = StopReason.EndTurn,
                Usage = new Usage(InputTokens: 10, OutputTokens: 20)
            };

            var stream = new AssistantMessageStream();
            stream.Push(new StartEvent(new AssistantMessage()));
            stream.Push(new DoneEvent(message));
            return stream;
        }
    }

    private sealed class CapturingProvider : IStreamProvider
    {
        public string Api => "sample-config-api";

        public StreamOptions? LastOptions { get; private set; }

        public LlmContext? LastContext { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            Complete(model, context, options);

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            Complete(model, context, options);

        private AssistantMessageStream Complete(Model model, LlmContext context, StreamOptions options)
        {
            LastOptions = options;
            LastContext = context;

            var message = new AssistantMessage([new TextContent($"hello from {model.Id}")])
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                StopReason = StopReason.EndTurn,
                Usage = new Usage(InputTokens: 10, OutputTokens: 20)
            };

            var stream = new AssistantMessageStream();
            stream.Push(new StartEvent(new AssistantMessage()));
            stream.Push(new DoneEvent(message));
            return stream;
        }
    }

    private sealed class SampleOAuthProvider : IOAuthProvider
    {
        public string Id => "sample-oauth";
        public string Name => "Sample OAuth";
        public bool UsesCallbackServer => true;

        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.FromResult(credentials with
            {
                Access = "refreshed-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });

        public string GetApiKey(OAuthCredentials credentials) => $"oauth:{credentials.Access}";
    }
}
