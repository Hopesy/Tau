using System.Collections.Concurrent;

namespace Tau.Mom;

public sealed class MomChannelRunRegistry
{
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new(StringComparer.Ordinal);

    public MomChannelRunHandle? TryStart(
        string channelId,
        string requestId,
        string workingDirectory,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var key = NormalizeChannelKey(channelId);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = new ActiveRun(key, requestId, workingDirectory, startedAt, source);
        if (!_activeRuns.TryAdd(key, run))
        {
            source.Dispose();
            return null;
        }

        return new MomChannelRunHandle(this, run);
    }

    public MomChannelStopResult RequestStop(string channelId)
    {
        var key = NormalizeChannelKey(channelId);
        if (!_activeRuns.TryGetValue(key, out var run))
        {
            return MomChannelStopResult.NotRunning;
        }

        run.RequestStop();
        return new MomChannelStopResult(true, run.RequestId, run.WorkingDirectory, run.StartedAt);
    }

    private void Complete(ActiveRun run)
    {
        if (_activeRuns.TryGetValue(run.ChannelKey, out var current) && ReferenceEquals(current, run))
        {
            _activeRuns.TryRemove(new KeyValuePair<string, ActiveRun>(run.ChannelKey, run));
        }

        run.Dispose();
    }

    private static string NormalizeChannelKey(string? channelId)
    {
        return MomChannelWorkspace.MakeSafePathSegment(channelId);
    }

    internal sealed class ActiveRun : IDisposable
    {
        private int _stopRequested;

        public ActiveRun(
            string channelKey,
            string requestId,
            string workingDirectory,
            DateTimeOffset startedAt,
            CancellationTokenSource cancellationTokenSource)
        {
            ChannelKey = channelKey;
            RequestId = requestId;
            WorkingDirectory = workingDirectory;
            StartedAt = startedAt;
            CancellationTokenSource = cancellationTokenSource;
        }

        public string ChannelKey { get; }
        public string RequestId { get; }
        public string WorkingDirectory { get; }
        public DateTimeOffset StartedAt { get; }
        public CancellationTokenSource CancellationTokenSource { get; }
        public CancellationToken Token => CancellationTokenSource.Token;
        public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
            {
                CancellationTokenSource.Cancel();
            }
        }

        public void Dispose()
        {
            CancellationTokenSource.Dispose();
        }
    }

    public sealed class MomChannelRunHandle : IDisposable
    {
        private readonly MomChannelRunRegistry _owner;
        private readonly ActiveRun _run;
        private bool _disposed;

        internal MomChannelRunHandle(MomChannelRunRegistry owner, ActiveRun run)
        {
            _owner = owner;
            _run = run;
        }

        public CancellationToken Token => _run.Token;
        public bool StopRequested => _run.StopRequested;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Complete(_run);
        }
    }
}

public sealed record MomChannelStopResult(
    bool Accepted,
    string? RequestId = null,
    string? WorkingDirectory = null,
    DateTimeOffset? StartedAt = null)
{
    public static MomChannelStopResult NotRunning { get; } = new(false);
}
