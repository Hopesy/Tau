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
    string? DeploymentName = null,
    string? ModelId = null,
    PodExecResult? Execution = null);

public sealed record PodStopResult(
    string PodId,
    bool Success,
    string Summary,
    string? DeploymentName = null,
    PodExecResult? Execution = null);

public sealed record PodLogsResult(
    string PodId,
    bool Success,
    string Summary,
    string? Output = null,
    string? DeploymentName = null,
    int? Tail = null,
    string? Command = null,
    int? ExitCode = null,
    string? StdErr = null,
    string FailureKind = PodExecFailureKinds.None);

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
