namespace Tau.Ai;

public delegate void SessionResourceCleanup(string? sessionId = null);

public static class SessionResources
{
    private static readonly object SyncRoot = new();
    private static readonly List<SessionResourceCleanup> Cleanups = [];

    public static IDisposable RegisterSessionResourceCleanup(SessionResourceCleanup cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);

        lock (SyncRoot)
        {
            Cleanups.Add(cleanup);
        }

        return new Registration(cleanup);
    }

    public static void CleanupSessionResources(string? sessionId = null)
    {
        SessionResourceCleanup[] cleanups;
        lock (SyncRoot)
        {
            cleanups = [.. Cleanups];
        }

        List<Exception>? errors = null;
        foreach (var cleanup in cleanups)
        {
            try
            {
                cleanup(sessionId);
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add(ex);
            }
        }

        if (errors is { Count: > 0 })
        {
            throw new AggregateException("Failed to cleanup session resources.", errors);
        }
    }

    private sealed class Registration(SessionResourceCleanup cleanup) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (SyncRoot)
            {
                Cleanups.Remove(cleanup);
            }
        }
    }
}
