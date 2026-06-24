using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Runtime;

public sealed record AgentLoopTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    IReadOnlyList<ChatMessage> Context,
    IReadOnlyList<ChatMessage> NewMessages);

public sealed record AgentLoopTurnUpdate(
    IReadOnlyList<ChatMessage>? Context = null,
    Model? Model = null,
    string? SystemPrompt = null,
    IReadOnlyList<IAgentTool>? Tools = null,
    SimpleStreamOptions? StreamOptions = null,
    ThinkingLevel? Reasoning = null,
    bool ClearReasoning = false);

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
    public TauRuntimeLogContext? LogContext { get; init; }
    public string? SystemPrompt { get; init; }
    public SimpleStreamOptions? StreamOptions { get; init; }
    public Func<string, CancellationToken, Task<string?>>? GetApiKeyAsync { get; init; }
    public ToolExecutionMode DefaultExecutionMode { get; init; } = ToolExecutionMode.Parallel;
    public bool SkipInitialSteeringPoll { get; init; }

    /// <summary>
    /// Transform agent messages before sending to LLM (pruning, injection).
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? TransformContext { get; init; }

    /// <summary>
    /// Cancellation-aware transform applied before sending messages to the LLM.
    /// Takes precedence over <see cref="TransformContext" /> when set.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContextAsync { get; init; }

    /// <summary>
    /// Convert agent messages to LLM-visible messages (filter custom types).
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? ConvertToLlm { get; init; }

    /// <summary>
    /// Called after turn_end to replace state used by the next provider request.
    /// Mirrors pi-main prepareNextTurn.
    /// </summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurnAsync { get; init; }

    /// <summary>
    /// Called after prepare-next-turn and before steering/follow-up queues are polled.
    /// Mirrors pi-main shouldStopAfterTurn.
    /// </summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<bool>>? ShouldStopAfterTurnAsync { get; init; }
}
