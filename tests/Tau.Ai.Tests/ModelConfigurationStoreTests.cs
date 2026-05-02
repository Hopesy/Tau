using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

public sealed class ModelConfigurationStoreTests
{
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