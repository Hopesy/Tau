namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentToolResultDetailsProvider
{
    IReadOnlyDictionary<string, object?> ToolResultDetailsByToolCallId { get; }
}
