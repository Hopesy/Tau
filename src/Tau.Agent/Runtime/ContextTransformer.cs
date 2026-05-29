using Tau.Ai;

namespace Tau.Agent.Runtime;

/// <summary>
/// Applies context transformations before sending messages to the LLM.
/// </summary>
internal static class ContextTransformer
{
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

        var tools = config.Tools
            .Select(t => new Tool(t.Name, t.Description, t.ParameterSchema))
            .ToList();

        return new LlmContext(config.SystemPrompt, llmMessages, tools);
    }
}
