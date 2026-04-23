using Tau.Agent;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class FakeCodingAgentRunner : ICodingAgentRunner
{
    private readonly Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> _run;

    public FakeCodingAgentRunner(Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> run)
    {
        _run = run;
    }

    public List<string> Inputs { get; } = [];

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        Inputs.Add(input);
        return _run(input, cancellationToken);
    }
}
