namespace Tau.Pods.Models;

public sealed record PodVllmOperationResult(
    string PodId,
    bool Success,
    string Operation,
    string DeploymentName,
    string Summary,
    string Command,
    int ExitCode,
    string StdOut,
    string StdErr,
    PodVllmServePlan? Plan = null,
    PodVllmHealthResult? Health = null,
    PodVllmRollbackResult? Rollback = null,
    PodVllmPreflightResult? Preflight = null);

public sealed record PodVllmPreflightResult(
    string PodId,
    bool Success,
    string DeploymentName,
    string ModelId,
    string ModelCachePath,
    string? ResolvedModelPath,
    bool ModelCachePresent,
    int SnapshotCount,
    bool VllmAvailable,
    string FailureKind,
    string Summary,
    string Command,
    int ExitCode,
    string StdOut,
    string StdErr);

public sealed record PodVllmStatusResult(
    string PodId,
    bool Success,
    string DeploymentName,
    string Summary,
    string Command,
    int ExitCode,
    string StdOut,
    string StdErr,
    string State = "unknown",
    bool Ready = false,
    bool Unhealthy = false);

public sealed record PodVllmHealthResult(
    string PodId,
    bool Success,
    string DeploymentName,
    bool Ready,
    string State,
    bool Unhealthy,
    string FailureKind,
    int Attempts,
    int MaxAttempts,
    string Summary,
    string Command,
    int ExitCode,
    string StdOut,
    string StdErr);

public sealed record PodVllmRollbackResult(
    string PodId,
    bool Success,
    string DeploymentName,
    string Summary,
    string Command,
    int ExitCode,
    string StdOut,
    string StdErr);
