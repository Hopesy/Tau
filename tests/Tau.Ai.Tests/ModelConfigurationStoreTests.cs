using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Cloudflare;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiResponses;
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
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_ResolvesCloudflareWorkersAiBaseUrlTemplate()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-cloudflare-workers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, $$"""
                {
                  "providers": {
                    "cloudflare-workers-ai": {
                      "baseUrl": "{{CloudflareAuthResolver.WorkersAiBaseUrl}}",
                      "api": "cloudflare-workers-ai-openai-api",
                      "apiKind": "openai-compatible",
                      "models": [
                        {
                          "id": "@cf/meta/llama-3.1-8b-instruct",
                          "name": "Workers AI Llama"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("CLOUDFLARE_API_KEY", "cf-key");
            scope.Set("CLOUDFLARE_ACCOUNT_ID", "acct_123");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"choices":[{"delta":{"content":"ok"},"finish_reason":null}]}

                data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("cloudflare-workers-ai", "@cf/meta/llama-3.1-8b-instruct");
            var registry = new ProviderRegistry();

            BuiltInProviders.RegisterAll(registry, configurationStore, client);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions()));

            Assert.Equal("api.cloudflare.com", handler.RequestUri!.Host);
            Assert.Equal("/client/v4/accounts/acct_123/ai/v1/chat/completions", handler.RequestUri.AbsolutePath);
            Assert.DoesNotContain("{CLOUDFLARE_ACCOUNT_ID}", handler.RequestUri.ToString(), StringComparison.Ordinal);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("cf-key", request.Headers.Authorization.Parameter);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_ResolvesCloudflareAiGatewayAuthHeadersAndBaseUrl()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-cloudflare-gateway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, $$"""
                {
                  "providers": {
                    "cloudflare-ai-gateway": {
                      "baseUrl": "{{CloudflareAuthResolver.AiGatewayCompatBaseUrl}}",
                      "api": "cloudflare-ai-gateway-openai-api",
                      "apiKind": "openai-compatible",
                      "authHeader": true,
                      "headers": {
                        "x-api-key": "configured-x-api-key"
                      },
                      "models": [
                        {
                          "id": "openai/gpt-4o-mini",
                          "name": "Gateway OpenAI"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("CLOUDFLARE_API_KEY", "cf-gateway-key");
            scope.Set("CLOUDFLARE_ACCOUNT_ID", "acct_123");
            scope.Set("CLOUDFLARE_GATEWAY_ID", "gw_456");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"choices":[{"delta":{"content":"ok"},"finish_reason":null}]}

                data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("cloudflare-ai-gateway", "openai/gpt-4o-mini");
            var registry = new ProviderRegistry();

            BuiltInProviders.RegisterAll(registry, configurationStore, client);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions()));

            Assert.Equal("gateway.ai.cloudflare.com", handler.RequestUri!.Host);
            Assert.Equal("/v1/acct_123/gw_456/compat/chat/completions", handler.RequestUri.AbsolutePath);
            Assert.DoesNotContain("{CLOUDFLARE_ACCOUNT_ID}", handler.RequestUri.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("{CLOUDFLARE_GATEWAY_ID}", handler.RequestUri.ToString(), StringComparison.Ordinal);
            var request = Assert.Single(handler.Requests);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal("Bearer cf-gateway-key", Assert.Single(request.Headers.GetValues("cf-aig-authorization")));
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_ResolvesCloudflareAiGatewayOpenAiResponsesBaseUrlAndHeaders()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-cloudflare-gateway-responses-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, $$"""
                {
                  "providers": {
                    "cloudflare-ai-gateway": {
                      "baseUrl": "{{CloudflareAuthResolver.AiGatewayOpenAiBaseUrl}}",
                      "api": "openai-responses",
                      "authHeader": true,
                      "headers": {
                        "x-api-key": "configured-x-api-key"
                      },
                      "models": [
                        {
                          "id": "openai/gpt-4o-mini",
                          "name": "Gateway Responses"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("CLOUDFLARE_API_KEY", "cf-gateway-key");
            scope.Set("CLOUDFLARE_ACCOUNT_ID", "acct_123");
            scope.Set("CLOUDFLARE_GATEWAY_ID", "gw_456");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

                """));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("cloudflare-ai-gateway", "openai/gpt-4o-mini");
            var registry = new ProviderRegistry();
            registry.Register("openai-responses", () => new OpenAiResponsesProvider(client), sourceId: "test");

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions()));

            Assert.Equal("gateway.ai.cloudflare.com", handler.RequestUri!.Host);
            Assert.Equal("/v1/acct_123/gw_456/openai/responses", handler.RequestUri.AbsolutePath);
            Assert.DoesNotContain("{CLOUDFLARE_ACCOUNT_ID}", handler.RequestUri.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("{CLOUDFLARE_GATEWAY_ID}", handler.RequestUri.ToString(), StringComparison.Ordinal);
            var request = Assert.Single(handler.Requests);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal("Bearer cf-gateway-key", Assert.Single(request.Headers.GetValues("cf-aig-authorization")));
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_ResolvesCloudflareAiGatewayAnthropicBaseUrlAndHeaders()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-cloudflare-gateway-anthropic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, $$"""
                {
                  "providers": {
                    "cloudflare-ai-gateway": {
                      "baseUrl": "{{CloudflareAuthResolver.AiGatewayAnthropicBaseUrl}}",
                      "api": "anthropic-messages",
                      "authHeader": true,
                      "headers": {
                        "x-api-key": "configured-x-api-key"
                      },
                      "models": [
                        {
                          "id": "anthropic/claude-3-5-sonnet",
                          "name": "Gateway Anthropic"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("CLOUDFLARE_API_KEY", "cf-gateway-key");
            scope.Set("CLOUDFLARE_ACCOUNT_ID", "acct_123");
            scope.Set("CLOUDFLARE_GATEWAY_ID", "gw_456");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":1,"output_tokens":0}}}

                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

                data: {"type":"content_block_stop","index":0}

                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

                data: {"type":"message_stop"}

                """));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("cloudflare-ai-gateway", "anthropic/claude-3-5-sonnet");
            var registry = new ProviderRegistry();
            registry.Register("anthropic-messages", () => new AnthropicProvider(client), sourceId: "test");

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions()));

            Assert.Equal("gateway.ai.cloudflare.com", handler.RequestUri!.Host);
            Assert.Equal("/v1/acct_123/gw_456/anthropic/v1/messages", handler.RequestUri.AbsolutePath);
            Assert.DoesNotContain("{CLOUDFLARE_ACCOUNT_ID}", handler.RequestUri.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("{CLOUDFLARE_GATEWAY_ID}", handler.RequestUri.ToString(), StringComparison.Ordinal);
            var request = Assert.Single(handler.Requests);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal("Bearer cf-gateway-key", Assert.Single(request.Headers.GetValues("cf-aig-authorization")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_ResolvesCloudflareStoredCredentialEnv()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-cloudflare-auth-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "cloudflare-ai-gateway": {
                    "type": "api_key",
                    "key": "stored-cf-key",
                    "env": {
                      "CLOUDFLARE_ACCOUNT_ID": "stored_acct",
                      "CLOUDFLARE_GATEWAY_ID": "stored_gateway"
                    }
                  }
                }
                """);
            File.WriteAllText(modelsPath, $$"""
                {
                  "providers": {
                    "cloudflare-ai-gateway": {
                      "baseUrl": "{{CloudflareAuthResolver.AiGatewayCompatBaseUrl}}",
                      "api": "cloudflare-ai-gateway-openai-api",
                      "apiKind": "openai-compatible",
                      "models": [
                        {
                          "id": "openai/gpt-4o-mini"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("CLOUDFLARE_API_KEY", "ambient-cf-key");
            scope.Set("CLOUDFLARE_ACCOUNT_ID", "ambient_acct");
            scope.Set("CLOUDFLARE_GATEWAY_ID", "ambient_gateway");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"choices":[{"delta":{"content":"ok"},"finish_reason":null}]}

                data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("cloudflare-ai-gateway", "openai/gpt-4o-mini");
            var registry = new ProviderRegistry();
            var authResolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([authPath]));

            BuiltInProviders.RegisterAll(registry, configurationStore, client);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions(),
                configurationStore,
                authResolver));

            Assert.Equal("/v1/stored_acct/stored_gateway/compat/chat/completions", handler.RequestUri!.AbsolutePath);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("Bearer stored-cf-key", Assert.Single(request.Headers.GetValues("cf-aig-authorization")));
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_CloudflareStoredCredentialDoesNotFallBackToAmbientEnv()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-cloudflare-stored-no-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "cloudflare-ai-gateway": {
                    "type": "api_key",
                    "key": "stored-cf-key"
                  }
                }
                """);
            File.WriteAllText(modelsPath, $$"""
                {
                  "providers": {
                    "cloudflare-ai-gateway": {
                      "baseUrl": "{{CloudflareAuthResolver.AiGatewayCompatBaseUrl}}",
                      "api": "cloudflare-ai-gateway-openai-api",
                      "apiKind": "openai-compatible",
                      "models": [
                        {
                          "id": "openai/gpt-4o-mini"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("CLOUDFLARE_ACCOUNT_ID", "ambient_acct");
            scope.Set("CLOUDFLARE_GATEWAY_ID", "ambient_gateway");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => throw new InvalidOperationException("request should not be sent"));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("cloudflare-ai-gateway", "openai/gpt-4o-mini");
            var registry = new ProviderRegistry();
            var authResolver = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([authPath]));

            BuiltInProviders.RegisterAll(registry, configurationStore, client);

            var events = await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions(),
                configurationStore,
                authResolver));

            var error = Assert.Single(events.OfType<ErrorEvent>());
            Assert.Contains("Cloudflare account ID is required", error.Error, StringComparison.Ordinal);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_CloudflareAiGatewayMissingGatewayIdReturnsAuthError()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-cloudflare-missing-gateway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, $$"""
                {
                  "providers": {
                    "cloudflare-ai-gateway": {
                      "baseUrl": "{{CloudflareAuthResolver.AiGatewayCompatBaseUrl}}",
                      "api": "cloudflare-ai-gateway-openai-api",
                      "apiKind": "openai-compatible",
                      "models": [
                        {
                          "id": "openai/gpt-4o-mini"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);
            scope.Set("CLOUDFLARE_API_KEY", "cf-gateway-key");
            scope.Set("CLOUDFLARE_ACCOUNT_ID", "acct_123");

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => throw new InvalidOperationException("request should not be sent"));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("cloudflare-ai-gateway", "openai/gpt-4o-mini");
            var registry = new ProviderRegistry();

            BuiltInProviders.RegisterAll(registry, configurationStore, client);

            var events = await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions()));

            var error = Assert.Single(events.OfType<ErrorEvent>());
            Assert.Contains("Cloudflare AI Gateway ID is required", error.Error, StringComparison.Ordinal);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public void Constructor_NormalizesKnownApiAliasesInCustomModels()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-dynamic-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openrouter": {
                      "api": "openai-completions",
                      "apiKind": "openai-compatible",
                      "baseUrl": "https://openrouter.example.test/v1",
                      "models": [
                        { "id": "proxy-model" }
                      ]
                    },
                    "google": {
                      "api": "google-generative-ai",
                      "models": [
                        { "id": "gemini-alias" }
                      ]
                    }
                  }
                }
                """);

            var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([modelsPath]));

            Assert.Equal("openai-chat-completions", catalog.GetModel("openrouter", "proxy-model").Api);
            Assert.Equal("google-generative-language", catalog.GetModel("google", "gemini-alias").Api);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
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
            DeleteDirectoryWithRetry(tempDir);
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
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_ResolvesModelsJsonScopedEnvironmentAndExplicitOverrides()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-env-{Guid.NewGuid():N}");
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
                      "apiKey": "TAU_SCOPED_MODELS_API_KEY",
                      "authHeader": true,
                      "headers": {
                        "X-Provider": "TAU_SCOPED_PROVIDER_HEADER"
                      },
                      "options": {
                        "env": {
                          "TAU_SCOPED_MODELS_API_KEY": "configured-key",
                          "TAU_SCOPED_PROVIDER_HEADER": "configured-provider",
                          "TAU_SCOPED_OPTION_HEADER": "configured-option",
                          "TAU_SCOPED_ONLY_CONFIGURED": "configured-only"
                        }
                      },
                      "models": [
                        {
                          "id": "custom-model",
                          "headers": {
                            "X-Model": "TAU_SCOPED_MODEL_HEADER"
                          },
                          "options": {
                            "env": {
                              "TAU_SCOPED_MODEL_HEADER": "configured-model",
                              "TAU_SCOPED_OPTION_HEADER": "model-option"
                            },
                            "headers": {
                              "X-Option": "TAU_SCOPED_OPTION_HEADER"
                            }
                          }
                        }
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

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions
                {
                    Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TAU_SCOPED_MODELS_API_KEY"] = "explicit-key",
                        ["TAU_SCOPED_PROVIDER_HEADER"] = "explicit-provider",
                        ["TAU_SCOPED_EXPLICIT_ONLY"] = "explicit-only"
                    }
                }));

            var options = provider.CapturedOptions!;
            Assert.Equal("explicit-key", options.ApiKey);
            Assert.Equal("explicit-provider", options.Headers!["X-Provider"]);
            Assert.Equal("configured-model", options.Headers["X-Model"]);
            Assert.Equal("model-option", options.Headers["X-Option"]);
            Assert.Equal("Bearer explicit-key", options.Headers["Authorization"]);
            Assert.Equal("explicit-key", options.Env!["TAU_SCOPED_MODELS_API_KEY"]);
            Assert.Equal("explicit-provider", options.Env["TAU_SCOPED_PROVIDER_HEADER"]);
            Assert.Equal("configured-model", options.Env["TAU_SCOPED_MODEL_HEADER"]);
            Assert.Equal("model-option", options.Env["TAU_SCOPED_OPTION_HEADER"]);
            Assert.Equal("configured-only", options.Env["TAU_SCOPED_ONLY_CONFIGURED"]);
            Assert.Equal("explicit-only", options.Env["TAU_SCOPED_EXPLICIT_ONLY"]);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
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
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_ReturnsAuthErrorStreamWhenStoredOAuthRefreshFails()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-stream-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, """
                {
                  "anthropic": {
                    "type": "oauth",
                    "refresh": "refresh-token",
                    "access": "expired-access",
                    "expiresAt": "2000-01-01T00:00:00Z"
                  }
                }
                """);
            scope.Set("ANTHROPIC_API_KEY", "env-key");

            var provider = new CapturingProvider("anthropic-messages");
            var registry = new ProviderRegistry();
            registry.Register("anthropic-messages", () => provider, sourceId: "test");
            var resolver = new ProviderAuthResolver(
                new OAuthProviderRegistry([new FailingOAuthProvider("anthropic")]),
                new OAuthCredentialStore([authPath]));
            var model = new Model
            {
                Id = "claude-test",
                Name = "Claude Test",
                Api = "anthropic-messages",
                Provider = "anthropic"
            };

            var stream = StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions(),
                authResolver: resolver);
            var events = await OpenAiResponsesProviderTests.CollectAsync(stream);

            var error = Assert.Single(events.OfType<ErrorEvent>());
            Assert.Contains("OAuth refresh failed for anthropic", error.Error, StringComparison.Ordinal);
            Assert.Equal(StopReason.Error, error.Message?.StopReason);
            Assert.Equal("anthropic", error.Message?.Provider);
            Assert.Equal("claude-test", error.Message?.Model);
            Assert.Null(provider.CapturedOptions);
            Assert.Contains("expired-access", File.ReadAllText(authPath), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesModelsJsonRequestOptionsWithModelPrecedence()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-options-{Guid.NewGuid():N}");
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
                      "options": {
                        "temperature": 0.2,
                        "maxTokens": 111,
                        "topP": 0.7,
                        "transport": "websocket",
                        "cacheRetention": "long",
                        "sessionId": "provider-session",
                        "timeoutMs": 900,
                        "maxRetryDelayMs": 2500,
                        "maxRetries": 2,
                        "websocketConnectTimeoutMs": 1500,
                        "headers": {
                          "X-Provider-Option": "provider-option",
                          "X-Option-Shared": "provider-option"
                        },
                        "metadata": {
                          "scope": "provider",
                          "shared": "provider"
                        }
                      },
                      "modelOverrides": {
                        "custom-model": {
                          "options": {
                            "temperature": 0.3,
                            "headers": {
                              "X-Override-Option": "override-option",
                              "X-Option-Shared": "override-option"
                            },
                            "metadata": {
                              "shared": "override",
                              "overrideOnly": true
                            }
                          }
                        }
                      },
                      "models": [
                        {
                          "id": "custom-model",
                          "options": {
                            "topP": 0.6,
                            "sessionId": "model-session",
                            "headers": {
                              "X-Model-Option": "model-option",
                              "X-Option-Shared": "model-option"
                            },
                            "metadata": {
                              "shared": "model",
                              "modelOnly": 7
                            }
                          }
                        }
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

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new StreamOptions
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-Explicit-Option"] = "explicit-option",
                        ["X-Option-Shared"] = "explicit-option"
                    }
                }));

            var options = provider.CapturedOptions!;
            Assert.Equal(0.3f, options.Temperature);
            Assert.Equal(111, options.MaxTokens);
            Assert.Equal(0.6f, options.TopP);
            Assert.Equal(StreamTransport.WebSocket, options.Transport);
            Assert.Equal(CacheRetention.Long, options.CacheRetention);
            Assert.Equal("model-session", options.SessionId);
            Assert.Equal(TimeSpan.FromMilliseconds(900), options.Timeout);
            Assert.Equal(TimeSpan.FromMilliseconds(2500), options.MaxRetryDelay);
            Assert.Equal(2, options.MaxRetries);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), options.WebSocketConnectTimeout);
            Assert.Equal("provider-option", options.Headers!["X-Provider-Option"]);
            Assert.Equal("override-option", options.Headers["X-Override-Option"]);
            Assert.Equal("model-option", options.Headers["X-Model-Option"]);
            Assert.Equal("explicit-option", options.Headers["X-Explicit-Option"]);
            Assert.Equal("explicit-option", options.Headers["X-Option-Shared"]);
            Assert.Equal("provider", Assert.IsType<JsonElement>(options.Metadata!["scope"]).GetString());
            Assert.Equal("model", Assert.IsType<JsonElement>(options.Metadata["shared"]).GetString());
            Assert.True(Assert.IsType<JsonElement>(options.Metadata["overrideOnly"]).GetBoolean());
            Assert.Equal(7, Assert.IsType<JsonElement>(options.Metadata["modelOnly"]).GetInt32());
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesExplicitSimpleOptionsOverModelsJsonRequestOptions()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-options-{Guid.NewGuid():N}");
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
                      "options": {
                        "temperature": 0.2,
                        "maxTokens": 111,
                        "topP": 0.7,
                        "transport": "websocket",
                        "cacheRetention": "long",
                        "sessionId": "provider-session",
                        "timeoutMs": 900,
                        "maxRetryDelayMs": 2500,
                        "maxRetries": 2,
                        "websocketConnectTimeoutMs": 1500,
                        "reasoning": "xhigh",
                        "thinkingBudgets": {
                          "minimal": 100,
                          "low": 200,
                          "medium": 300,
                          "high": 400
                        },
                        "metadata": {
                          "shared": "configured",
                          "configuredOnly": "present"
                        },
                        "signal": "ignored",
                        "onPayload": "ignored",
                        "onResponse": "ignored"
                      },
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
                Provider = "custom-provider",
                Reasoning = true
            };
            using var cts = new CancellationTokenSource();
            Func<object, Model, ValueTask<object?>> onPayload = (payload, _) => ValueTask.FromResult<object?>(payload);
            Func<ProviderResponse, Model, ValueTask> onResponse = (_, _) => ValueTask.CompletedTask;

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions
                {
                    Temperature = 0.9f,
                    MaxTokens = 222,
                    TopP = 0.1f,
                    Transport = StreamTransport.Sse,
                    CacheRetention = CacheRetention.None,
                    SessionId = "explicit-session",
                    Timeout = TimeSpan.FromMilliseconds(42),
                    MaxRetryDelay = TimeSpan.Zero,
                    MaxRetries = 4,
                    WebSocketConnectTimeout = TimeSpan.FromMilliseconds(42),
                    Metadata = new Dictionary<string, object>
                    {
                        ["shared"] = "explicit"
                    },
                    Reasoning = ThinkingLevel.Low,
                    ThinkingBudgets = new ThinkingBudgets { Low = 777 },
                    Signal = cts.Token,
                    OnPayload = onPayload,
                    OnResponse = onResponse
                }));

            var options = provider.CapturedSimpleOptions!;
            Assert.Equal(0.9f, options.Temperature);
            Assert.Equal(222, options.MaxTokens);
            Assert.Equal(0.1f, options.TopP);
            Assert.Equal(StreamTransport.Sse, options.Transport);
            Assert.Equal(CacheRetention.None, options.CacheRetention);
            Assert.Equal("explicit-session", options.SessionId);
            Assert.Equal(TimeSpan.FromMilliseconds(42), options.Timeout);
            Assert.Equal(TimeSpan.Zero, options.MaxRetryDelay);
            Assert.Equal(4, options.MaxRetries);
            Assert.Equal(TimeSpan.FromMilliseconds(42), options.WebSocketConnectTimeout);
            Assert.Equal("explicit", options.Metadata!["shared"]);
            Assert.Equal("present", Assert.IsType<JsonElement>(options.Metadata["configuredOnly"]).GetString());
            Assert.Equal(ThinkingLevel.Low, options.Reasoning);
            Assert.Equal(100, options.ThinkingBudgets!.Minimal);
            Assert.Equal(777, options.ThinkingBudgets.Low);
            Assert.Equal(300, options.ThinkingBudgets.Medium);
            Assert.Equal(400, options.ThinkingBudgets.High);
            Assert.Equal(cts.Token, options.Signal);
            Assert.Same(onPayload, options.OnPayload);
            Assert.Same(onResponse, options.OnResponse);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesOpenAiProviderSpecificOptionsFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-openai-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openai": {
                      "apiKey": "openai-key",
                      "models": [
                        {
                          "id": "gpt-5.4",
                          "options": {
                            "maxTokens": 512,
                            "toolChoice": {
                              "type": "function",
                              "function": {
                                "name": "read_file"
                              }
                            },
                            "reasoningEffort": "low"
                          }
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new OpenAiOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("openai-chat-completions", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "gpt-5.4",
                    Name = "GPT-5.4",
                    Api = "openai-chat-completions",
                    Provider = "openai",
                    Reasoning = true,
                    MaxOutputTokens = 1024
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("openai-key", options.ApiKey);
            Assert.Equal(512, options.MaxTokens);
            Assert.True(options.ToolChoice?.IsFunction);
            Assert.Equal("function", options.ToolChoice?.Kind);
            Assert.Equal("read_file", options.ToolChoice?.FunctionName);
            Assert.Equal("low", options.ReasoningEffort);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesExplicitOpenAiOptionsOverModelsJsonProviderSpecificOptions()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-openai-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openai": {
                      "apiKey": "openai-key",
                      "models": [
                        {
                          "id": "gpt-5.4",
                          "options": {
                            "toolChoice": "required",
                            "reasoningEffort": "low"
                          }
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new OpenAiOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("openai-chat-completions", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                new Model
                {
                    Id = "gpt-5.4",
                    Name = "GPT-5.4",
                    Api = "openai-chat-completions",
                    Provider = "openai",
                    Reasoning = true
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new OpenAiOptions
                {
                    ToolChoice = OpenAiToolChoice.Function("explicit_tool"),
                    ReasoningEffort = "high"
                },
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("openai-key", options.ApiKey);
            Assert.True(options.ToolChoice?.IsFunction);
            Assert.Equal("explicit_tool", options.ToolChoice?.FunctionName);
            Assert.Equal("high", options.ReasoningEffort);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesResponsesProviderSpecificOptionsFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-provider-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openai": {
                      "apiKey": "models-key",
                      "options": {
                        "serviceTier": "flex",
                        "reasoningEffort": "low",
                        "reasoningSummary": "concise"
                      },
                      "models": [
                        { "id": "gpt-5.4" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

                """));
            using var client = new HttpClient(handler);
            var registry = new ProviderRegistry();
            registry.Register("openai-responses", () => new OpenAiResponsesProvider(client), sourceId: "test");
            var model = new Model
            {
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-responses",
                Provider = "openai",
                BaseUrl = "https://example.invalid/v1",
                Reasoning = true
            };
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            _ = await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            using var body = JsonDocument.Parse(handler.CapturedBody);
            var root = body.RootElement;
            Assert.Equal("flex", root.GetProperty("service_tier").GetString());
            var reasoning = root.GetProperty("reasoning");
            Assert.Equal("low", reasoning.GetProperty("effort").GetString());
            Assert.Equal("concise", reasoning.GetProperty("summary").GetString());
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesExplicitResponsesOptionsOverModelsJsonProviderSpecificOptions()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-provider-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openai": {
                      "apiKey": "models-key",
                      "options": {
                        "serviceTier": "flex",
                        "reasoningEffort": "low",
                        "reasoningSummary": "concise"
                      },
                      "models": [
                        { "id": "gpt-5.4" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":1,"output_tokens":1}}}

                """));
            using var client = new HttpClient(handler);
            var registry = new ProviderRegistry();
            registry.Register("openai-responses", () => new OpenAiResponsesProvider(client), sourceId: "test");
            var model = new Model
            {
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-responses",
                Provider = "openai",
                BaseUrl = "https://example.invalid/v1",
                Reasoning = true
            };
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            _ = await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                new LlmContext { Messages = [new UserMessage("hi")] },
                new OpenAiResponsesOptions
                {
                    ServiceTier = "priority",
                    ReasoningEffort = "high",
                    ReasoningSummary = "detailed"
                },
                configurationStore,
                authResolver));

            using var body = JsonDocument.Parse(handler.CapturedBody);
            var root = body.RootElement;
            Assert.Equal("priority", root.GetProperty("service_tier").GetString());
            var reasoning = root.GetProperty("reasoning");
            Assert.Equal("high", reasoning.GetProperty("effort").GetString());
            Assert.Equal("detailed", reasoning.GetProperty("summary").GetString());
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesCodexAndAzureProviderSpecificOptionsFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-provider-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openai-codex": {
                      "apiKey": "codex-key",
                      "options": {
                        "serviceTier": "priority",
                        "reasoningEffort": "minimal",
                        "reasoningSummary": "detailed",
                        "textVerbosity": "high"
                      },
                      "models": [
                        { "id": "gpt-5.2-codex" }
                      ]
                    },
                    "azure-openai-responses": {
                      "apiKey": "azure-key",
                      "options": {
                        "reasoningEffort": "low",
                        "reasoningSummary": "concise",
                        "azureApiVersion": "2026-01-01-preview",
                        "azureResourceName": "configured-resource",
                        "azureBaseUrl": "https://configured.openai.azure.com/openai/v1",
                        "azureDeploymentName": "configured-deployment"
                      },
                      "models": [
                        { "id": "gpt-4o-mini" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var codexProvider = new CodexOptionsCapturingProvider();
            var azureProvider = new AzureOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("openai-codex-responses", () => codexProvider, sourceId: "test");
            registry.Register("azure-openai-responses", () => azureProvider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "gpt-5.2-codex",
                    Name = "GPT-5.2 Codex",
                    Api = "openai-codex-responses",
                    Provider = "openai-codex",
                    Reasoning = true,
                    MaxOutputTokens = 456
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));
            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "gpt-4o-mini",
                    Name = "GPT-4o mini",
                    Api = "azure-openai-responses",
                    Provider = "azure-openai-responses",
                    Reasoning = true,
                    MaxOutputTokens = 789
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            var codexOptions = codexProvider.CapturedOptions!;
            Assert.Equal("codex-key", codexOptions.ApiKey);
            Assert.Equal(456, codexOptions.MaxTokens);
            Assert.Equal("priority", codexOptions.ServiceTier);
            Assert.Equal("minimal", codexOptions.ReasoningEffort);
            Assert.Equal("detailed", codexOptions.ReasoningSummary);
            Assert.Equal("high", codexOptions.TextVerbosity);

            var azureOptions = azureProvider.CapturedOptions!;
            Assert.Equal("azure-key", azureOptions.ApiKey);
            Assert.Equal(789, azureOptions.MaxTokens);
            Assert.Equal("low", azureOptions.ReasoningEffort);
            Assert.Equal("concise", azureOptions.ReasoningSummary);
            Assert.Equal("2026-01-01-preview", azureOptions.AzureApiVersion);
            Assert.Equal("configured-resource", azureOptions.AzureResourceName);
            Assert.Equal("https://configured.openai.azure.com/openai/v1", azureOptions.AzureBaseUrl);
            Assert.Equal("configured-deployment", azureOptions.AzureDeploymentName);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesMistralProviderSpecificOptionsFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-provider-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "mistral": {
                      "apiKey": "mistral-key",
                      "options": {
                        "sessionId": "mistral-session",
                        "toolChoice": "required",
                        "promptMode": "reasoning",
                        "reasoningEffort": "high"
                      },
                      "models": [
                        { "id": "mistral-small-latest" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new MistralOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("mistral-conversations", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "mistral-small-latest",
                    Name = "Mistral Small",
                    Api = "mistral-conversations",
                    Provider = "mistral",
                    Reasoning = true,
                    MaxOutputTokens = 321
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("mistral-key", options.ApiKey);
            Assert.Equal(321, options.MaxTokens);
            Assert.Equal("mistral-session", options.SessionId);
            Assert.Equal("required", options.ToolChoice?.Kind);
            Assert.Equal("reasoning", options.PromptMode);
            Assert.Equal("high", options.ReasoningEffort);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesAnthropicProviderSpecificOptionsFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-anthropic-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "anthropic": {
                      "apiKey": "anthropic-key",
                      "options": {
                        "maxTokens": 777,
                        "thinkingEnabled": true,
                        "thinkingBudgetTokens": 2345,
                        "effort": "high",
                        "thinkingDisplay": "omitted",
                        "interleavedThinking": true,
                        "toolChoice": {
                          "type": "tool",
                          "name": "read_file"
                        }
                      },
                      "models": [
                        { "id": "claude-sonnet-4-5-20250929" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new AnthropicOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("anthropic-messages", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "claude-sonnet-4-5-20250929",
                    Name = "Claude Sonnet",
                    Api = "anthropic-messages",
                    Provider = "anthropic",
                    Reasoning = true,
                    MaxOutputTokens = 4096
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("anthropic-key", options.ApiKey);
            Assert.Equal(777, options.MaxTokens);
            Assert.True(options.ThinkingEnabled);
            Assert.Equal(2345, options.ThinkingBudgetTokens);
            Assert.Equal("high", options.Effort);
            Assert.Equal("omitted", options.ThinkingDisplay);
            Assert.True(options.InterleavedThinking);
            Assert.True(options.ToolChoice?.IsTool);
            Assert.Equal("tool", options.ToolChoice?.Kind);
            Assert.Equal("read_file", options.ToolChoice?.Name);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesExplicitSimpleReasoningWhenApplyingAnthropicProviderSpecificOptions()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-anthropic-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "anthropic": {
                      "apiKey": "anthropic-key",
                      "options": {
                        "thinkingEnabled": false,
                        "effort": "low",
                        "thinkingDisplay": "omitted"
                      },
                      "models": [
                        { "id": "claude-opus-4-7-20260101" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new AnthropicOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("anthropic-messages", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "claude-opus-4-7-20260101",
                    Name = "Claude Opus",
                    Api = "anthropic-messages",
                    Provider = "anthropic",
                    Reasoning = true,
                    MaxOutputTokens = 4096
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions { Reasoning = ThinkingLevel.ExtraHigh },
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.True(options.ThinkingEnabled);
            Assert.Null(options.ThinkingBudgetTokens);
            Assert.Equal("xhigh", options.Effort);
            Assert.Equal("omitted", options.ThinkingDisplay);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesExplicitAnthropicOptionsOverModelsJsonProviderSpecificOptions()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-anthropic-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "anthropic": {
                      "apiKey": "anthropic-key",
                      "options": {
                        "thinkingEnabled": true,
                        "thinkingBudgetTokens": 2345,
                        "effort": "high",
                        "thinkingDisplay": "omitted",
                        "interleavedThinking": true,
                        "toolChoice": {
                          "type": "tool",
                          "name": "read_file"
                        }
                      },
                      "models": [
                        { "id": "claude-sonnet-4-5-20250929" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new AnthropicOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("anthropic-messages", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                new Model
                {
                    Id = "claude-sonnet-4-5-20250929",
                    Name = "Claude Sonnet",
                    Api = "anthropic-messages",
                    Provider = "anthropic",
                    Reasoning = true
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new AnthropicOptions
                {
                    ThinkingEnabled = false,
                    ThinkingBudgetTokens = 9999,
                    Effort = "low",
                    ThinkingDisplay = "summarized",
                    InterleavedThinking = false,
                    ToolChoice = AnthropicToolChoice.Tool("explicit_tool")
                },
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("anthropic-key", options.ApiKey);
            Assert.False(options.ThinkingEnabled);
            Assert.Equal(9999, options.ThinkingBudgetTokens);
            Assert.Equal("low", options.Effort);
            Assert.Equal("summarized", options.ThinkingDisplay);
            Assert.False(options.InterleavedThinking);
            Assert.True(options.ToolChoice?.IsTool);
            Assert.Equal("explicit_tool", options.ToolChoice?.Name);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesMistralFunctionToolChoiceFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-mistral-tool-choice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "mistral": {
                      "apiKey": "mistral-key",
                      "options": {
                        "toolChoice": {
                          "type": "function",
                          "function": { "name": "read_file" }
                        },
                        "promptMode": "reasoning",
                        "reasoningEffort": "high"
                      },
                      "models": [
                        { "id": "mistral-small-latest" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new MistralOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("mistral-conversations", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "mistral-small-latest",
                    Name = "Mistral Small",
                    Api = "mistral-conversations",
                    Provider = "mistral",
                    Reasoning = true,
                    MaxOutputTokens = 321
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("mistral-key", options.ApiKey);
            Assert.Equal(321, options.MaxTokens);
            Assert.True(options.ToolChoice?.IsFunction);
            Assert.Equal("function", options.ToolChoice?.Kind);
            Assert.Equal("read_file", options.ToolChoice?.FunctionName);
            Assert.Equal("reasoning", options.PromptMode);
            Assert.Equal("high", options.ReasoningEffort);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesExplicitMistralOptionsOverModelsJsonProviderSpecificOptions()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-provider-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "mistral": {
                      "apiKey": "mistral-key",
                      "options": {
                        "toolChoice": "required",
                        "promptMode": "reasoning",
                        "reasoningEffort": "high"
                      },
                      "models": [
                        { "id": "mistral-small-latest" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new MistralOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("mistral-conversations", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                new Model
                {
                    Id = "mistral-small-latest",
                    Name = "Mistral Small",
                    Api = "mistral-conversations",
                    Provider = "mistral",
                    Reasoning = true
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new MistralOptions
                {
                    ToolChoice = MistralToolChoice.Function("explicit_tool"),
                    PromptMode = "explicit-mode",
                    ReasoningEffort = "none"
                },
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("mistral-key", options.ApiKey);
            Assert.True(options.ToolChoice?.IsFunction);
            Assert.Equal("explicit_tool", options.ToolChoice?.FunctionName);
            Assert.Equal("explicit-mode", options.PromptMode);
            Assert.Equal("none", options.ReasoningEffort);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesGoogleProviderSpecificOptionsFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-google-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "google": {
                      "apiKey": "google-key",
                      "options": {
                        "maxTokens": 111,
                        "toolChoice": "any",
                        "thinkingEnabled": true,
                        "thinkingBudgetTokens": 4567
                      },
                      "models": [
                        { "id": "gemini-2.5-flash" }
                      ]
                    },
                    "google-vertex": {
                      "apiKey": "vertex-key",
                      "options": {
                        "maxTokens": 222,
                        "toolChoice": "auto",
                        "thinkingEnabled": false,
                        "project": "configured-project",
                        "location": "europe-west4"
                      },
                      "models": [
                        { "id": "gemini-3-pro-preview" }
                      ]
                    },
                    "google-gemini-cli": {
                      "apiKey": "{\"token\":\"token\",\"projectId\":\"credential-project\"}",
                      "options": {
                        "maxTokens": 333,
                        "toolChoice": "none",
                        "thinkingLevel": "HIGH",
                        "projectId": "configured-cli-project"
                      },
                      "models": [
                        { "id": "gemini-3.1-pro-high" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var googleProvider = new GoogleOptionsCapturingProvider();
            var vertexProvider = new GoogleVertexOptionsCapturingProvider();
            var cliProvider = new GoogleGeminiCliOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("google-generative-language", () => googleProvider, sourceId: "test");
            registry.Register("google-vertex", () => vertexProvider, sourceId: "test");
            registry.Register("google-gemini-cli", () => cliProvider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "gemini-2.5-flash",
                    Name = "Gemini Flash",
                    Api = "google-generative-language",
                    Provider = "google",
                    Reasoning = true,
                    MaxOutputTokens = 999
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "gemini-3-pro-preview",
                    Name = "Gemini Vertex",
                    Api = "google-vertex",
                    Provider = "google-vertex",
                    Reasoning = true,
                    MaxOutputTokens = 999
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "gemini-3.1-pro-high",
                    Name = "Gemini CLI",
                    Api = "google-gemini-cli",
                    Provider = "google-gemini-cli",
                    Reasoning = true,
                    MaxOutputTokens = 999
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            var googleOptions = googleProvider.CapturedOptions!;
            Assert.Equal("google-key", googleOptions.ApiKey);
            Assert.Equal(111, googleOptions.MaxTokens);
            Assert.Equal("any", googleOptions.ToolChoice);
            Assert.True(googleOptions.Thinking?.Enabled);
            Assert.Equal(4567, googleOptions.Thinking?.BudgetTokens);

            var vertexOptions = vertexProvider.CapturedOptions!;
            Assert.Equal("vertex-key", vertexOptions.ApiKey);
            Assert.Equal(222, vertexOptions.MaxTokens);
            Assert.Equal("auto", vertexOptions.ToolChoice);
            Assert.False(vertexOptions.Thinking?.Enabled);
            Assert.Equal("configured-project", vertexOptions.Project);
            Assert.Equal("europe-west4", vertexOptions.Location);

            var cliOptions = cliProvider.CapturedOptions!;
            Assert.Equal("""{"token":"token","projectId":"credential-project"}""", cliOptions.ApiKey);
            Assert.Equal(333, cliOptions.MaxTokens);
            Assert.Equal("none", cliOptions.ToolChoice);
            Assert.True(cliOptions.Thinking?.Enabled);
            Assert.Equal("HIGH", cliOptions.Thinking?.Level);
            Assert.Equal("configured-cli-project", cliOptions.ProjectId);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_AppliesBedrockProviderSpecificOptionsFromModelsJson()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-bedrock-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "amazon-bedrock": {
                      "apiKey": "bedrock-api-key",
                      "options": {
                        "maxTokens": 888,
                        "region": "us-west-2",
                        "profile": "foundation-profile",
                        "bearerToken": "configured-bedrock-token",
                        "toolChoice": {
                          "type": "tool",
                          "name": "read_file"
                        },
                        "reasoning": "high",
                        "thinkingBudgetTokens": 3456,
                        "thinkingDisplay": "omitted",
                        "interleavedThinking": true,
                        "requestMetadata": {
                          "app": "tau",
                          "costCenter": "foundation"
                        }
                      },
                      "models": [
                        { "id": "anthropic.claude-3-7-sonnet-20250219-v1:0" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new BedrockOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("bedrock-converse-stream", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.StreamSimple(
                registry,
                new Model
                {
                    Id = "anthropic.claude-3-7-sonnet-20250219-v1:0",
                    Name = "Bedrock Claude",
                    Api = "bedrock-converse-stream",
                    Provider = "amazon-bedrock",
                    Reasoning = true,
                    MaxOutputTokens = 4096
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new SimpleStreamOptions(),
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("bedrock-api-key", options.ApiKey);
            Assert.Equal(888, options.MaxTokens);
            Assert.Equal("us-west-2", options.Region);
            Assert.Equal("foundation-profile", options.Profile);
            Assert.Equal("configured-bedrock-token", options.BearerToken);
            Assert.Equal("tool", options.ToolChoice);
            Assert.Equal("read_file", options.ToolName);
            Assert.Equal(ThinkingLevel.High, options.Reasoning);
            Assert.Equal(3456, options.ThinkingBudgetTokens);
            Assert.Equal("omitted", options.ThinkingDisplay);
            Assert.True(options.InterleavedThinking);
            Assert.Equal("tau", options.RequestMetadata?["app"]);
            Assert.Equal("foundation", options.RequestMetadata?["costCenter"]);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task StreamFunctions_PreservesExplicitBedrockOptionsOverModelsJsonProviderSpecificOptions()
    {
        using var scope = EnvironmentVariableScope.Acquire();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-bedrock-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "amazon-bedrock": {
                      "apiKey": "bedrock-api-key",
                      "options": {
                        "region": "us-west-2",
                        "profile": "configured-profile",
                        "bearerToken": "configured-token",
                        "toolChoice": "auto",
                        "reasoning": "high",
                        "thinkingBudgetTokens": 3456,
                        "thinkingDisplay": "omitted",
                        "interleavedThinking": true,
                        "requestMetadata": {
                          "app": "configured"
                        }
                      },
                      "models": [
                        { "id": "anthropic.claude-3-7-sonnet-20250219-v1:0" }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            var provider = new BedrockOptionsCapturingProvider();
            var registry = new ProviderRegistry();
            registry.Register("bedrock-converse-stream", () => provider, sourceId: "test");
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                new Model
                {
                    Id = "anthropic.claude-3-7-sonnet-20250219-v1:0",
                    Name = "Bedrock Claude",
                    Api = "bedrock-converse-stream",
                    Provider = "amazon-bedrock",
                    Reasoning = true
                },
                new LlmContext { Messages = [new UserMessage("hi")] },
                new BedrockOptions
                {
                    Region = "eu-central-1",
                    Profile = "explicit-profile",
                    BearerToken = "explicit-token",
                    ToolChoice = "tool",
                    ToolName = "explicit_tool",
                    Reasoning = ThinkingLevel.Low,
                    ThinkingBudgetTokens = 9999,
                    ThinkingDisplay = "summarized",
                    InterleavedThinking = false,
                    RequestMetadata = new Dictionary<string, string> { ["app"] = "explicit" }
                },
                configurationStore,
                authResolver));

            var options = provider.CapturedOptions!;
            Assert.Equal("bedrock-api-key", options.ApiKey);
            Assert.Equal("eu-central-1", options.Region);
            Assert.Equal("explicit-profile", options.Profile);
            Assert.Equal("explicit-token", options.BearerToken);
            Assert.Equal("tool", options.ToolChoice);
            Assert.Equal("explicit_tool", options.ToolName);
            Assert.Equal(ThinkingLevel.Low, options.Reasoning);
            Assert.Equal(9999, options.ThinkingBudgetTokens);
            Assert.Equal("summarized", options.ThinkingDisplay);
            Assert.False(options.InterleavedThinking);
            Assert.Equal("explicit", options.RequestMetadata?["app"]);
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Theory]
    [InlineData("openai-chat-completions", "openai-completions")]
    [InlineData("google-generative-language", "google-generative-ai")]
    public async Task StreamFunctions_ResolvesUpstreamApiAliasToCanonicalProvider(string registeredApi, string aliasApi)
    {
        var provider = new CapturingProvider(registeredApi);
        var registry = new ProviderRegistry();
        registry.Register(registeredApi, () => provider, sourceId: "test");

        var model = new Model
        {
            Id = "alias-model",
            Name = "Alias Model",
            Api = aliasApi,
            Provider = "alias-provider"
        };

        var events = await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
            registry,
            model,
            new LlmContext { Messages = [new UserMessage("hi")] },
            new StreamOptions()));

        Assert.Contains(events, evt => evt is DoneEvent done && ReadText(done.Message.Content) == "ok");
        Assert.NotNull(provider.CapturedOptions);
        Assert.Contains(registeredApi, registry.RegisteredApis);
        Assert.DoesNotContain(aliasApi, registry.RegisteredApis);
    }

    [Fact]
    public async Task RegisterConfiguredProviders_AppliesCompatMessageSemanticsForDynamicOpenAiCompatibleProvider()
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
                    "custom-proxy": {
                      "baseUrl": "https://proxy.example.test/v1",
                      "api": "custom-openai-api",
                      "apiKind": "openai-compatible",
                      "apiKey": "dynamic-key",
                      "compat": {
                        "requiresToolResultName": true,
                        "requiresAssistantAfterToolResult": true
                      },
                      "models": [
                        {
                          "id": "proxy-model"
                        }
                      ]
                    }
                  }
                }
                """);
            scope.Set("TAU_MODELS_FILE", modelsPath);
            scope.Set("TAU_AUTH_FILE", authPath);

            using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => OpenAiResponsesProviderTests.SseResponse(
                """
                data: {"choices":[{"delta":{"content":"ok"},"finish_reason":null}]}

                data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """));
            using var client = new HttpClient(handler);
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var catalog = new ModelCatalog(configurationStore: configurationStore);
            var model = catalog.GetModel("custom-proxy", "proxy-model");
            var registry = new ProviderRegistry();

            BuiltInProviders.RegisterAll(registry, configurationStore, client);

            var context = new LlmContext(
                null,
                [
                    new AssistantMessage([new ToolCallContent("call_1", "read_file", "{}")]),
                    new ToolResultMessage("call_1", [new TextContent("done")]) { ToolName = "read_file" },
                    new UserMessage("follow up")
                ],
                null);

            await OpenAiResponsesProviderTests.CollectAsync(StreamFunctions.Stream(
                registry,
                model,
                context,
                new StreamOptions()));

            using var body = JsonDocument.Parse(handler.CapturedBody);
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal(4, messages.GetArrayLength());
            Assert.Equal("tool", messages[1].GetProperty("role").GetString());
            Assert.Equal("read_file", messages[1].GetProperty("name").GetString());
            Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
            Assert.Equal("I have processed the tool results.", messages[2].GetProperty("content").GetString());
            Assert.Equal("user", messages[3].GetProperty("role").GetString());
            Assert.Equal("follow up", messages[3].GetProperty("content").GetString());
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private sealed class CapturingProvider : IStreamProvider
    {
        public CapturingProvider(string api = "test-api")
        {
            Api = api;
        }

        public string Api { get; }
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

    private sealed class CodexOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "openai-codex-responses";
        public OpenAiCodexResponsesOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<OpenAiCodexResponsesOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed Codex options.");
    }

    private sealed class OpenAiOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "openai-chat-completions";
        public OpenAiOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<OpenAiOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed OpenAI options.");
    }

    private sealed class AzureOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "azure-openai-responses";
        public AzureOpenAiResponsesOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<AzureOpenAiResponsesOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed Azure options.");
    }

    private sealed class AnthropicOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "anthropic-messages";
        public AnthropicOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<AnthropicOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed Anthropic options.");
    }

    private sealed class MistralOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "mistral-conversations";
        public MistralOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<MistralOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed Mistral options.");
    }

    private sealed class GoogleOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "google-generative-language";
        public GoogleOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<GoogleOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed Google options.");
    }

    private sealed class GoogleVertexOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "google-vertex";
        public GoogleVertexOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<GoogleVertexOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed Google Vertex options.");
    }

    private sealed class GoogleGeminiCliOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "google-gemini-cli";
        public GoogleGeminiCliOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<GoogleGeminiCliOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed Google Gemini CLI options.");
    }

    private sealed class BedrockOptionsCapturingProvider : IStreamProvider
    {
        public string Api => "bedrock-converse-stream";
        public BedrockOptions? CapturedOptions { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            CapturedOptions = Assert.IsType<BedrockOptions>(options);
            return Complete(model);
        }

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed Bedrock options.");
    }

    private static AssistantMessageStream Complete(Model model)
    {
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage { Content = [new TextContent("ok")] }
        with
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            StopReason = StopReason.EndTurn
        }));
        return stream;
    }

    private static string ReadText(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

    private sealed class FailingOAuthProvider(string id) : IOAuthProvider
    {
        public string Id { get; } = id;
        public string Name => "Failing OAuth";

        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("refresh failed");

        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }
}
