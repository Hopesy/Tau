using Tau.Agent;

namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentRunner
{
    IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default);
}
