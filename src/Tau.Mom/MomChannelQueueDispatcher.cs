using System.Collections.Concurrent;

namespace Tau.Mom;

public enum MomChannelDispatchStatus
{
    Enqueued,
    StopDispatched,
    QueueFull
}

public sealed record MomChannelDispatchResult(MomChannelDispatchStatus Status, Task<bool> Completion)
{
    public bool Accepted => Status is not MomChannelDispatchStatus.QueueFull;
}

public sealed class MomChannelQueueDispatcher
{
    private readonly ConcurrentDictionary<string, ChannelQueue> _queues = new(StringComparer.Ordinal);
    private readonly MomChannelMessageProcessor _processor;
    private readonly MomOptions _options;
    private readonly ILogger<MomChannelQueueDispatcher> _logger;

    public MomChannelQueueDispatcher(
        MomChannelMessageProcessor processor,
        MomOptions options,
        ILogger<MomChannelQueueDispatcher> logger)
    {
        _processor = processor;
        _options = options;
        _logger = logger;
    }

    public MomChannelDispatchResult Dispatch(
        MomChannelMessage message,
        IMomChannelResponder responder,
        CancellationToken cancellationToken = default)
    {
        if (MomChannelCommands.IsStopCommand(message.Text))
        {
            return new MomChannelDispatchResult(
                MomChannelDispatchStatus.StopDispatched,
                RunProcessorAsync(message, responder, cancellationToken));
        }

        var queue = _queues.GetOrAdd(
            NormalizeChannelKey(message.ChannelId),
            static (_, state) => new ChannelQueue(
                Math.Max(1, state.Options.SlackChannelQueueLimit),
                state.Processor,
                state.Logger),
            (Options: _options, Processor: _processor, Logger: _logger));

        if (!queue.TryEnqueue(message, responder, cancellationToken, out var completion))
        {
            return new MomChannelDispatchResult(
                MomChannelDispatchStatus.QueueFull,
                RespondQueueFullAsync(message, responder, cancellationToken));
        }

        return new MomChannelDispatchResult(MomChannelDispatchStatus.Enqueued, completion);
    }

    private async Task<bool> RespondQueueFullAsync(
        MomChannelMessage message,
        IMomChannelResponder responder,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogWarning(
                "Slack channel queue full for {ChannelId}; discarding message {Ts}.",
                message.ChannelId,
                message.Ts);
            await responder.RespondAsync(
                    message,
                    "_Queue full. Try again after the current work finishes._",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to respond to full Slack channel queue for {ChannelId}/{Ts}.",
                message.ChannelId,
                message.Ts);
        }

        return false;
    }

    private async Task<bool> RunProcessorAsync(
        MomChannelMessage message,
        IMomChannelResponder responder,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _processor.ProcessAsync(message, responder, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Slack channel processing failed for {ChannelId}/{Ts}.",
                message.ChannelId,
                message.Ts);
            return false;
        }
    }

    private static string NormalizeChannelKey(string? channelId)
    {
        return string.IsNullOrWhiteSpace(channelId) ? "channel" : channelId.Trim();
    }

    private sealed class ChannelQueue
    {
        private readonly object _gate = new();
        private readonly Queue<QueuedWork> _queue = [];
        private readonly int _queueLimit;
        private readonly MomChannelMessageProcessor _processor;
        private readonly ILogger _logger;
        private bool _processing;

        public ChannelQueue(
            int queueLimit,
            MomChannelMessageProcessor processor,
            ILogger logger)
        {
            _queueLimit = queueLimit;
            _processor = processor;
            _logger = logger;
        }

        public bool TryEnqueue(
            MomChannelMessage message,
            IMomChannelResponder responder,
            CancellationToken cancellationToken,
            out Task<bool> completion)
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_queue.Count >= _queueLimit)
                {
                    completion = Task.FromResult(false);
                    return false;
                }

                _queue.Enqueue(new QueuedWork(message, responder, cancellationToken, source));
                if (!_processing)
                {
                    _processing = true;
                    _ = ProcessLoopAsync();
                }
            }

            completion = source.Task;
            return true;
        }

        private async Task ProcessLoopAsync()
        {
            while (true)
            {
                QueuedWork work;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _processing = false;
                        return;
                    }

                    work = _queue.Dequeue();
                }

                try
                {
                    var processed = await _processor
                        .ProcessAsync(work.Message, work.Responder, work.CancellationToken)
                        .ConfigureAwait(false);
                    work.Completion.TrySetResult(processed);
                }
                catch (OperationCanceledException ex)
                {
                    work.Completion.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Slack channel queue error for {ChannelId}/{Ts}.",
                        work.Message.ChannelId,
                        work.Message.Ts);
                    work.Completion.TrySetResult(false);
                }
            }
        }
    }

    private sealed record QueuedWork(
        MomChannelMessage Message,
        IMomChannelResponder Responder,
        CancellationToken CancellationToken,
        TaskCompletionSource<bool> Completion);
}
