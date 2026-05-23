namespace Tau.Mom;

public sealed class MomLocalDelegationFlow
{
    private readonly MomEventProcessor _eventProcessor;
    private readonly FileDelegationProcessor _fileProcessor;

    public MomLocalDelegationFlow(MomEventProcessor eventProcessor, FileDelegationProcessor fileProcessor)
    {
        _eventProcessor = eventProcessor;
        _fileProcessor = fileProcessor;
    }

    public async Task<MomLocalDelegationFlowResult> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        return await ProcessOnceAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MomLocalDelegationFlowResult> ProcessOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var queuedEvents = await _eventProcessor.ProcessDueEventsAsync(now, cancellationToken).ConfigureAwait(false);
        var processedRequests = await _fileProcessor.ProcessPendingAsync(cancellationToken).ConfigureAwait(false);
        return new MomLocalDelegationFlowResult(queuedEvents, processedRequests);
    }
}

public readonly record struct MomLocalDelegationFlowResult(int QueuedEvents, int ProcessedRequests);
