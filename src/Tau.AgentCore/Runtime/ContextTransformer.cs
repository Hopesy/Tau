using Tau.Ai;

namespace Tau.AgentCore.Runtime;

/// <summary>
/// Applies context transformations before sending messages to the LLM.
/// </summary>
internal static class ContextTransformer
{
    /// <summary>【AgentCore】【请求上下文】转换消息并保留系统声明，由实际协议决定传输方式。</summary>
    /// <param name="config">本次运行配置。</param>
    /// <param name="messages">当前会话。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>保留系统消息位置且不重复声明工具的请求上下文。</returns>
    public static async ValueTask<LlmContext> BuildAsync(
        AgentLoopConfig config,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transformed = config.TransformContextAsync is not null
            ? await config.TransformContextAsync(messages, cancellationToken).ConfigureAwait(false)
            : config.TransformContext?.Invoke(messages) ?? messages;
        cancellationToken.ThrowIfCancellationRequested();

        var llmMessages = config.ConvertToLlm?.Invoke(transformed) ?? transformed;
        cancellationToken.ThrowIfCancellationRequested();

        return new LlmContext(config.SystemPrompt, llmMessages, []);
    }
}
