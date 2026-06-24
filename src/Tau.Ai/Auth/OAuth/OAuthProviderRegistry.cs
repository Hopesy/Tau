namespace Tau.Ai.Auth.OAuth;

public sealed class OAuthProviderRegistry
{
    private readonly Dictionary<string, IOAuthProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IOAuthProvider> _builtIns = new(StringComparer.OrdinalIgnoreCase);

    public OAuthProviderRegistry(IEnumerable<IOAuthProvider>? providers = null)
    {
        if (providers is null)
        {
            providers = BuiltInOAuthProviders.GetAll();
        }

        foreach (var provider in providers)
        {
            _builtIns[provider.Id] = provider;
            Register(provider);
        }
    }

    public IReadOnlyCollection<IOAuthProvider> Providers => _providers.Values.ToArray();

    public IReadOnlyList<OAuthProviderInfo> GetProviderInfoList() =>
        _providers.Values
            .Select(provider => new OAuthProviderInfo(provider.Id, provider.Name, true, provider.UsesCallbackServer))
            .ToArray();

    public void Register(IOAuthProvider provider)
    {
        _providers[provider.Id] = provider;
    }

    public bool Unregister(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (_builtIns.TryGetValue(id, out var builtIn))
        {
            _providers[id] = builtIn;
            return true;
        }

        return _providers.Remove(id);
    }

    public void Reset()
    {
        _providers.Clear();
        foreach (var (id, provider) in _builtIns)
        {
            _providers[id] = provider;
        }
    }

    public IOAuthProvider? TryGet(string id) =>
        _providers.TryGetValue(id, out var provider) ? provider : null;

    public IOAuthProvider Get(string id) =>
        TryGet(id) ?? throw new KeyNotFoundException($"Unknown OAuth provider '{id}'.");
}
