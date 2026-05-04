namespace Tau.Mom;

public sealed record DelegationUsage(
    int InputTokens,
    int OutputTokens,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null,
    decimal? TotalCost = null);
