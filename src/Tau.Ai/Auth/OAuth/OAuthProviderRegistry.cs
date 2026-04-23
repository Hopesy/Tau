namespace Tau.Ai.Auth.OAuth;

public sealed class OAuthProviderRegistry
{
    private readonly Dictionary<string, IOAuthProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public OAuthProviderRegistry(IEnumerable<IOAuthProvider>? providers = null)
    {
        if (providers is null)
        {
            providers = BuiltInOAuthProviders.GetAll();
        }

        foreach (var provider in providers)
        {
            Register(provider);
        }
    }

    public IReadOnlyCollection<IOAuthProvider> Providers => _providers.Values.ToArray();

    public void Register(IOAuthProvider provider)
    {
        _providers[provider.Id] = provider;
    }

    public IOAuthProvider? TryGet(string id) =>
        _providers.TryGetValue(id, out var provider) ? provider : null;

    public IOAuthProvider Get(string id) =>
        TryGet(id) ?? throw new KeyNotFoundException($"Unknown OAuth provider '{id}'.");
}
