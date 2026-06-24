namespace Tau.AgentCore.Extensions;

/// <summary>
/// Extension point for adding tools, interceptors, and custom behavior to the agent.
/// </summary>
public interface IAgentExtension
{
    string Name { get; }

    IReadOnlyList<IAgentTool> GetTools() => [];
    IReadOnlyList<IToolInterceptor> GetInterceptors() => [];

    Task OnAgentStartAsync(CancellationToken ct = default) => Task.CompletedTask;
    Task OnAgentEndAsync(CancellationToken ct = default) => Task.CompletedTask;
}
