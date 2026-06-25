using System.Text.Json;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public void GetProviders_IncludesPortedProviderFamilies()
    {
        var catalog = CreateBuiltInCatalog();

        var providers = catalog.GetProviders();

        Assert.Contains("openai", providers);
        Assert.Contains("azure-openai-responses", providers);
        Assert.Contains("openai-codex", providers);
        Assert.Contains("mistral", providers);
        Assert.Contains("google-vertex", providers);
        Assert.Contains("google-gemini-cli", providers);
        Assert.Contains("amazon-bedrock", providers);
        Assert.Contains("deepseek", providers);
        Assert.Contains("groq", providers);
        Assert.Contains("cerebras", providers);
        Assert.Contains("openrouter", providers);
        Assert.Contains("together", providers);
        Assert.Contains("xai", providers);
        Assert.Contains("zai", providers);
        Assert.Contains("xiaomi", providers);
        Assert.Contains("moonshotai", providers);
        Assert.Contains("nvidia", providers);
        Assert.Contains("huggingface", providers);
        Assert.Contains("cloudflare-workers-ai", providers);
        Assert.Contains("cloudflare-ai-gateway", providers);
        Assert.Contains("fireworks", providers);
        Assert.Contains("opencode", providers);
        Assert.Contains("ant-ling", providers);
    }

    [Fact]
    public void GetModel_LoadsGeneratedCatalogEntries()
    {
        var catalog = CreateBuiltInCatalog();

        var azure = catalog.GetModel("azure-openai-responses", "gpt-5.4-pro");
        var codex = catalog.GetModel("openai-codex", "gpt-5.1-codex-max");
        var copilot = catalog.GetModel("github-copilot", "gpt-5.4-mini");
        var geminiCli = catalog.GetModel("google-gemini-cli", "gemini-3.1-pro-preview");
        var antigravity = catalog.GetModel("google-antigravity", "claude-opus-4-6-thinking");
        var deepseek = catalog.GetModel("deepseek", "deepseek-v4-pro");
        var groq = catalog.GetModel("groq", "meta-llama/llama-4-scout-17b-16e-instruct");
        var cerebras = catalog.GetModel("cerebras", "gpt-oss-120b");
        var openrouter = catalog.GetModel("openrouter", "anthropic/claude-sonnet-4.6");
        var together = catalog.GetModel("together", "moonshotai/Kimi-K2.6");
        var xai = catalog.GetModel("xai", "grok-4.3");
        var zai = catalog.GetModel("zai", "glm-4.7");
        var opencode = catalog.GetModel("opencode", "claude-sonnet-4-6");
        var fireworks = catalog.GetModel("fireworks", "accounts/fireworks/models/kimi-k2p6");
        var cloudflareAiGateway = catalog.GetModel("cloudflare-ai-gateway", "claude-sonnet-4-6");
        var fable = catalog.GetModel("cloudflare-ai-gateway", "claude-fable-5");

        Assert.Equal("azure-openai-responses", azure.Api);
        Assert.Equal(128_000, azure.MaxOutputTokens);
        Assert.Equal(1_050_000, azure.ContextWindow);
        Assert.Equal("openai-codex-responses", codex.Api);
        Assert.Equal(272_000, codex.ContextWindow);
        Assert.Equal("openai-responses", copilot.Api);
        Assert.Contains("image", copilot.InputModalities);
        Assert.NotNull(copilot.Headers);
        Assert.Equal("google-gemini-cli", geminiCli.Api);
        Assert.Equal(65_535, geminiCli.MaxOutputTokens);
        Assert.Equal("google-gemini-cli", antigravity.Api);
        Assert.Equal(128_000, antigravity.MaxOutputTokens);
        Assert.Equal("openai-chat-completions", deepseek.Api);
        Assert.Equal("https://api.deepseek.com", deepseek.BaseUrl);
        Assert.Equal(1_000_000, deepseek.ContextWindow);
        Assert.Equal("deepseek", deepseek.Compat!.ThinkingFormat);
        Assert.False(deepseek.Compat.SupportsDeveloperRole);
        Assert.Equal("openai-chat-completions", groq.Api);
        Assert.Contains("image", groq.InputModalities);
        Assert.Equal(8_192, groq.MaxOutputTokens);
        Assert.Equal(0.11m, groq.Cost!.Value.InputPerMillion);
        Assert.Equal("openai-chat-completions", cerebras.Api);
        Assert.Equal("https://api.cerebras.ai/v1", cerebras.BaseUrl);
        Assert.False(cerebras.Compat!.SupportsStore);
        Assert.Equal("openai-chat-completions", openrouter.Api);
        Assert.Equal("https://openrouter.ai/api/v1", openrouter.BaseUrl);
        Assert.Equal("openrouter", openrouter.Compat!.ThinkingFormat);
        Assert.Equal("anthropic", openrouter.Compat.CacheControlFormat);
        Assert.Equal("openai-chat-completions", together.Api);
        Assert.Equal("https://api.together.ai/v1", together.BaseUrl);
        Assert.Equal("together", together.Compat!.ThinkingFormat);
        Assert.Equal("openai-chat-completions", xai.Api);
        Assert.Equal("https://api.x.ai/v1", xai.BaseUrl);
        Assert.False(xai.Compat!.SupportsStore);
        Assert.Equal("openai-chat-completions", zai.Api);
        Assert.Equal("https://api.z.ai/api/coding/paas/v4", zai.BaseUrl);
        Assert.True(zai.Compat!.ZaiToolStream);
        Assert.Equal("anthropic-messages", opencode.Api);
        Assert.Equal("https://opencode.ai/zen", opencode.BaseUrl);
        Assert.True(opencode.Compat!.ForceAdaptiveThinking);
        Assert.Equal("anthropic-messages", fireworks.Api);
        Assert.Equal("https://api.fireworks.ai/inference", fireworks.BaseUrl);
        Assert.True(fireworks.Compat!.SendSessionAffinityHeaders);
        Assert.False(fireworks.Compat.SupportsLongCacheRetention);
        Assert.False(fireworks.Compat.SupportsEagerToolInputStreaming);
        Assert.False(fireworks.Compat.SupportsCacheControlOnTools);
        Assert.Equal("anthropic-messages", cloudflareAiGateway.Api);
        Assert.Contains("{CLOUDFLARE_ACCOUNT_ID}", cloudflareAiGateway.BaseUrl);
        Assert.True(cloudflareAiGateway.Compat!.SendSessionAffinityHeaders);
        Assert.True(cloudflareAiGateway.Compat.ForceAdaptiveThinking);
        Assert.True(fable.Compat!.ForceAdaptiveThinking);
        Assert.False(fable.Compat.SupportsDisabledThinking);
    }

    [Fact]
    public void GetModels_MergesHandWrittenAndGeneratedCatalogs()
    {
        var catalog = CreateBuiltInCatalog();

        var azureIds = catalog.GetModels("azure-openai-responses").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codexIds = catalog.GetModels("openai-codex").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copilotIds = catalog.GetModels("github-copilot").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var geminiCliIds = catalog.GetModels("google-gemini-cli").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var antigravityIds = catalog.GetModels("google-antigravity").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deepseekIds = catalog.GetModels("deepseek").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groqIds = catalog.GetModels("groq").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cerebrasIds = catalog.GetModels("cerebras").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openrouterIds = catalog.GetModels("openrouter").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var xaiIds = catalog.GetModels("xai").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var xiaomiIds = catalog.GetModels("xiaomi").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var opencodeIds = catalog.GetModels("opencode").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fireworksIds = catalog.GetModels("fireworks").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cloudflareAiGatewayIds = catalog.GetModels("cloudflare-ai-gateway").Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("gpt-4.1", azureIds);
        Assert.Contains("gpt-4-turbo", azureIds);
        Assert.Contains("gpt-5.4-pro", azureIds);
        Assert.Contains("o4-mini-deep-research", azureIds);
        Assert.Contains("gpt-5.2-codex", codexIds);
        Assert.Contains("gpt-5.1-codex-max", codexIds);
        Assert.Contains("gpt-5.4-mini", codexIds);
        Assert.Contains("gpt-4o", copilotIds);
        Assert.Contains("gpt-5.4-mini", copilotIds);
        Assert.Contains("gemini-2.5-pro", geminiCliIds);
        Assert.Contains("gemini-3.1-pro-preview", geminiCliIds);
        Assert.Contains("gemini-3.1-pro-high", antigravityIds);
        Assert.Contains("claude-opus-4-6-thinking", antigravityIds);
        Assert.Contains("deepseek-v4-flash", deepseekIds);
        Assert.Contains("deepseek-v4-pro", deepseekIds);
        Assert.Contains("openai/gpt-oss-120b", groqIds);
        Assert.Contains("qwen/qwen3-32b", groqIds);
        Assert.Contains("gpt-oss-120b", cerebrasIds);
        Assert.Contains("zai-glm-4.7", cerebrasIds);
        Assert.True(openrouterIds.Count >= 250);
        Assert.Contains("anthropic/claude-sonnet-4.6", openrouterIds);
        Assert.Contains("openai/gpt-oss-120b", openrouterIds);
        Assert.Contains("grok-4.3", xaiIds);
        Assert.Contains("mimo-v2.5-pro", xiaomiIds);
        Assert.Contains("claude-sonnet-4-6", opencodeIds);
        Assert.Contains("gpt-5.4", opencodeIds);
        Assert.Contains("accounts/fireworks/models/kimi-k2p6", fireworksIds);
        Assert.Contains("accounts/fireworks/models/glm-5p2", fireworksIds);
        Assert.Contains("claude-sonnet-4-6", cloudflareAiGatewayIds);
        Assert.Contains("workers-ai/@cf/moonshotai/kimi-k2.6", cloudflareAiGatewayIds);
    }

    [Fact]
    public void ResolveSelection_UsesCanonicalDefaultsAndModelReferences()
    {
        var catalog = CreateBuiltInCatalog();

        var implicitDefault = catalog.ResolveSelection();
        var explicitDefault = catalog.ResolveSelection("default", "default");
        var canonicalReference = catalog.ResolveSelection(modelHint: "google-antigravity/claude-opus-4-6-thinking");
        var exactId = catalog.ResolveSelection(modelHint: "claude-opus-4-6-thinking");
        var deepseekDefault = catalog.ResolveSelection(providerHint: "deepseek");
        var groqReference = catalog.ResolveSelection(modelHint: "groq/openai/gpt-oss-120b");
        var openrouterDefault = catalog.ResolveSelection(providerHint: "openrouter");
        var xaiDefault = catalog.ResolveSelection(providerHint: "xai");
        var opencodeDefault = catalog.ResolveSelection(providerHint: "opencode");
        var fireworksDefault = catalog.ResolveSelection(providerHint: "fireworks");
        var cloudflareAiGatewayDefault = catalog.ResolveSelection(providerHint: "cloudflare-ai-gateway");

        Assert.Equal("openai", implicitDefault.Provider);
        Assert.Equal("gpt-5.4", implicitDefault.ModelId);
        Assert.Equal(implicitDefault, explicitDefault);
        Assert.Equal("google-antigravity", canonicalReference.Provider);
        Assert.Equal("claude-opus-4-6-thinking", canonicalReference.ModelId);
        Assert.Equal(canonicalReference, exactId);
        Assert.Equal("deepseek", deepseekDefault.Provider);
        Assert.Equal("deepseek-v4-pro", deepseekDefault.ModelId);
        Assert.Equal("groq", groqReference.Provider);
        Assert.Equal("openai/gpt-oss-120b", groqReference.ModelId);
        Assert.Equal("openrouter", openrouterDefault.Provider);
        Assert.Equal("anthropic/claude-sonnet-4.6", openrouterDefault.ModelId);
        Assert.Equal("xai", xaiDefault.Provider);
        Assert.Equal("grok-4.3", xaiDefault.ModelId);
        Assert.Equal("opencode", opencodeDefault.Provider);
        Assert.Equal("claude-sonnet-4-6", opencodeDefault.ModelId);
        Assert.Equal("fireworks", fireworksDefault.Provider);
        Assert.Equal("accounts/fireworks/models/kimi-k2p6", fireworksDefault.ModelId);
        Assert.Equal("cloudflare-ai-gateway", cloudflareAiGatewayDefault.Provider);
        Assert.Equal("claude-sonnet-4-6", cloudflareAiGatewayDefault.ModelId);
    }

    [Fact]
    public void ResolveSelection_PrefersResolvedProviderButRejectsConflictingReferences()
    {
        var catalog = CreateBuiltInCatalog();

        var preferred = catalog.ResolveSelection(modelHint: "gpt-5.4");
        var conflicting = Assert.Throws<InvalidOperationException>(() => catalog.ResolveSelection("openai", "google-antigravity/claude-opus-4-6-thinking"));

        Assert.Equal("openai", preferred.Provider);
        Assert.Equal("gpt-5.4", preferred.ModelId);
        Assert.Contains("conflicts", conflicting.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_LoadsCustomModelsAndProviderOverrides()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openrouter": {
                      "baseUrl": "https://openrouter.ai/api/v1",
                      "api": "openai-completions",
                      "headers": {
                        "HTTP-Referer": "https://tau.local"
                      },
                      "compat": {
                        "supportsDeveloperRole": false,
                        "supportsUsageInStreaming": false,
                        "maxTokensField": "max_completion_tokens",
                        "requiresToolResultName": true,
                        "requiresAssistantAfterToolResult": true,
                        "requiresReasoningContentOnAssistantMessages": true,
                        "thinkingFormat": "openrouter"
                      },
                      "models": [
                        {
                          "id": "anthropic/claude-sonnet-4",
                          "name": "Claude Sonnet 4 via OpenRouter",
                          "reasoning": true,
                          "input": ["text", "image"],
                          "contextWindow": 200000,
                          "maxTokens": 64000,
                          "cost": {
                            "input": 3,
                            "output": 15,
                            "cacheRead": 0.3,
                            "cacheWrite": 3.75
                          },
                          "compat": {
                            "openRouterRouting": {
                              "only": ["anthropic"],
                              "allow_fallbacks": false
                            }
                          }
                        },
                        {
                          "id": "local/qwen3",
                          "api": "openai-compatible",
                          "baseUrl": "http://localhost:1234/v1"
                        }
                      ]
                    }
                  }
                }
                """);

            var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([modelsPath]));

            var model = catalog.GetModel("openrouter", "anthropic/claude-sonnet-4");

            Assert.Equal("openai-chat-completions", model.Api);
            Assert.Equal("https://openrouter.ai/api/v1", model.BaseUrl);
            Assert.True(model.Reasoning);
            Assert.Contains("image", model.InputModalities);
            Assert.Equal(200_000, model.ContextWindow);
            Assert.Equal(64_000, model.MaxOutputTokens);
            Assert.Equal(3m, model.Cost!.Value.InputPerMillion);
            Assert.Equal("https://tau.local", model.Headers!["HTTP-Referer"]);
            Assert.False(model.Compat!.SupportsDeveloperRole);
            Assert.False(model.Compat.SupportsUsageInStreaming);
            Assert.Equal("max_completion_tokens", model.Compat.MaxTokensField);
            Assert.True(model.Compat.RequiresToolResultName);
            Assert.True(model.Compat.RequiresAssistantAfterToolResult);
            Assert.True(model.Compat.RequiresReasoningContentOnAssistantMessages);
            Assert.Equal("openrouter", model.Compat.ThinkingFormat);
            Assert.Equal("anthropic", ((JsonElement)model.Compat.OpenRouterRouting!["only"])[0].GetString());
            Assert.False(((JsonElement)model.Compat.OpenRouterRouting["allow_fallbacks"]).GetBoolean());

            var aliasModel = catalog.GetModel("openrouter", "local/qwen3");
            Assert.Equal("openai-chat-completions", aliasModel.Api);
            Assert.Equal("http://localhost:1234/v1", aliasModel.BaseUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Constructor_IgnoresUnreadableOrInvalidCustomModelsConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            File.WriteAllText(modelsPath, "{ invalid json");

            var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([modelsPath]));

            Assert.Equal("gpt-5.4", catalog.GetModel("openai", "gpt-5.4").Id);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Constructor_AppliesProviderAndModelOverridesToBuiltIns()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "openai": {
                      "baseUrl": "https://proxy.example.com/v1",
                      "headers": {
                        "X-Proxy": "tau"
                      },
                      "compat": {
                        "supportsUsageInStreaming": false
                      },
                      "modelOverrides": {
                        "gpt-5.4": {
                          "name": "GPT-5.4 via Proxy",
                          "reasoning": false,
                          "cost": {
                            "input": 9.5
                          },
                          "headers": {
                            "X-Model": "gpt-54"
                          },
                          "compat": {
                            "maxTokensField": "max_completion_tokens",
                            "requiresToolResultName": true,
                            "requiresAssistantAfterToolResult": true
                          }
                        },
                        "missing-model": {
                          "name": "Ignored"
                        }
                      }
                    }
                  }
                }
                """);

            var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([modelsPath]));

            var model = catalog.GetModel("openai", "gpt-5.4");

            Assert.Equal("GPT-5.4 via Proxy", model.Name);
            Assert.Equal("https://proxy.example.com/v1", model.BaseUrl);
            Assert.False(model.Reasoning);
            Assert.Equal(9.5m, model.Cost!.Value.InputPerMillion);
            Assert.Equal(10m, model.Cost.Value.OutputPerMillion);
            Assert.Equal("tau", model.Headers!["X-Proxy"]);
            Assert.Equal("gpt-54", model.Headers["X-Model"]);
            Assert.False(model.Compat!.SupportsUsageInStreaming);
            Assert.Equal("max_completion_tokens", model.Compat.MaxTokensField);
            Assert.True(model.Compat.RequiresToolResultName);
            Assert.True(model.Compat.RequiresAssistantAfterToolResult);
            Assert.Null(catalog.TryGetModel("openai", "missing-model"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetModel_ReturnsExpectedModel()
    {
        var catalog = CreateBuiltInCatalog();

        var model = catalog.GetModel("openai-codex", "gpt-5.2-codex");

        Assert.Equal("openai-codex-responses", model.Api);
        Assert.Equal("openai-codex", model.Provider);
        Assert.True(model.Reasoning);
    }

    [Fact]
    public void CalculateCost_ReturnsActualUsageCost()
    {
        var model = new Model
        {
            Id = "test",
            Name = "Test",
            Api = "openai-chat-completions",
            Provider = "openai",
            Cost = new ModelCost(2m, 8m, 0.5m, 1m)
        };

        var cost = ModelCatalog.CalculateCost(model, new Usage(500_000, 250_000, 100_000, 50_000));

        Assert.Equal(1.0m, cost.Input);
        Assert.Equal(2.0m, cost.Output);
        Assert.Equal(0.05m, cost.CacheRead);
        Assert.Equal(0.05m, cost.CacheWrite);
        Assert.Equal(3.10m, cost.Total);
    }

    [Fact]
    public void CalculateCost_AppliesResponsesServiceTierMultiplier()
    {
        var model = new Model
        {
            Id = "test",
            Name = "Test",
            Api = "openai-responses",
            Provider = "openai",
            Cost = new ModelCost(2m, 8m, 0.5m, 1m)
        };

        var priority = ModelCatalog.CalculateCost(model, new Usage(500_000, 250_000, 100_000, 50_000, "priority"));
        var flex = ModelCatalog.CalculateCost(model, new Usage(500_000, 250_000, 100_000, 50_000, "flex"));

        Assert.Equal(6.20m, priority.Total);
        Assert.Equal(1.55m, flex.Total);
        Assert.Equal(2m, ModelCatalog.GetServiceTierCostMultiplier("priority"));
        Assert.Equal(0.5m, ModelCatalog.GetServiceTierCostMultiplier("flex"));
    }

    [Fact]
    public void CreateOpenAiCompatibleModel_PreservesCompatibilityMetadata()
    {
        var compat = new ModelCompatibility
        {
            SupportsUsageInStreaming = false,
            MaxTokensField = "max_tokens",
            VercelGatewayRouting = new VercelGatewayRouting
            {
                Only = ["fireworks"],
                Order = ["fireworks", "novita"]
            }
        };

        var model = ModelCatalog.CreateOpenAiCompatibleModel(
            "vercel-ai-gateway",
            "moonshotai/kimi-k2.5",
            "Kimi K2.5",
            "https://ai-gateway.vercel.sh/v1",
            reasoning: true,
            contextWindow: 262_144,
            maxTokens: 262_144,
            inputCost: 0.6m,
            outputCost: 3m,
            compat: compat);

        Assert.False(model.Compat!.SupportsUsageInStreaming);
        Assert.Equal("max_tokens", model.Compat.MaxTokensField);
        Assert.Equal("novita", model.Compat.VercelGatewayRouting!.Order![1]);
    }

    [Fact]
    public void SupportsXhigh_ReturnsTrue_ForGpt54AndOpus46()
    {
        var gpt = new Model { Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-chat-completions", Provider = "openai" };
        var opus = new Model { Id = "claude-opus-4-6", Name = "Claude Opus 4.6", Api = "anthropic-messages", Provider = "anthropic" };

        Assert.True(ModelCatalog.SupportsXhigh(gpt));
        Assert.True(ModelCatalog.SupportsXhigh(opus));
    }

    private static ModelCatalog CreateBuiltInCatalog() =>
        new(configurationStore: new ModelConfigurationStore([]));
}
