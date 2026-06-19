using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

// Direct unit coverage for the Tau-native port of upstream `packages/ai/src/api-registry.ts`.
// Upstream backs the registry with a `Map<string, RegisteredApiProvider>` and exposes register /
// get / getAll / unregisterBySource / clear plus a `sourceId` for bulk unregistration. Tau's
// `ProviderRegistry` is a .NET-native equivalent that additionally supports lazy factory
// initialization (provider is only constructed on first resolution). These tests pin that contract
// directly; provider wiring is otherwise only exercised incidentally via BuiltInProvidersTests.
public sealed class ProviderRegistryTests
{
    [Fact]
    public void Get_ReturnsRegisteredProvider()
    {
        var registry = new ProviderRegistry();
        var provider = new FakeStreamProvider("openai-chat-completions");

        registry.Register("openai-chat-completions", provider);

        Assert.Same(provider, registry.Get("openai-chat-completions"));
    }

    [Fact]
    public void Get_ThrowsWhenApiNotRegistered()
    {
        var registry = new ProviderRegistry();

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Get("missing"));
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGet_ReturnsNullWhenApiNotRegistered()
    {
        var registry = new ProviderRegistry();

        Assert.Null(registry.TryGet("missing"));
    }

    [Fact]
    public void TryGet_ReturnsRegisteredProvider()
    {
        var registry = new ProviderRegistry();
        var provider = new FakeStreamProvider("anthropic-messages");

        registry.Register("anthropic-messages", provider);

        Assert.Same(provider, registry.TryGet("anthropic-messages"));
    }

    [Theory]
    [InlineData("openai-chat-completions", "openai-completions")]
    [InlineData("openai-chat-completions", "openai-compatible")]
    [InlineData("google-generative-language", "google-generative-ai")]
    public void GetAndTryGet_ResolveUpstreamApiAliases(string registeredApi, string aliasApi)
    {
        var registry = new ProviderRegistry();
        var provider = new FakeStreamProvider(registeredApi);

        registry.Register(registeredApi, provider);

        Assert.Same(provider, registry.Get(aliasApi));
        Assert.Same(provider, registry.TryGet(aliasApi));
        Assert.Contains(registeredApi, registry.RegisteredApis);
        Assert.DoesNotContain(aliasApi, registry.RegisteredApis);
    }

    [Theory]
    [InlineData("openai-completions", "openai-chat-completions")]
    [InlineData("openai-compatible", "openai-chat-completions")]
    [InlineData("google-generative-ai", "google-generative-language")]
    public void Register_NormalizesUpstreamApiAliases(string aliasApi, string canonicalApi)
    {
        var registry = new ProviderRegistry();
        var provider = new FakeStreamProvider(aliasApi);

        registry.Register(aliasApi, provider);

        Assert.Same(provider, registry.Get(canonicalApi));
        Assert.Same(provider, registry.Get(aliasApi));
        Assert.Contains(canonicalApi, registry.RegisteredApis);
        Assert.DoesNotContain(aliasApi, registry.RegisteredApis);
    }

    [Fact]
    public void Register_Factory_DefersConstructionUntilFirstResolution()
    {
        var registry = new ProviderRegistry();
        var constructed = 0;

        registry.Register("lazy", () =>
        {
            constructed++;
            return new FakeStreamProvider("lazy");
        });

        // Registering and listing must not trigger the factory.
        Assert.Equal(0, constructed);
        Assert.Contains("lazy", registry.RegisteredApis);

        var first = registry.Get("lazy");
        var second = registry.Get("lazy");

        // Factory runs exactly once and the same instance is cached (Lazy semantics).
        Assert.Equal(1, constructed);
        Assert.Same(first, second);
    }

    [Fact]
    public void Register_ReplacesExistingProviderForSameApi()
    {
        var registry = new ProviderRegistry();
        var original = new FakeStreamProvider("openai-responses");
        var replacement = new FakeStreamProvider("openai-responses");

        registry.Register("openai-responses", original);
        registry.Register("openai-responses", replacement);

        Assert.Same(replacement, registry.Get("openai-responses"));
        Assert.Single(registry.RegisteredApis, "openai-responses");
    }

    [Fact]
    public void RegisteredApis_ListsAllRegisteredApis()
    {
        var registry = new ProviderRegistry();
        registry.Register("a", new FakeStreamProvider("a"));
        registry.Register("b", new FakeStreamProvider("b"));
        registry.Register("c", new FakeStreamProvider("c"));

        Assert.Equal(["a", "b", "c"], registry.RegisteredApis.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Unregister_RemovesOnlyTheNamedApi()
    {
        var registry = new ProviderRegistry();
        registry.Register("keep", new FakeStreamProvider("keep"));
        registry.Register("drop", new FakeStreamProvider("drop"));

        registry.Unregister("drop");

        Assert.Null(registry.TryGet("drop"));
        Assert.NotNull(registry.TryGet("keep"));
    }

    [Fact]
    public void Unregister_NormalizesUpstreamApiAlias()
    {
        var registry = new ProviderRegistry();
        registry.Register("google-generative-language", new FakeStreamProvider("google-generative-language"));

        registry.Unregister("google-generative-ai");

        Assert.Null(registry.TryGet("google-generative-language"));
    }

    [Fact]
    public void Unregister_IsNoOpForUnknownApi()
    {
        var registry = new ProviderRegistry();
        registry.Register("keep", new FakeStreamProvider("keep"));

        registry.Unregister("never-registered");

        Assert.NotNull(registry.TryGet("keep"));
    }

    [Fact]
    public void UnregisterBySource_RemovesEveryApiFromThatSourceOnly()
    {
        var registry = new ProviderRegistry();
        registry.Register("pkg-a-1", new FakeStreamProvider("pkg-a-1"), sourceId: "package-a");
        registry.Register("pkg-a-2", new FakeStreamProvider("pkg-a-2"), sourceId: "package-a");
        registry.Register("pkg-b-1", new FakeStreamProvider("pkg-b-1"), sourceId: "package-b");
        registry.Register("builtin", new FakeStreamProvider("builtin"));

        registry.UnregisterBySource("package-a");

        Assert.Null(registry.TryGet("pkg-a-1"));
        Assert.Null(registry.TryGet("pkg-a-2"));
        Assert.NotNull(registry.TryGet("pkg-b-1"));
        Assert.NotNull(registry.TryGet("builtin"));
    }

    [Fact]
    public void UnregisterBySource_DoesNotRemoveProvidersWithNullSource()
    {
        var registry = new ProviderRegistry();
        registry.Register("sourced", new FakeStreamProvider("sourced"), sourceId: "package-a");
        registry.Register("unsourced", new FakeStreamProvider("unsourced"));

        // A null sourceId must never match an explicit source-based unregistration.
        registry.UnregisterBySource("package-a");

        Assert.Null(registry.TryGet("sourced"));
        Assert.NotNull(registry.TryGet("unsourced"));
    }

    [Fact]
    public void UnregisterBySource_IsNoOpForUnknownSource()
    {
        var registry = new ProviderRegistry();
        registry.Register("a", new FakeStreamProvider("a"), sourceId: "package-a");

        registry.UnregisterBySource("package-z");

        Assert.NotNull(registry.TryGet("a"));
    }

    [Fact]
    public void Clear_RemovesAllProviders()
    {
        var registry = new ProviderRegistry();
        registry.Register("a", new FakeStreamProvider("a"));
        registry.Register("b", new FakeStreamProvider("b"), sourceId: "package-a");

        registry.Clear();

        Assert.Empty(registry.RegisteredApis);
        Assert.Null(registry.TryGet("a"));
        Assert.Null(registry.TryGet("b"));
    }

    private sealed class FakeStreamProvider(string api) : IStreamProvider
    {
        public string Api { get; } = api;

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            throw new NotSupportedException("Stream is not exercised by registry tests.");

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
            throw new NotSupportedException("StreamSimple is not exercised by registry tests.");
    }
}
