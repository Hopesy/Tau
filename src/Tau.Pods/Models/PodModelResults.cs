namespace Tau.Pods.Models;

public sealed record PodCachedModelInfo(
    string ModelId,
    string CacheDirectory);

public sealed record PodModelListResult(
    string PodId,
    bool Success,
    string Summary,
    IReadOnlyList<PodCachedModelInfo> Models);

public sealed record PodModelOperationResult(
    string PodId,
    bool Success,
    string Operation,
    string ModelId,
    string Summary,
    string? Output = null);

public sealed record PodModelStatusResult(
    string PodId,
    bool Success,
    string ModelId,
    bool Present,
    string Summary,
    string? Output = null);
