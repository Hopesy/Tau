using System.Text.Json;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

public sealed class ModelConfigurationStoreTests
{
    [Fact]
    public async Task RegisterConfiguredProviders_RegistersOpenAiCompatibleApiFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-dynamic-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "local-proxy": {
                      "baseUrl": "https://proxy.example.test/v1",
                      "api": "custom-openai-api",
                      "apiKind": "openai-compatible",
                      "apiKey": "DYNAMIC_PROVIDER_KEY",
                      "authHeader": true,
                      "headers": {
                        "X-Provider": "provider-secret"
                      },
                      "models": [
                        {
                          "id": "proxy-model",
                          "name": "Proxy Model"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("DYNAMIC_PROVIDER_KEY", "dynamic-key");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"choices":[{"delta":{"content":"ok"},"finish_reason":null}]}

                data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("local-proxy", "proxy-model");
            var registry = new ProviderRegistry();

            BuiltInProviders.RegisterAll(registry, configurationStore, client);

            Assert.Equal("custom-openai-api", model.Api);
            Assert.Contains("custom-openai-api", registry.RegisteredApis);

            var events = await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions()));

            Assert.Equal("proxy.example.test", handler.RequestUri!.Host);
            Assert.Equal("/v1/chat/completions", handler.RequestUri.AbsolutePath);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("dynamic-key", request.Headers.Authorization.Parameter);
            Assert.Contains("provider-secret", request.Headers.GetValues("X-Provider"));

            using var body = JsonDocument.Parse(handler.CapturedBody);
            Assert.Equal("proxy-model", body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());

            var done = Assert.Single(events.OfType<DoneEvent>());
            Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(done.Message.Content)).Text);
            Assert.Equal("custom-openai-api", done.Message.Api);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RegisterConfiguredProviders_IgnoresUnknownApiWithoutOpenAiCompatibleMarker()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-dynamic-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "local-proxy": {
                      "baseUrl": "https://proxy.example.test/v1",
                      "api": "custom-openai-api",
                      "apiKey": "literal-key",
                      "models": [
                        { "id": "proxy-model" }
                      ]
                    }
                  }
                }
                """);

            var registry = new ProviderRegistry();
            BuiltInProviders.RegisterAll(registry, new ModelConfigurationStore([modelsPath]));

            Assert.DoesNotContain("custom-openai-api", registry.RegisteredApis);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task StreamFunctions_ResolvesModelsJsonRequestAuthAndHeaders()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "custom-provider": {
                      "apiKey": "TAU_TEST_MODELS_API_KEY",
                      "authHeader": true,
                      "headers": {
                        "X-Provider": "TAU_TEST_PROVIDER_HEADER",
                        "X-Shared": "provider-value"
                      },
                      "models": [
                        {
                          "id": "custom-model",
                          "headers": {
                            "X-Model": "TAU_TEST_MODEL_HEADER",
                            "X-Shared": "model-value"
                          }
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("TAU_TEST_MODELS_API_KEY", "models-key");
            scope.Set("TAU_TEST_PROVIDER_HEADER", "provider-secret");
            scope.Set("TAU_TEST_MODEL_HEADER", "model-secret");

            var provider = new CapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("test-api", () => provider, sourceId: "test");

            var model = new Model
            {
                Id = "custom-model",
                Name = "Custom Model",
                Api = "test-api",
                Provider = "custom-provider"
            };

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-Explicit"] = "explicit-value",
                        ["X-Shared"] = "explicit-value"
                    }
                }));

            Assert.Equal("models-key", provider.CapturedOptions!.ApiKey);
            Assert.Equal("provider-secret", provider.CapturedOptions.Headers!["X-Provider"]);
            Assert.Equal("model-secret", provider.CapturedOptions.Headers["X-Model"]);
            Assert.Equal("explicit-value", provider.CapturedOptions.Headers["X-Explicit"]);
            Assert.Equal("explicit-value", provider.CapturedOptions.Headers["X-Shared"]);
            Assert.Equal("Bearer models-key", provider.CapturedOptions.Headers["Authorization"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesExplicitApiKeyAndSimpleOptionsWhenApplyingModelsJsonAuthHeader()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "custom-provider": {
                      "apiKey": "models-key",
                      "authHeader": true,
                      "models": [
                        { "id": "custom-model" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new CapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("test-api", () => provider, sourceId: "test");

            var model = new Model
            {
                Id = "custom-model",
                Name = "Custom Model",
                Api = "test-api",
                Provider = "custom-provider"
            };

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions
                {
                    ApiKey = "explicit-key",
                    Reasoning = ThinkingLevel.High
                }));

            Assert.Equal("explicit-key", provider.CapturedSimpleOptions!.ApiKey);
            Assert.Equal(ThinkingLevel.High, provider.CapturedSimpleOptions.Reasoning);
            Assert.Equal("Bearer explicit-key", provider.CapturedSimpleOptions.Headers!["Authorization"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class CapturingProvider : IStreamProvider
    {
        public string Api => "test-api";
        public StreamOptions? CapturedOptions { get; private set; }
        public SimpleStreamOptions? CapturedSimpleOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = options;
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage { Content = [new TextContent("ok")] }));
            return stream;
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        {
            CapturedSimpleOptions = options;
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage { Content = [new TextContent("ok")] }));
            return stream;
        }
    }
}
