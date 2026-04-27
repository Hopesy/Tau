namespace Tau.Mom;

public interface IDelegationAgentRunner
{
    Task<DelegationExecution> ExecuteAsync(DelegationRequest request, CancellationToken cancellationToken = default);
}
