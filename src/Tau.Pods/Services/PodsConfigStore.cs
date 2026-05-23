using System.Text.Json;
using Tau.Pods.Models;
using Tau.Pods.Serialization;

namespace Tau.Pods.Services;

public sealed class PodsConfigStore
{
    public PodsConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, PodsJsonContext.Default.PodsConfig) ?? new PodsConfig();
    }

    public void Save(string path, PodsConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(config, PodsJsonContext.Default.PodsConfig));
    }

    public PodsConfig CreateSample()
    {
        return new PodsConfig
        {
            Pods =
            [
                new PodDefinition
                {
                    Id = "dev-pod-1",
                    Provider = "vllm",
                    Model = "gpt-oss-120b",
                    Region = "local-lab",
                    Endpoint = "http://127.0.0.1:8000/v1",
                    Enabled = true,
                    Tags = ["default", "lab"]
                },
                new PodDefinition
                {
                    Id = "gpu-pod-2",
                    Provider = "ssh",
                    Model = "deepseek-r1",
                    Region = "ap-south-1",
                    SshHost = "pods.example.internal",
                    SshPort = 22,
                    ModelsPath = "/mnt/models",
                    Enabled = false,
                    Tags = ["staging"]
                }
            ]
        };
    }
}
