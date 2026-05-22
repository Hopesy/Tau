namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentCompactionResult(
    string Summary,
    int MessagesBefore,
    int MessagesAfter,
    int TokensBefore = 0,
    string? FirstKeptEntryId = null,
    bool FromHook = false);

public sealed record CodingAgentBranchSummaryResult(
    string Summary,
    int EntryCount,
    int TokensBefore = 0,
    IReadOnlyList<string>? ReadFiles = null,
    IReadOnlyList<string>? ModifiedFiles = null,
    bool FromHook = false);
