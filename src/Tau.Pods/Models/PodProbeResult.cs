namespace Tau.Pods.Models;

public sealed record PodProbeResult(
    string PodId,
    bool Success,
    string Transport,
    string Summary,
    int? StatusCode = null,
    TimeSpan? Latency = null,
    string? Endpoint = null,
    string? Host = null,
    int? Port = null);
