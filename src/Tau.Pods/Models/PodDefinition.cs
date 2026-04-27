namespace Tau.Pods.Models;

public sealed class PodDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? SshHost { get; set; }
    public int? SshPort { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> Tags { get; set; } = [];
}
