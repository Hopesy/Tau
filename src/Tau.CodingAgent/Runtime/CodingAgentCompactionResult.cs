namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentCompactionResult(
    string Summary,
    int MessagesBefore,
    int MessagesAfter);
