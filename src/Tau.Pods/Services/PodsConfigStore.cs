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

    public void ApplySetupResult(PodsConfig config, string podId, PodSetupRunResult result)
    {
        var pod = config.Pods.FirstOrDefault(candidate => candidate.Id.Equals(podId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Pod not found: {podId}");

        pod.ModelsPath = result.Plan.ModelsPath;
        pod.VllmVersion = result.Plan.VllmVersion;
        pod.Gpus = result.Gpus.Select(gpu => new PodGpuInfo(gpu.Id, gpu.Name, gpu.Memory)).ToList();
    }

    public void AddOrUpdatePod(PodsConfig config, PodDefinition pod)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(pod);

        var existingIndex = config.Pods.FindIndex(candidate => candidate.Id.Equals(pod.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            config.Pods[existingIndex] = pod;
        }
        else
        {
            config.Pods.Add(pod);
        }

        if (string.IsNullOrWhiteSpace(config.ActivePodId))
        {
            config.ActivePodId = pod.Id;
        }
    }

    public bool SetActivePod(PodsConfig config, string podId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Pods.Any(candidate => candidate.Id.Equals(podId, StringComparison.OrdinalIgnoreCase)))
        {
            config.ActivePodId = podId;
            return true;
        }

        return false;
    }

    public bool RemovePod(PodsConfig config, string podId)
    {
        ArgumentNullException.ThrowIfNull(config);
        var removed = config.Pods.RemoveAll(candidate => candidate.Id.Equals(podId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed && config.ActivePodId is not null && config.ActivePodId.Equals(podId, StringComparison.OrdinalIgnoreCase))
        {
            config.ActivePodId = null;
        }

        return removed;
    }

    public PodsConfig CreateSample()
    {
        return new PodsConfig
        {
            ActivePodId = "dev-pod-1",
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
                    VllmVersion = "release",
                    Enabled = false,
                    Tags = ["staging"]
                }
            ]
        };
    }
}
