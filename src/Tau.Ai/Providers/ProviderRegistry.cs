using System.Collections.Concurrent;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

/// <summary>
/// Global provider registry with lazy initialization and source-based bulk unregistration.
/// Mirrors pi-mono's api-registry.ts.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, ProviderEntry> _providers = new();

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

    public void Clear() => _providers.Clear();

    private sealed record ProviderEntry(Lazy<IStreamProvider> Provider, string? SourceId);
}
