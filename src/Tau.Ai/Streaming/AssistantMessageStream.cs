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
            ErrorEvent err => err.Message ?? new AssistantMessage
            {
                ErrorMessage = err.Error,
                Content = err.Partial?.Content ?? [],
                StopReason = err.Partial?.StopReason ?? StopReason.Error,
                Usage = err.Partial?.Usage,
                Api = err.Partial?.Api,
                Provider = err.Partial?.Provider,
                Model = err.Partial?.Model,
                ResponseId = err.Partial?.ResponseId,
                ResponseModel = err.Partial?.ResponseModel,
                RawStopReason = err.Partial?.RawStopReason,
                EndTurn = err.Partial?.EndTurn,
                Deferred = err.Partial?.Deferred,
                Timestamp = err.Partial?.Timestamp
            },
            _ => null
        })
    {
    }
}
