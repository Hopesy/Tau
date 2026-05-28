namespace Tau.Pods.Models;

public sealed record PodVllmServeOptions(
    string ModelId,
    string? DeploymentName = null,
    int Port = 8000,
    string? ServedModelName = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyList<string>? ExtraArgs = null,
    bool WaitForHealth = true,
    int HealthAttempts = 12,
    int HealthBackoffMilliseconds = 5000,
    string? ResolvedModelPath = null,
    string? Revision = null,
    bool Prefetch = false);

public sealed record PodVllmServePlan(
    string DeploymentName,
    string ModelId,
    string ModelPath,
    int Port,
    string ServedModelName,
    string UnitName,
    string ServeCommand,
    string SystemdUnit,
    string MetadataJson,
    string RemoteCommand,
    bool UsesSnapshotDiscovery = false,
    string? Revision = null);
