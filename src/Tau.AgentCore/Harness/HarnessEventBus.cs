namespace Tau.AgentCore.Harness;

/// <summary>
/// Harness 运行开始事件。
/// </summary>
public sealed record HarnessRunStartEvent(string Lane, string RunId) { public string Type => "run_start"; }

/// <summary>
/// Harness 运行结束事件。
/// </summary>
public sealed record HarnessRunEndEvent(string Lane, string RunId, string Outcome, string LeafId) { public string Type => "run_end"; }

/// <summary>
/// 提供同步事件发布和可回放 watch 快照能力的 Harness 事件总线。
/// </summary>
public sealed class HarnessEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Action<object>>> _listeners = new(StringComparer.Ordinal);
    private readonly List<Action<object>> _watchers = [];

    /// <summary>
    /// 注册指定事件类型监听器，并返回取消订阅句柄。
    /// </summary>
    /// <typeparam name="TEvent">事件类型。</typeparam>
    /// <param name="listener">事件回调。</param>
    /// <returns>取消订阅句柄。</returns>
    public IDisposable On<TEvent>(Action<TEvent> listener) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(listener);
        var key = typeof(TEvent).FullName ?? typeof(TEvent).Name;
        Action<object> wrapper = value => { if (value is TEvent typed) listener(typed); };
        lock (_gate) (_listeners.TryGetValue(key, out var values) ? values : (_listeners[key] = [])).Add(wrapper);
        return new DelegateSubscription(() => { lock (_gate) if (_listeners.TryGetValue(key, out var values)) values.Remove(wrapper); });
    }

    /// <summary>
    /// 发布事件给当前监听器和 watch 缓冲区。
    /// </summary>
    /// <param name="eventValue">要发布的事件。</param>
    public void Emit(object eventValue)
    {
        ArgumentNullException.ThrowIfNull(eventValue);
        List<Action<object>> listeners;
        lock (_gate)
        {
            var key = eventValue.GetType().FullName ?? eventValue.GetType().Name;
            listeners = _listeners.TryGetValue(key, out var values) ? [.. values] : [];
            listeners.AddRange(_watchers.ToArray());
        }
        List<Exception>? failures = null;
        foreach (var listener in listeners)
        {
            try { listener(eventValue); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        if (failures is { Count: > 0 }) throw new AggregateException("One or more Harness listeners failed.", failures);
    }

    /// <summary>
    /// 创建携带当前快照的 watch；在 Start 前发布的事件会按顺序缓冲。
    /// </summary>
    /// <typeparam name="TSnapshot">快照类型。</typeparam>
    /// <param name="captureSnapshot">快照生成函数。</param>
    /// <returns>可启动和取消的 watch 句柄。</returns>
    public HarnessWatch<TSnapshot> Watch<TSnapshot>(Func<TSnapshot> captureSnapshot)
    {
        ArgumentNullException.ThrowIfNull(captureSnapshot);
        var buffered = new List<object>();
        var stateGate = new object();
        Action<object>? listener = null;
        var started = false;
        var unsubscribed = false;
        Action<object> receive = value =>
        {
            Action<object>? target;
            lock (stateGate)
            {
                if (unsubscribed) return;
                target = listener;
                if (target is null) { buffered.Add(value); return; }
            }
            InvokeSafely(target, value);
        };
        TSnapshot snapshot;
        lock (_gate)
        {
            _watchers.Add(receive);
            snapshot = captureSnapshot();
        }
        return new HarnessWatch<TSnapshot>(snapshot, next =>
        {
            List<object> pending;
            lock (stateGate)
            {
                if (unsubscribed) throw new ObjectDisposedException(nameof(HarnessWatch<TSnapshot>));
                if (started) throw new InvalidOperationException("Harness watch can only be started once.");
                started = true;
                listener = next;
                pending = [.. buffered];
                buffered.Clear();
            }
            foreach (var value in pending) InvokeSafely(next, value);
        }, () =>
        {
            lock (_gate) _watchers.Remove(receive);
            lock (stateGate) { unsubscribed = true; listener = null; buffered.Clear(); }
        });
    }

    private static void InvokeSafely(Action<object> listener, object value)
    {
        try { listener(value); }
        catch { /* 单个 watch listener 失败不能阻断总线后续事件 */ }
    }

    private sealed class DelegateSubscription(Action action) : IDisposable { public void Dispose() => action(); }
}

/// <summary>
/// Harness watch 的快照和事件启动句柄。
/// </summary>
public sealed class HarnessWatch<TSnapshot>
{
    private readonly Action<Action<object>> _start;
    private readonly Action _unsubscribe;
    internal HarnessWatch(TSnapshot snapshot, Action<Action<object>> start, Action unsubscribe) { Snapshot = snapshot; _start = start; _unsubscribe = unsubscribe; }
    /// <summary>创建 watch 时捕获的快照。</summary>
    public TSnapshot Snapshot { get; }
    /// <summary>启动事件消费。</summary>
    /// <param name="listener">事件回调。</param>
    public void Start(Action<object> listener) { ArgumentNullException.ThrowIfNull(listener); _start(listener); }
    /// <summary>取消 watch 并丢弃未消费事件。</summary>
    public void Unsubscribe() => _unsubscribe();
}
