namespace Tau.Pods.Models;

public sealed class PodConfiguredModel
{
    public string Model { get; set; } = string.Empty;
    public int Port { get; set; }
    public List<int> Gpu { get; set; } = [];
    public int Pid { get; set; }
}
