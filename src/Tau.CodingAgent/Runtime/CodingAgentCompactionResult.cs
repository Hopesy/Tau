namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentCompactionResult(
    string Summary,
    int MessagesBefore,
    int MessagesAfter,
    int TokensBefore = 0,
    string? FirstKeptEntryId = null,
    bool FromHook = false);
