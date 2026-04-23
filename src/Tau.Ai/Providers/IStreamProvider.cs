using Tau.Ai.Streaming;

namespace Tau.Ai.Providers;

/// <summary>
/// Each LLM provider implements this interface.
/// Stream functions must not throw — errors are delivered as stream events.
/// </summary>
public interface IStreamProvider
{
    string Api { get; }

    AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options);

    AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options);
}
