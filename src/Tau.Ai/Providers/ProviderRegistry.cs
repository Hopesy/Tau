using System.Collections.Concurrent;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

/// <summary>
/// Global provider registry with lazy initialization and source-based bulk unregistration.
/// Mirrors pi-main's api-registry.ts.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, ProviderEntry> _providers = new();
    private IReadOnlyDictionary<string, IStreamProvider> _modelProviders = new Dictionary<string, IStreamProvider>(StringComparer.OrdinalIgnoreCase);

    /// <summary>【AI】【提供方路由】优先使用模型所属提供方的扩展实现，保持同协议的其他提供方不变。</summary>
    /// <param name="model">请求模型。</param><returns>实际流式实现。</returns>
    public IStreamProvider Get(Model model) => Volatile.Read(ref _modelProviders).GetValueOrDefault(model.Provider) ?? Get(model.Api);

    /// <summary>【AI】【提供方快照】读取独立的提供方路由快照，供会话覆盖后恢复。</summary>
    /// <returns>只读实现索引。</returns>
    public IReadOnlyDictionary<string, IStreamProvider> GetModelProviders() => new Dictionary<string, IStreamProvider>(Volatile.Read(ref _modelProviders), StringComparer.OrdinalIgnoreCase);

    /// <summary>【AI】【提供方覆盖】替换会话级实现，空字典恢复协议注册表。</summary>
    /// <param name="providers">按提供方标识组织的实现。</param>
    public void SetModelProviders(IReadOnlyDictionary<string, IStreamProvider> providers) =>
        Volatile.Write(ref _modelProviders, new Dictionary<string, IStreamProvider>(providers, StringComparer.OrdinalIgnoreCase));

    /// <summary>【AI】【注册表隔离】复制注册状态，可为所有协议和提供方实现统一包装钩子。</summary>
    /// <param name="wrap">可选实现包装函数。</param><returns>独立注册表。</returns>
    public ProviderRegistry CreateSessionCopy(Func<IStreamProvider, IStreamProvider>? wrap = null)
    {
        var copy = new ProviderRegistry();
        foreach (var (api, entry) in _providers)
            copy._providers[api] = wrap is null ? entry : new(new Lazy<IStreamProvider>(() => wrap(entry.Provider.Value)), entry.SourceId);
        copy.SetModelProviders(Volatile.Read(ref _modelProviders).ToDictionary(pair => pair.Key, pair => wrap?.Invoke(pair.Value) ?? pair.Value, StringComparer.OrdinalIgnoreCase));
        return copy;
    }

    public void Register(string api, Func<IStreamProvider> factory, string? sourceId = null)
    {
        api = ModelApiNames.Normalize(api) ?? api;
        var entry = new ProviderEntry(new Lazy<IStreamProvider>(factory), sourceId);
        _providers.AddOrUpdate(api, entry, (_, _) => entry);
    }

    public void Register(string api, IStreamProvider provider, string? sourceId = null)
    {
        api = ModelApiNames.Normalize(api) ?? api;
        var entry = new ProviderEntry(new Lazy<IStreamProvider>(provider), sourceId);
        _providers.AddOrUpdate(api, entry, (_, _) => entry);
    }

    public IStreamProvider Get(string api)
    {
        api = ModelApiNames.Normalize(api) ?? api;
        if (_providers.TryGetValue(api, out var entry))
            return entry.Provider.Value;

        throw new KeyNotFoundException($"No provider registered for API '{api}'.");
    }

    public IStreamProvider? TryGet(string api)
    {
        api = ModelApiNames.Normalize(api) ?? api;
        return _providers.TryGetValue(api, out var entry) ? entry.Provider.Value : null;
    }

    public IReadOnlyList<string> RegisteredApis => [.. _providers.Keys];

    public void Unregister(string api)
    {
        api = ModelApiNames.Normalize(api) ?? api;
        _providers.TryRemove(api, out _);
    }

    public void UnregisterBySource(string sourceId)
    {
        var toRemove = _providers
            .Where(kv => kv.Value.SourceId == sourceId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            _providers.TryRemove(key, out _);
    }

    public void Clear()
    {
        _providers.Clear();
        SetModelProviders(new Dictionary<string, IStreamProvider>());
    }

    private sealed record ProviderEntry(Lazy<IStreamProvider> Provider, string? SourceId);
}
