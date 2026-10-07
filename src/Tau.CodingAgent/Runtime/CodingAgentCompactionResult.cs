using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentCompactionResult(
    string Summary,
    int MessagesBefore,
    int MessagesAfter,
    int TokensBefore = 0,
    string? FirstKeptEntryId = null,
    bool FromHook = false)
{
    public JsonElement? Details { get; init; }
    public JsonElement? Usage { get; init; }
    public int? EstimatedTokensAfter { get; init; }
    /// <summary>压缩已持久化时的条目标识，防止宿主再次记录同一结果。</summary>
    public string? StoredEntryId { get; init; }
    /// <summary>摘要已包含按上游边界生成的回合前缀，不再生成旧式附加前缀。</summary>
    public bool UsesPreparedBoundary { get; init; }
}

public sealed record CodingAgentBranchSummaryResult(
    string Summary,
    int EntryCount,
    int TokensBefore = 0,
    IReadOnlyList<string>? ReadFiles = null,
    IReadOnlyList<string>? ModifiedFiles = null,
    bool FromHook = false)
{
    public JsonElement? Usage { get; init; }
}
