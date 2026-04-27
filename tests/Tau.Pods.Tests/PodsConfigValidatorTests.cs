using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodsConfigValidatorTests
{
    [Fact]
    public void Validate_ValidSampleConfig_ReturnsNoErrors()
    {
        var store = new PodsConfigStore();
        var validator = new PodsConfigValidator();

        var errors = validator.Validate(store.CreateSample());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_DuplicateIds_ReturnsError()
    {
        var validator = new PodsConfigValidator();
        var config = new PodsConfig
        {
            Pods =
            [
                new PodDefinition { Id = "dup", Provider = "vllm", Model = "model-a", Region = "lab", Endpoint = "http://localhost:8000" },
                new PodDefinition { Id = "dup", Provider = "vllm", Model = "model-b", Region = "lab", Endpoint = "http://localhost:8001" }
            ]
        };

        var errors = validator.Validate(config);

        Assert.Contains(errors, error => error.Contains("Duplicate pod id", StringComparison.Ordinal));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsConfig()
    {
        var store = new PodsConfigStore();
        var path = Path.Combine(Path.GetTempPath(), $"tau-pods-{Guid.NewGuid():N}.json");
        var config = store.CreateSample();

        try
        {
            store.Save(path, config);
            var loaded = store.Load(path);

            Assert.Equal(config.Pods.Count, loaded.Pods.Count);
            Assert.Equal(config.Pods[0].Id, loaded.Pods[0].Id);
            Assert.Equal(config.Pods[1].SshHost, loaded.Pods[1].SshHost);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
