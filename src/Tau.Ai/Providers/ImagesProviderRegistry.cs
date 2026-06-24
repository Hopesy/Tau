using System.Collections.Concurrent;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers;

public sealed class ImagesProviderRegistry
{
    private readonly ConcurrentDictionary<string, ImagesProviderEntry> _providers = new();

    public void Register(string api, Func<IImagesProvider> factory, string? sourceId = null)
    {
        api = ModelApiNames.Normalize(api) ?? api;
        var entry = new ImagesProviderEntry(new Lazy<IImagesProvider>(factory), sourceId);
        _providers.AddOrUpdate(api, entry, (_, _) => entry);
    }

    public void Register(string api, IImagesProvider provider, string? sourceId = null)
    {
        api = ModelApiNames.Normalize(api) ?? api;
        var entry = new ImagesProviderEntry(new Lazy<IImagesProvider>(provider), sourceId);
        _providers.AddOrUpdate(api, entry, (_, _) => entry);
    }

    public IImagesProvider Get(string api)
    {
        api = ModelApiNames.Normalize(api) ?? api;
        if (_providers.TryGetValue(api, out var entry))
        {
            return entry.Provider.Value;
        }

        throw new KeyNotFoundException($"No image provider registered for API '{api}'.");
    }

    public IImagesProvider? TryGet(string api)
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
        {
            _providers.TryRemove(key, out _);
        }
    }

    public void Clear() => _providers.Clear();

    private sealed record ImagesProviderEntry(Lazy<IImagesProvider> Provider, string? SourceId);
}
