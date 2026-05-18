namespace Tau.Pods.Models;

public sealed record PodHealthResult(
    string PodId,
    bool Healthy,
    string Transport,
    string Summary,
    TimeSpan? Latency = null);

public sealed record PodDeployResult(
    string PodId,
    bool Success,
    string Summary,
    string? DeploymentName = null);

public sealed record PodStopResult(
    string PodId,
    bool Success,
    string Summary);

public sealed record PodLogsResult(
    string PodId,
    bool Success,
    string Summary,
    string? Output = null);

public sealed record PodDeploymentInfo(
    string Name,
    string? Model,
    string? Status,
    string? Timestamp);

public sealed record PodDeploymentsResult(
    string PodId,
    bool Success,
    string Summary,
    IReadOnlyList<PodDeploymentInfo> Deployments);
