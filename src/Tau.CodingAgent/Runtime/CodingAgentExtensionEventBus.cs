namespace Tau.CodingAgent.Runtime;

public interface ICodingAgentExtensionEventBus
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler);

    IDisposable Subscribe(string eventType, Func<CodingAgentExtensionEvent, CancellationToken, ValueTask> handler);

    Task<CodingAgentExtensionEventPublishResult> PublishAsync<TEvent>(
        TEvent eventRecord,
        CancellationToken cancellationToken = default);

    Task<CodingAgentExtensionEventPublishResult> PublishAsync(
        string eventType,
        object? payload,
        CancellationToken cancellationToken = default);
}

public sealed record CodingAgentExtensionEvent(
    string Type,
    object? Payload,
    DateTimeOffset Timestamp);

public sealed record CodingAgentExtensionEventHandlerResult(
    long SubscriptionId,
    bool Succeeded,
    Exception? Exception)
{
    public static CodingAgentExtensionEventHandlerResult Success(long subscriptionId) =>
        new(subscriptionId, true, null);

    public static CodingAgentExtensionEventHandlerResult Failure(long subscriptionId, Exception exception) =>
        new(subscriptionId, false, exception);
}

public sealed record CodingAgentExtensionEventPublishResult(
    string EventType,
    int HandlerCount,
    IReadOnlyList<CodingAgentExtensionEventHandlerResult> HandlerResults)
{
    public bool Succeeded => HandlerResults.All(static result => result.Succeeded);

    public IReadOnlyList<Exception> Exceptions =>
        HandlerResults
            .Where(static result => result.Exception is not null)
            .Select(static result => result.Exception!)
            .ToArray();
}

public sealed class CodingAgentExtensionEventBus : ICodingAgentExtensionEventBus
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private long _nextSubscriptionId;

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        return Subscribe(
            GetEventType<TEvent>(),
            async (extensionEvent, cancellationToken) =>
            {
                if (extensionEvent.Payload is not TEvent typedEvent)
                {
                    throw new InvalidOperationException(
                        $"Event payload for '{extensionEvent.Type}' is not assignable to '{typeof(TEvent).FullName}'.");
                }

                await handler(typedEvent, cancellationToken).ConfigureAwait(false);
            });
    }

    public IDisposable Subscribe(
        string eventType,
        Func<CodingAgentExtensionEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(
            Interlocked.Increment(ref _nextSubscriptionId),
            eventType,
            handler,
            this);

        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public Task<CodingAgentExtensionEventPublishResult> PublishAsync<TEvent>(
        TEvent eventRecord,
        CancellationToken cancellationToken = default) =>
        PublishAsync(GetEventType<TEvent>(), eventRecord, cancellationToken);

    public async Task<CodingAgentExtensionEventPublishResult> PublishAsync(
        string eventType,
        object? payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        var extensionEvent = new CodingAgentExtensionEvent(eventType, payload, DateTimeOffset.UtcNow);
        var matchingSubscriptions = GetMatchingSubscriptions(eventType);
        var results = new List<CodingAgentExtensionEventHandlerResult>(matchingSubscriptions.Length);

        foreach (var subscription in matchingSubscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await subscription.Handler(extensionEvent, cancellationToken).ConfigureAwait(false);
                results.Add(CodingAgentExtensionEventHandlerResult.Success(subscription.Id));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(CodingAgentExtensionEventHandlerResult.Failure(subscription.Id, ex));
            }
        }

        return new CodingAgentExtensionEventPublishResult(eventType, matchingSubscriptions.Length, results);
    }

    private static string GetEventType<TEvent>() =>
        typeof(TEvent).FullName ?? typeof(TEvent).Name;

    private Subscription[] GetMatchingSubscriptions(string eventType)
    {
        lock (_gate)
        {
            return _subscriptions
                .Where(subscription => subscription.EventType.Equals(eventType, StringComparison.Ordinal))
                .OrderBy(static subscription => subscription.Id)
                .ToArray();
        }
    }

    private void Unsubscribe(long id)
    {
        lock (_gate)
        {
            _subscriptions.RemoveAll(subscription => subscription.Id == id);
        }
    }

    private sealed record Subscription(
        long Id,
        string EventType,
        Func<CodingAgentExtensionEvent, CancellationToken, ValueTask> Handler,
        CodingAgentExtensionEventBus Owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Owner.Unsubscribe(Id);
            }
        }
    }
}
