using System.Text.Json.Serialization;

namespace Tau.Pods.Models;

public sealed class PodsConfig
{
    [JsonPropertyName("active")]
    public string? ActivePodId { get; set; }

    public List<PodDefinition> Pods { get; set; } = [];
}
