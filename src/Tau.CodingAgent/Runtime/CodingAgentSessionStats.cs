namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSessionStats(
    string Provider,
    string Model,
    int TotalMessages,
    int UserMessages,
    int AssistantMessages,
    int ToolResultMessages,
    int ToolCalls,
    string? SessionName,
    string? SessionFile);
