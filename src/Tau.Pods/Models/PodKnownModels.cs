namespace Tau.Pods.Models;

public sealed class PodKnownModelsFile
{
    public Dictionary<string, PodKnownModelInfo> Models { get; set; } = [];
}

public sealed class PodKnownModelInfo
{
    public string Name { get; set; } = string.Empty;
    public List<PodKnownModelConfigEntry> Configs { get; set; } = [];
    public string? Notes { get; set; }
}

public sealed class PodKnownModelConfigEntry
{
    public int GpuCount { get; set; }
    public List<string>? GpuTypes { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string>? Env { get; set; }
    public string? Notes { get; set; }
}

public sealed record PodKnownModelConfig(
    string ModelId,
    string Name,
    int GpuCount,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Notes = null);
