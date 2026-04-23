using Tau.Ai;

namespace Tau.Agent.Runtime;

/// <summary>
/// Applies context transformations before sending messages to the LLM.
/// </summary>
internal static class ContextTransformer
{
    public static LlmContext Build(
        AgentLoopConfig config,
        IReadOnlyList<ChatMessage> messages)
    {
        var transformed = config.TransformContext?.Invoke(messages) ?? messages;
        var llmMessages = config.ConvertToLlm?.Invoke(transformed) ?? transformed;

        var tools = config.Tools
            .Select(t => new Tool(t.Name, t.Description, t.ParameterSchema))
            .ToList();

        return new LlmContext(config.SystemPrompt, llmMessages, tools);
    }
}
