namespace Tau.Ai.Streaming;

/// <summary>
/// Typed event stream for LLM assistant responses.
/// Terminates on DoneEvent or ErrorEvent.
/// </summary>
public sealed class AssistantMessageStream : EventStream<StreamEvent, AssistantMessage>
{
    public AssistantMessageStream() : base(
        isComplete: static evt => evt is DoneEvent or ErrorEvent,
        extractResult: static evt => evt switch
        {
            DoneEvent done => done.Message,
            ErrorEvent err => new AssistantMessage
            {
                ErrorMessage = err.Error,
                Content = err.Partial?.Content ?? []
            },
            _ => null
        })
    {
    }
}
