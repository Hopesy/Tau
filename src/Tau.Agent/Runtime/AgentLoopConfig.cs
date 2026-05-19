using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Agent.Runtime;

/// <summary>
/// Configuration for a single agent run.
/// </summary>
public record AgentLoopConfig
{
    public required Model Model { get; init; }
    public required ProviderRegistry ProviderRegistry { get; init; }
    public required IReadOnlyList<IAgentTool> Tools { get; init; }
    public IReadOnlyList<IToolInterceptor> Interceptors { get; init; } = [];
    public ITauLogSink LogSink { get; init; } = NullTauLogSink.Instance;
    public string? SystemPrompt { get; init; }
    public SimpleStreamOptions? StreamOptions { get; init; }
    public ToolExecutionMode DefaultExecutionMode { get; init; } = ToolExecutionMode.Parallel;

    /// <summary>
    /// Transform agent messages before sending to LLM (pruning, injection).
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? TransformContext { get; init; }

    /// <summary>
    /// Convert agent messages to LLM-visible messages (filter custom types).
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? ConvertToLlm { get; init; }
}
