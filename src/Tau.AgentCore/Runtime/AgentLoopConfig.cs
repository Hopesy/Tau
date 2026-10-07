using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Registry;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Runtime;

/// <summary>【AgentCore】【回合完成】已完成回合的消息、工具结果及上下文快照</summary>
/// <param name="Message">已完成的助手消息</param>
/// <param name="ToolResults">本回合最终工具结果</param>
/// <param name="Context">当前完整会话消息</param>
/// <param name="NewMessages">本次运行新增的消息；不包含运行前的历史</param>
public sealed record AgentLoopTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    IReadOnlyList<ChatMessage> Context,
    IReadOnlyList<ChatMessage> NewMessages)
{
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<IAgentTool> Tools { get; init; } = [];
}

/// <summary>【AgentCore】【回合调度】正常回合结束后的显式调度决策</summary>
public enum AgentTurnDecision
{
    /// <summary>保证下一次请求；工具或队列引发的下一请求可满足此决策</summary>
    Continue,
    /// <summary>发布 turn_end 后立即结束，保留所有尚未选择的队列消息</summary>
    End
}

/// <summary>【AgentCore】【请求准备】队列消息发布后、上下文转换前的运行状态</summary>
/// <param name="Context">当前完整会话消息</param>
/// <param name="Model">当前请求使用的模型</param>
/// <param name="ThinkingLevel">当前推理等级；未配置时为 Off</param>
/// <param name="SystemPrompt">当前系统提示词</param>
/// <param name="Tools">当前可执行工具集合</param>
public sealed record AgentPrepareRequestContext(
    IReadOnlyList<ChatMessage> Context,
    Model Model,
    ThinkingLevel ThinkingLevel,
    string? SystemPrompt,
    IReadOnlyList<IAgentTool> Tools);

/// <summary>【AgentCore】【请求准备】替换当前请求状态；空属性保留原配置</summary>
/// <param name="Context">替换的完整会话消息，不额外发布消息事件</param>
/// <param name="Model">替换模型</param>
/// <param name="SystemPrompt">替换系统提示词</param>
/// <param name="Tools">替换可执行工具集合</param>
/// <param name="StreamOptions">替换流式请求选项</param>
/// <param name="Reasoning">替换推理等级；Off 清除推理配置</param>
/// <param name="ClearReasoning">是否清除推理配置；Reasoning 显式值优先</param>
public record AgentRequestUpdate(
    IReadOnlyList<ChatMessage>? Context = null,
    Model? Model = null,
    string? SystemPrompt = null,
    IReadOnlyList<IAgentTool>? Tools = null,
    SimpleStreamOptions? StreamOptions = null,
    ThinkingLevel? Reasoning = null,
    bool ClearReasoning = false)
{
    /// <summary>【AgentCore】【请求路由】只为当前请求替换模型，保留 Agent 状态中的用户选择。</summary>
    public bool PreserveModelSelection { get; init; }
}

/// <summary>【AgentCore】【回合准备】替换下一回合状态，并可追加需要发布事件的新消息</summary>
/// <param name="Context">替换的完整会话消息</param>
/// <param name="Model">替换模型</param>
/// <param name="SystemPrompt">替换系统提示词</param>
/// <param name="Tools">替换可执行工具集合</param>
/// <param name="StreamOptions">替换流式请求选项</param>
/// <param name="Reasoning">替换推理等级</param>
/// <param name="ClearReasoning">是否清除推理配置</param>
public sealed record AgentLoopTurnUpdate(
    IReadOnlyList<ChatMessage>? Context = null,
    Model? Model = null,
    string? SystemPrompt = null,
    IReadOnlyList<IAgentTool>? Tools = null,
    SimpleStreamOptions? StreamOptions = null,
    ThinkingLevel? Reasoning = null,
    bool ClearReasoning = false)
    : AgentRequestUpdate(Context, Model, SystemPrompt, Tools, StreamOptions, Reasoning, ClearReasoning)
{
    /// <summary>下一回合在队列消息之前追加并发布的消息</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
}

/// <summary>
/// Configuration for a single agent run.
/// </summary>
public record AgentLoopConfig
{
    /// <summary>从已初始化的会话读取提示，并将钩子的提示替换写回历史；启用时 SystemPrompt 应为空。</summary>
    public bool SystemPromptFromTranscript { get; init; }
    public required Model Model { get; init; }
    public ProviderRegistry ProviderRegistry { get; init; } = new();
    /// <summary>【AgentCore】【流式函数】可同步或异步启动模型响应流；为空时使用默认函数或提供方注册表</summary>
    public AgentStreamFunction? StreamFunction { get; init; }
    /// <summary>【AgentCore】【请求配置】会话独立的模型配置；为空时沿用默认来源。</summary>
    public ModelConfigurationStore? ConfigurationStore { get; init; }
    /// <summary>【AgentCore】【请求认证】会话独立的认证解析器；为空时沿用默认来源。</summary>
    public ProviderAuthResolver? AuthResolver { get; init; }
    public required IReadOnlyList<IAgentTool> Tools { get; init; }
    public IReadOnlyList<IToolInterceptor> Interceptors { get; init; } = [];
    public ITauLogSink LogSink { get; init; } = NullTauLogSink.Instance;
    public TauRuntimeLogContext? LogContext { get; init; }
    public string? SystemPrompt { get; init; }
    public SimpleStreamOptions? StreamOptions { get; init; }
    public Func<string, CancellationToken, Task<string?>>? GetApiKeyAsync { get; init; }
    public ToolExecutionMode DefaultExecutionMode { get; init; } = ToolExecutionMode.Parallel;
    public bool SkipInitialSteeringPoll { get; init; }
    /// <summary>本次运行的新输入，由运行时发布事件并追加到状态；AddMessage 添加的消息属于既有历史。</summary>
    public IReadOnlyList<ChatMessage> InitialMessages { get; init; } = [];

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
    /// 【AgentCore】【回合准备】仅在确定继续运行时调用，替换下一回合状态或追加消息
    /// </summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurnAsync { get; init; }

    /// <summary>【AgentCore】【请求准备】每次模型请求前调用，包括首次；执行后不额外轮询队列</summary>
    public Func<AgentPrepareRequestContext, CancellationToken, Task<AgentRequestUpdate?>>? PrepareRequestAsync { get; init; }

    /// <summary>【AgentCore】【回合完成】最终消息发布后、turn_end 前调用；空决策保留正常调度，错误和中止忽略决策</summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<AgentTurnDecision?>>? FinishTurnAsync { get; init; }

    /// <summary>
    /// 兼容旧停止接口：正常回合的 turn_end 后、队列轮询前调用
    /// 设置 FinishTurnAsync 时此接口不再执行；新代码应返回 AgentTurnDecision.End
    /// </summary>
    public Func<AgentLoopTurnContext, CancellationToken, Task<bool>>? ShouldStopAfterTurnAsync { get; init; }

    /// <summary>
    /// 消息结束时的转换钩子；用于在 message_end 事件发布后、消息写入会话状态前替换消息内容。
    /// 镜像上游扩展 message_end handler 返回替换消息的语义。
    /// </summary>
    public Func<ChatMessage, CancellationToken, Task<ChatMessage>>? MessageEndTransformAsync { get; init; }
}
