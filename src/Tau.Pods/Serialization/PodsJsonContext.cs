using System.Text.Json.Serialization;
using Tau.Pods.Models;

namespace Tau.Pods.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PodsConfig))]
[JsonSerializable(typeof(UpstreamPodsConfig))]
[JsonSerializable(typeof(UpstreamPodDefinition))]
[JsonSerializable(typeof(PodConfiguredModel))]
[JsonSerializable(typeof(PodKnownModelsFile))]
[JsonSerializable(typeof(PodProbeResult))]
[JsonSerializable(typeof(PodProbeResult[]))]
internal sealed partial class PodsJsonContext : JsonSerializerContext
{
}
