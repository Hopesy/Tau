using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class ImagesModelsTests
{
    private static readonly ImagesContext Context = new([new TextContent("a red circle")]);

    [Fact]
    public void GetModels_ReturnsRegisteredModelsAndSuppressesThrowingProviders()
    {
        var models = new ImagesModels();
        models.SetProvider(CreateProviderDefinition(
            "good",
            [CreateModel("good", "m1"), CreateModel("good", "m2")]));
        models.SetProvider(new ThrowingImagesProviderDefinition("bad"));

        Assert.Equal(["good", "bad"], models.GetProviders().Select(provider => provider.Id).ToArray());
        Assert.Equal(["m1", "m2"], models.GetModels().Select(model => model.Id).ToArray());
        Assert.Equal(["m1", "m2"], models.GetModels("good").Select(model => model.Id).ToArray());
        Assert.Empty(models.GetModels("bad"));
        Assert.Empty(models.GetModels("missing"));
        Assert.Equal("m2", models.GetModel("good", "m2")?.Id);
        Assert.Null(models.GetModel("good", "missing"));

        Assert.True(models.DeleteProvider("good"));
        Assert.Null(models.GetProvider("good"));
    }

    [Fact]
    public async Task Refresh_WithProviderUpdatesModelListSharesInflightAndRetriesAfterFailure()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        var models = new ImagesModels();
        models.SetProvider(CreateProviderDefinition(
            "dyn",
            [],
            refreshModelsAsync: async cancellationToken =>
            {
                Interlocked.Increment(ref fetches);
                await gate.Task.WaitAsync(cancellationToken);
                return [CreateModel("dyn", "listed")];
            }));

        var first = models.RefreshAsync("dyn");
        var second = models.RefreshAsync("dyn");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref fetches) == 1, TimeSpan.FromSeconds(1)));

        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, fetches);
        Assert.Equal("listed", Assert.Single(models.GetModels("dyn")).Id);

        var attempts = 0;
        models.SetProvider(CreateProviderDefinition(
            "flaky",
            [CreateModel("flaky", "last-known")],
            refreshModelsAsync: _ =>
            {
                attempts++;
                throw new InvalidOperationException("fetch failed");
            }));

        var ex = await Assert.ThrowsAsync<ImagesModelsException>(() => models.RefreshAsync("flaky"));
        Assert.Equal("model_source", ex.Code);
        Assert.Contains("flaky", ex.Message, StringComparison.Ordinal);
        Assert.Equal("last-known", Assert.Single(models.GetModels("flaky")).Id);

        await Assert.ThrowsAsync<ImagesModelsException>(() => models.RefreshAsync("flaky"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Refresh_AllProvidersIsBestEffort()
    {
        var models = new ImagesModels();
        models.SetProvider(CreateProviderDefinition(
            "success",
            [],
            refreshModelsAsync: _ => Task.FromResult<IReadOnlyList<ImagesModel>>([CreateModel("success", "fresh")])));
        models.SetProvider(CreateProviderDefinition(
            "broken",
            [CreateModel("broken", "old")],
            refreshModelsAsync: _ => throw new InvalidOperationException("refresh failed")));

        await models.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("fresh", Assert.Single(models.GetModels("success")).Id);
        Assert.Equal("old", Assert.Single(models.GetModels("broken")).Id);
    }

    [Fact]
    public async Task GenerateImages_UsesProviderIdAndResolvesAuth()
    {
        var calls = new List<GenerateCall>();
        var executor = new CapturingImagesProvider("executor-images-api", calls);
        var model = CreateModel("openrouter", "model-a") with { Api = "not-used-for-dispatch" };
        var models = new ImagesModels();
        models.SetProvider(CreateProviderDefinition("openrouter", [model], executor));

        var auth = models.GetAuth(model, new ImagesOptions
        {
            Env = new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "env-key" }
        });
        Assert.NotNull(auth);
        Assert.True(auth.IsConfigured);
        Assert.Equal("environment", auth.Source);

        var result = await models.GenerateImagesAsync(
            model,
            Context,
            new ImagesOptions
            {
                Env = new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "env-key" }
            });

        Assert.Equal(ImagesStopReason.Stop, result.StopReason);
        Assert.Equal("not-used-for-dispatch", calls[0].Model.Api);
        Assert.Equal("env-key", calls[0].Options.ApiKey);

        await models.GenerateImagesAsync(
            model,
            Context,
            new ImagesOptions
            {
                ApiKey = "explicit-key",
                Env = new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "env-key" }
            });

        Assert.Equal("explicit-key", calls[1].Options.ApiKey);
    }

    [Fact]
    public async Task GenerateImages_MergesModelsJsonRequestConfiguration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tau-images-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var modelsPath = Path.Combine(tempDir, "models.json");
            var authPath = Path.Combine(tempDir, "auth.json");
            File.WriteAllText(authPath, "{}");
            File.WriteAllText(modelsPath, """
                {
                  "providers": {
                    "configured-images": {
                      "apiKey": "CONFIGURED_IMAGES_KEY",
                      "authHeader": true,
                      "headers": {
                        "X-Provider": "CONFIGURED_IMAGES_PROVIDER_HEADER",
                        "X-Shared": "provider"
                      },
                      "options": {
                        "headers": {
                          "X-Option": "CONFIGURED_IMAGES_OPTION_HEADER",
                          "X-Shared": "option"
                        },
                        "env": {
                          "CONFIG_ONLY": "configured",
                          "SHARED_ENV": "configured"
                        },
                        "timeoutMs": 12000,
                        "maxRetryDelayMs": 750,
                        "maxRetries": 3,
                        "metadata": {
                          "source": "configured"
                        }
                      },
                      "models": [
                        {
                          "id": "model-a",
                          "headers": {
                            "X-Model": "CONFIGURED_IMAGES_MODEL_HEADER"
                          }
                        }
                      ]
                    }
                  }
                }
                """);

            var calls = new List<GenerateCall>();
            var configurationStore = new ModelConfigurationStore([modelsPath]);
            var authResolver = new ProviderAuthResolver(
                credentialStore: new OAuthCredentialStore([authPath]),
                configurationStore: configurationStore);
            var imagesModels = new ImagesModels(authResolver: authResolver, configurationStore: configurationStore);
            var model = CreateModel("configured-images", "model-a");
            imagesModels.SetProvider(CreateProviderDefinition(
                "configured-images",
                [model],
                new CapturingImagesProvider("configured-images-api", calls)));

            await imagesModels.GenerateImagesAsync(
                model,
                Context,
                new ImagesOptions
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-Explicit"] = "explicit",
                        ["X-Shared"] = "explicit"
                    },
                    Metadata = new Dictionary<string, object> { ["source"] = "explicit" },
                    Env = new Dictionary<string, string>
                    {
                        ["CONFIGURED_IMAGES_KEY"] = "models-key",
                        ["CONFIGURED_IMAGES_PROVIDER_HEADER"] = "provider-header",
                        ["CONFIGURED_IMAGES_OPTION_HEADER"] = "option-header",
                        ["CONFIGURED_IMAGES_MODEL_HEADER"] = "model-header",
                        ["SHARED_ENV"] = "explicit"
                    }
                });

            var options = Assert.Single(calls).Options;
            Assert.Equal("models-key", options.ApiKey);
            Assert.Equal("provider-header", options.Headers!["X-Provider"]);
            Assert.Equal("option-header", options.Headers["X-Option"]);
            Assert.Equal("model-header", options.Headers["X-Model"]);
            Assert.Equal("explicit", options.Headers["X-Explicit"]);
            Assert.Equal("explicit", options.Headers["X-Shared"]);
            Assert.Equal("Bearer models-key", options.Headers["Authorization"]);
            Assert.Equal("configured", options.Env!["CONFIG_ONLY"]);
            Assert.Equal("explicit", options.Env["SHARED_ENV"]);
            Assert.Equal(TimeSpan.FromSeconds(12), options.Timeout);
            Assert.Equal(TimeSpan.FromMilliseconds(750), options.MaxRetryDelay);
            Assert.Equal(3, options.MaxRetries);
            Assert.Equal("explicit", options.Metadata!["source"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateImages_UnknownProviderReturnsErrorResult()
    {
        var model = CreateModel("ghost", "missing");
        var result = await new ImagesModels().GenerateImagesAsync(model, Context);

        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Equal("ghost", result.Provider);
        Assert.Equal("missing", result.Model);
        Assert.Contains("Unknown image provider: ghost", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInImagesModels_RegistersOpenRouterWithGeneratedCatalog()
    {
        var models = BuiltInProviders.CreateBuiltInImagesModels();

        var provider = Assert.Single(models.GetProviders());
        Assert.Equal("openrouter", provider.Id);
        Assert.Equal("OpenRouter", provider.Name);

        var openRouterModels = models.GetModels("openrouter");
        Assert.Equal(34, openRouterModels.Count);
        Assert.All(openRouterModels, model => Assert.Equal("openrouter-images", model.Api));
        Assert.NotNull(models.GetModel("openrouter", "openrouter/auto"));
    }

    private static ImagesProviderDefinition CreateProviderDefinition(
        string id,
        IEnumerable<ImagesModel> models,
        IImagesProvider? executor = null,
        Func<CancellationToken, Task<IReadOnlyList<ImagesModel>>>? refreshModelsAsync = null) =>
        new(id, executor ?? new CapturingImagesProvider($"{id}-images-api", []), models, id, refreshModelsAsync);

    private static ImagesModel CreateModel(string provider, string id) =>
        new()
        {
            Provider = provider,
            Id = id,
            Name = id,
            Api = "test-images",
            BaseUrl = "https://example.invalid/v1",
            InputModalities = ["text"],
            OutputModalities = ["image"],
            Cost = new ModelCost(0m, 0m, 0m, 0m)
        };

    private sealed class ThrowingImagesProviderDefinition(string id)
        : ImagesProviderDefinition(id, new CapturingImagesProvider($"{id}-images-api", []), [], id)
    {
        public override IReadOnlyList<ImagesModel> GetModels() =>
            throw new InvalidOperationException("getModels failed");
    }

    private sealed record GenerateCall(ImagesModel Model, ImagesContext Context, ImagesOptions Options);

    private sealed class CapturingImagesProvider(string api, IList<GenerateCall> calls) : IImagesProvider
    {
        public string Api { get; } = api;

        public Task<AssistantImages> GenerateImagesAsync(
            ImagesModel model,
            ImagesContext context,
            ImagesOptions options)
        {
            calls.Add(new GenerateCall(model, context, options));
            return Task.FromResult(new AssistantImages
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Output = [new ImageContent("aW1hZ2U=", "image/png")]
            });
        }
    }
}
