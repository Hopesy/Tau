using System.Text.Json;
using Tau.Pods.Models;
using Tau.Pods.Serialization;

namespace Tau.Pods.Services;

public sealed class PodsConfigStore
{
    public PodsConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PodsConfig();
        }

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("pods", out var podsElement) &&
            podsElement.ValueKind == JsonValueKind.Object)
        {
            var upstream = JsonSerializer.Deserialize(json, PodsJsonContext.Default.UpstreamPodsConfig);
            return FromUpstream(upstream);
        }

        return JsonSerializer.Deserialize(json, PodsJsonContext.Default.PodsConfig) ?? new PodsConfig();
    }

    public void Save(string path, PodsConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var upstream = ToUpstream(config);
        File.WriteAllText(path, JsonSerializer.Serialize(upstream, PodsJsonContext.Default.UpstreamPodsConfig));
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
                    SshCommand = "ssh pods.example.internal",
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

    private static PodsConfig FromUpstream(UpstreamPodsConfig? upstream)
    {
        if (upstream is null)
        {
            return new PodsConfig();
        }

        var config = new PodsConfig
        {
            ActivePodId = upstream.Active
        };

        foreach (var (id, pod) in upstream.Pods)
        {
            TryParseSimpleSshCommand(pod.Ssh, out var parsedHost, out var parsedPort);
            var sshHost = FirstNonWhiteSpace(pod.SshHost, parsedHost);
            config.Pods.Add(new PodDefinition
            {
                Id = id,
                Provider = FirstNonWhiteSpace(pod.Provider, string.IsNullOrWhiteSpace(pod.Endpoint) ? "ssh" : "vllm")!,
                Model = FirstNonWhiteSpace(pod.Model, "unassigned")!,
                Region = FirstNonWhiteSpace(pod.Region, "registered")!,
                Endpoint = pod.Endpoint,
                SshCommand = string.IsNullOrWhiteSpace(pod.Ssh) ? null : pod.Ssh,
                SshHost = sshHost,
                SshPort = pod.SshPort ?? parsedPort,
                ModelsPath = pod.ModelsPath,
                VllmVersion = pod.VllmVersion,
                Gpus = pod.Gpus.ToList(),
                Models = new Dictionary<string, PodConfiguredModel>(pod.Models, StringComparer.OrdinalIgnoreCase),
                Enabled = pod.Enabled ?? true,
                Tags = pod.Tags?.ToList() ?? []
            });
        }

        return config;
    }

    private static UpstreamPodsConfig ToUpstream(PodsConfig config)
    {
        var upstream = new UpstreamPodsConfig
        {
            Active = config.ActivePodId
        };

        foreach (var pod in config.Pods)
        {
            upstream.Pods[pod.Id] = new UpstreamPodDefinition
            {
                Ssh = BuildSshCommand(pod),
                Gpus = pod.Gpus.ToList(),
                Models = new Dictionary<string, PodConfiguredModel>(pod.Models, StringComparer.OrdinalIgnoreCase),
                ModelsPath = pod.ModelsPath,
                VllmVersion = pod.VllmVersion,
                Provider = EmptyToNull(pod.Provider),
                Model = EmptyToNull(pod.Model),
                Region = EmptyToNull(pod.Region),
                Endpoint = pod.Endpoint,
                SshHost = pod.SshHost,
                SshPort = pod.SshPort,
                Enabled = pod.Enabled,
                Tags = pod.Tags.Count == 0 ? null : pod.Tags.ToList()
            };
        }

        return upstream;
    }

    private static string BuildSshCommand(PodDefinition pod)
    {
        if (!string.IsNullOrWhiteSpace(pod.SshCommand))
        {
            return pod.SshCommand.Trim();
        }

        if (string.IsNullOrWhiteSpace(pod.SshHost))
        {
            return string.Empty;
        }

        var host = pod.SshHost.Trim();
        var port = pod.SshPort ?? 22;
        return port == 22 ? $"ssh {host}" : $"ssh -p {port} {host}";
    }

    private static bool TryParseSimpleSshCommand(string? sshCommand, out string? host, out int? port)
    {
        host = null;
        port = null;
        if (string.IsNullOrWhiteSpace(sshCommand))
        {
            return false;
        }

        var tokens = sshCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var index = 0;
        if (tokens[index].Equals("ssh", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        var parsedPort = 22;
        if (index < tokens.Length && tokens[index].Equals("-p", StringComparison.Ordinal))
        {
            if (index + 1 >= tokens.Length || !int.TryParse(tokens[index + 1], out parsedPort) || parsedPort is < 1 or > 65535)
            {
                return false;
            }

            index += 2;
        }
        else if (index < tokens.Length && tokens[index].StartsWith("-p", StringComparison.Ordinal) && tokens[index].Length > 2)
        {
            if (!int.TryParse(tokens[index][2..], out parsedPort) || parsedPort is < 1 or > 65535)
            {
                return false;
            }

            index++;
        }

        if (tokens.Skip(index).Any(token => token.StartsWith("-", StringComparison.Ordinal)))
        {
            return false;
        }

        if (tokens.Length - index != 1)
        {
            return false;
        }

        var parsedHost = tokens[index].Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(parsedHost))
        {
            return false;
        }

        host = parsedHost;
        port = parsedPort;
        return true;
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
