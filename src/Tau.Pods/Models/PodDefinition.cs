namespace Tau.Pods.Models;

public sealed class PodDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? SshCommand { get; set; }
    public string? SshHost { get; set; }
    public int? SshPort { get; set; }
    public string? ModelsPath { get; set; }
    public string? VllmVersion { get; set; }
    public List<PodGpuInfo> Gpus { get; set; } = [];
    public Dictionary<string, PodConfiguredModel> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Enabled { get; set; } = true;
    public List<string> Tags { get; set; } = [];
}
