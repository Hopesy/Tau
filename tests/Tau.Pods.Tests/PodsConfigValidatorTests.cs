using System.Text.Json;
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
    public void Validate_ActivePodMustReferenceExistingPod()
    {
        var validator = new PodsConfigValidator();
        var config = new PodsConfig
        {
            ActivePodId = "missing-pod",
            Pods =
            [
                new PodDefinition
                {
                    Id = "gpu-a",
                    Provider = "ssh",
                    Model = "llama",
                    Region = "lab",
                    SshHost = "pods.example.internal",
                    SshPort = 22
                }
            ]
        };

        var errors = validator.Validate(config);

        Assert.Contains(errors, error => error.Contains("Active pod not found: missing-pod", StringComparison.Ordinal));
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
            Assert.Equal(config.ActivePodId, loaded.ActivePodId);
            Assert.Equal(config.Pods[0].Id, loaded.Pods[0].Id);
            Assert.Equal(config.Pods[1].SshHost, loaded.Pods[1].SshHost);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingPathReturnsEmptyConfig()
    {
        var store = new PodsConfigStore();
        var path = Path.Combine(Path.GetTempPath(), $"tau-pods-missing-{Guid.NewGuid():N}.json");

        var loaded = store.Load(path);

        Assert.Null(loaded.ActivePodId);
        Assert.Empty(loaded.Pods);
    }

    [Fact]
    public void Load_RecordShapedUpstreamConfigConvertsToInternalPods()
    {
        var store = new PodsConfigStore();
        var path = Path.Combine(Path.GetTempPath(), $"tau-pods-upstream-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "active": "gpu-a",
              "pods": {
                "gpu-a": {
                  "ssh": "ssh -p 2222 root@example.com",
                  "gpus": [
                    { "id": 0, "name": "NVIDIA H100", "memory": "80 GiB" }
                  ],
                  "models": {
                    "llama": { "model": "meta/llama", "port": 8001, "gpu": [0], "pid": 1234 }
                  },
                  "modelsPath": "/mnt/models",
                  "vllmVersion": "release"
                }
              }
            }
            """);

        try
        {
            var loaded = store.Load(path);

            Assert.Equal("gpu-a", loaded.ActivePodId);
            var pod = Assert.Single(loaded.Pods);
            Assert.Equal("gpu-a", pod.Id);
            Assert.Equal("ssh", pod.Provider);
            Assert.Equal("unassigned", pod.Model);
            Assert.Equal("registered", pod.Region);
            Assert.Equal("ssh -p 2222 root@example.com", pod.SshCommand);
            Assert.Equal("root@example.com", pod.SshHost);
            Assert.Equal(2222, pod.SshPort);
            Assert.Equal("/mnt/models", pod.ModelsPath);
            Assert.Equal("release", pod.VllmVersion);
            var gpu = Assert.Single(pod.Gpus);
            Assert.Equal(0, gpu.Id);
            Assert.Equal("NVIDIA H100", gpu.Name);
            Assert.Equal("80 GiB", gpu.Memory);
            var model = Assert.Single(pod.Models);
            Assert.Equal("llama", model.Key);
            Assert.Equal("meta/llama", model.Value.Model);
            Assert.Equal(8001, model.Value.Port);
            Assert.Equal([0], model.Value.Gpu);
            Assert.Equal(1234, model.Value.Pid);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_WritesRecordShapedUpstreamConfig()
    {
        var store = new PodsConfigStore();
        var path = Path.Combine(Path.GetTempPath(), $"tau-pods-record-{Guid.NewGuid():N}.json");
        var config = new PodsConfig
        {
            ActivePodId = "gpu-a",
            Pods =
            [
                new PodDefinition
                {
                    Id = "gpu-a",
                    Provider = "ssh",
                    Model = "unassigned",
                    Region = "registered",
                    SshCommand = "ssh -p 2222 root@example.com",
                    SshHost = "root@example.com",
                    SshPort = 2222,
                    ModelsPath = "/mnt/models",
                    VllmVersion = "gpt-oss",
                    Gpus = [new PodGpuInfo(0, "NVIDIA H100", "80 GiB")],
                    Models =
                    {
                        ["llama"] = new PodConfiguredModel
                        {
                            Model = "meta/llama",
                            Port = 8001,
                            Gpu = [0],
                            Pid = 1234
                        }
                    }
                }
            ]
        };

        try
        {
            store.Save(path, config);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Equal("gpu-a", root.GetProperty("active").GetString());
            var pods = root.GetProperty("pods");
            Assert.Equal(JsonValueKind.Object, pods.ValueKind);
            Assert.True(pods.TryGetProperty("gpu-a", out var pod));
            Assert.Equal("ssh -p 2222 root@example.com", pod.GetProperty("ssh").GetString());
            Assert.Equal("/mnt/models", pod.GetProperty("modelsPath").GetString());
            Assert.Equal("gpt-oss", pod.GetProperty("vllmVersion").GetString());
            Assert.Equal("NVIDIA H100", pod.GetProperty("gpus")[0].GetProperty("name").GetString());
            var model = pod.GetProperty("models").GetProperty("llama");
            Assert.Equal("meta/llama", model.GetProperty("model").GetString());
            Assert.Equal(8001, model.GetProperty("port").GetInt32());
            Assert.Equal(0, model.GetProperty("gpu")[0].GetInt32());
            Assert.Equal(1234, model.GetProperty("pid").GetInt32());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyListShapedConfigStillWorks()
    {
        var store = new PodsConfigStore();
        var path = Path.Combine(Path.GetTempPath(), $"tau-pods-legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "active": "legacy",
              "pods": [
                {
                  "id": "legacy",
                  "provider": "ssh",
                  "model": "llama",
                  "region": "lab",
                  "sshHost": "pods.example.internal",
                  "sshPort": 22,
                  "modelsPath": "/mnt/models",
                  "vllmVersion": "release"
                }
              ]
            }
            """);

        try
        {
            var loaded = store.Load(path);

            Assert.Equal("legacy", loaded.ActivePodId);
            var pod = Assert.Single(loaded.Pods);
            Assert.Equal("legacy", pod.Id);
            Assert.Equal("pods.example.internal", pod.SshHost);
            Assert.Equal(22, pod.SshPort);
            Assert.Equal("/mnt/models", pod.ModelsPath);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
