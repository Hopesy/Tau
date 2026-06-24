using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Tau.Ai.Streaming;

/// <summary>
/// Push-pull bridge: producers Push events, consumers iterate via IAsyncEnumerable.
/// Mirrors pi-main's EventStream with Channel-based buffering.
/// </summary>
public class EventStream<TEvent, TResult> : IAsyncEnumerable<TEvent>
{
    private readonly Channel<TEvent> _channel = Channel.CreateUnbounded<TEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly TaskCompletionSource<TResult> _resultTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<TEvent, bool> _isComplete;
    private readonly Func<TEvent, TResult?> _extractResult;

    public EventStream(Func<TEvent, bool> isComplete, Func<TEvent, TResult?> extractResult)
    {
        _isComplete = isComplete;
        _extractResult = extractResult;
    }

    public Task<TResult> ResultAsync => _resultTcs.Task;

    public void Push(TEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
            return;

        if (_isComplete(evt))
        {
            _channel.Writer.Complete();
            var result = _extractResult(evt);
            if (result is not null)
                _resultTcs.TrySetResult(result);
            else
                _resultTcs.TrySetException(new InvalidOperationException("Stream completed without a result."));
        }
    }

    public void End(TResult result)
    {
        _channel.Writer.TryComplete();
        _resultTcs.TrySetResult(result);
    }

    public void Fault(Exception ex)
    {
        _channel.Writer.TryComplete(ex);
        _resultTcs.TrySetException(ex);
    }

    public IAsyncEnumerator<TEvent> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }
}
