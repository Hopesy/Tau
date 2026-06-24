using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Tests;

public sealed class OAuthProviderRegistryTests
{
    [Fact]
    public void Constructor_RegistersBuiltInProvidersByDefault()
    {
        var registry = new OAuthProviderRegistry();

        var ids = registry.Providers.Select(static provider => provider.Id).ToArray();

        Assert.Contains("anthropic", ids);
        Assert.Contains("github-copilot", ids);
        Assert.Contains("google-gemini-cli", ids);
        Assert.Contains("google-antigravity", ids);
        Assert.Contains("openai-codex", ids);
    }

    [Fact]
    public void Register_AddsCustomProviderAndGetProviderInfoListIncludesIt()
    {
        var registry = new OAuthProviderRegistry([]);
        var provider = new StubOAuthProvider("custom-oauth", "Custom OAuth");

        registry.Register(provider);

        var info = Assert.Single(registry.GetProviderInfoList());
        Assert.Equal("custom-oauth", info.Id);
        Assert.Equal("Custom OAuth", info.Name);
        Assert.True(info.Available);
        Assert.False(info.UsesCallbackServer);
        Assert.Same(provider, registry.Get("custom-oauth"));
    }

    [Fact]
    public void GetProviderInfoList_IncludesCallbackServerMetadata()
    {
        var registry = new OAuthProviderRegistry([]);
        registry.Register(new StubOAuthProvider("callback-oauth", "Callback OAuth") { UsesCallbackServer = true });
        registry.Register(new StubOAuthProvider("device-oauth", "Device OAuth"));

        var providers = registry.GetProviderInfoList().OrderBy(static info => info.Id, StringComparer.Ordinal).ToArray();

        Assert.Collection(
            providers,
            info =>
            {
                Assert.Equal("callback-oauth", info.Id);
                Assert.True(info.UsesCallbackServer);
            },
            info =>
            {
                Assert.Equal("device-oauth", info.Id);
                Assert.False(info.UsesCallbackServer);
            });
    }

    [Fact]
    public void Unregister_RemovesCustomProvider()
    {
        var registry = new OAuthProviderRegistry([]);
        registry.Register(new StubOAuthProvider("custom-oauth", "Custom OAuth"));

        var removed = registry.Unregister("custom-oauth");

        Assert.True(removed);
        Assert.Null(registry.TryGet("custom-oauth"));
        Assert.Empty(registry.Providers);
    }

    [Fact]
    public void Unregister_RestoresBuiltInProviderInsteadOfDeletingIt()
    {
        var builtIn = new StubOAuthProvider("anthropic", "Built In Anthropic");
        var overrideProvider = new StubOAuthProvider("anthropic", "Override Anthropic");
        var registry = new OAuthProviderRegistry([builtIn]);
        registry.Register(overrideProvider);

        var removed = registry.Unregister("anthropic");

        Assert.True(removed);
        Assert.Same(builtIn, registry.Get("anthropic"));
    }

    [Fact]
    public void Reset_RestoresBuiltInsAndDropsCustomProviders()
    {
        var builtIn = new StubOAuthProvider("anthropic", "Built In Anthropic");
        var registry = new OAuthProviderRegistry([builtIn]);
        registry.Register(new StubOAuthProvider("custom-oauth", "Custom OAuth"));
        registry.Register(new StubOAuthProvider("anthropic", "Override Anthropic"));

        registry.Reset();

        Assert.Single(registry.Providers);
        Assert.Same(builtIn, registry.Get("anthropic"));
        Assert.Null(registry.TryGet("custom-oauth"));
    }

    [Fact]
    public void Get_ThrowsForUnknownProvider()
    {
        var registry = new OAuthProviderRegistry([]);

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Get("missing-oauth"));

        Assert.Contains("missing-oauth", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOAuthApiKeyAsync_ReturnsNullWhenCredentialIsMissing()
    {
        var registry = new OAuthProviderRegistry([new StubOAuthProvider("custom-oauth", "Custom OAuth")]);

        var result = await OAuthHelpers.GetOAuthApiKeyAsync(
            registry,
            "custom-oauth",
            new Dictionary<string, OAuthCredentials>(StringComparer.OrdinalIgnoreCase));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOAuthApiKeyAsync_RefreshesExpiredCredentialAndReturnsApiKey()
    {
        var provider = new StubOAuthProvider("custom-oauth", "Custom OAuth")
        {
            RefreshedCredentials = new OAuthCredentials
            {
                Refresh = "refresh-token",
                Access = "refreshed-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            }
        };
        var registry = new OAuthProviderRegistry([provider]);
        var credentials = new Dictionary<string, OAuthCredentials>(StringComparer.OrdinalIgnoreCase)
        {
            ["custom-oauth"] = new OAuthCredentials
            {
                Refresh = "refresh-token",
                Access = "expired-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
            }
        };

        var result = await OAuthHelpers.GetOAuthApiKeyAsync(registry, "custom-oauth", credentials);

        Assert.NotNull(result);
        Assert.Equal("refreshed-access-token", result.NewCredentials.Access);
        Assert.Equal("api:refreshed-access-token", result.ApiKey);
        Assert.Equal(1, provider.RefreshCalls);
    }

    [Fact]
    public async Task GetOAuthApiKeyAsync_UsesExistingUnexpiredCredential()
    {
        var provider = new StubOAuthProvider("custom-oauth", "Custom OAuth");
        var registry = new OAuthProviderRegistry([provider]);
        var credentials = new Dictionary<string, OAuthCredentials>(StringComparer.OrdinalIgnoreCase)
        {
            ["custom-oauth"] = new OAuthCredentials
            {
                Refresh = "refresh-token",
                Access = "active-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            }
        };

        var result = await OAuthHelpers.GetOAuthApiKeyAsync(registry, "custom-oauth", credentials);

        Assert.NotNull(result);
        Assert.Equal("active-access-token", result.NewCredentials.Access);
        Assert.Equal("api:active-access-token", result.ApiKey);
        Assert.Equal(0, provider.RefreshCalls);
    }

    [Fact]
    public async Task RefreshOAuthTokenAsync_DelegatesToProvider()
    {
        var provider = new StubOAuthProvider("custom-oauth", "Custom OAuth")
        {
            RefreshedCredentials = new OAuthCredentials
            {
                Refresh = "refresh-token",
                Access = "refreshed-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
            }
        };
        var registry = new OAuthProviderRegistry([provider]);
        var credentials = new OAuthCredentials
        {
            Refresh = "refresh-token",
            Access = "expired-access-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        var refreshed = await OAuthHelpers.RefreshOAuthTokenAsync(registry, "custom-oauth", credentials);

        Assert.Equal("refreshed-access-token", refreshed.Access);
        Assert.Equal(1, provider.RefreshCalls);
    }

    private sealed class StubOAuthProvider(string id, string name) : IOAuthProvider
    {
        public string Id => id;
        public string Name => name;
        public bool UsesCallbackServer { get; init; }
        public OAuthCredentials? RefreshedCredentials { get; init; }
        public int RefreshCalls { get; private set; }

        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(RefreshedCredentials ?? credentials);
        }

        public string GetApiKey(OAuthCredentials credentials) => $"api:{credentials.Access}";
    }
}
