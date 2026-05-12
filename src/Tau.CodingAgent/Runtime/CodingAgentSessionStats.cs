namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSessionStats(
    string Provider,
    string Model,
    int TotalMessages,
    int UserMessages,
    int AssistantMessages,
    int ToolResultMessages,
    int ToolCalls,
    int EstimatedTokens,
    int? ContextWindowTokens,
    string? SessionName,
    string? SessionFile);
