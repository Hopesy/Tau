// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

/// <summary>【AI】【缓存默认值】验证显式禁用、配置和环境优先级，以及发送前观察到的最终参数。</summary>
public sealed class RequestCacheDefaultsTests
{
    /// <summary>【AI】【缓存保留】普通和简化请求都采用上游默认短缓存，并保留显式 none。</summary>
    /// <param name="simple">是否使用简化入口。</param><param name="environment">缓存环境值。</param>
    /// <param name="disabled">是否显式禁用。</param><returns>异步回归任务。</returns>
    [Theory]
    [InlineData(false, "short", false)] [InlineData(true, "short", false)]
    [InlineData(false, "long", false)] [InlineData(true, "long", false)]
    [InlineData(false, "long", true)] [InlineData(true, "long", true)]
    public async Task Request_ResolvesRetentionBeforeDispatch(bool simple, string environment, bool disabled)
    {
        var expected = disabled ? CacheRetention.None : environment == "long" ? CacheRetention.Long : CacheRetention.Short;
        var provider = new CaptureProvider(options => Assert.Equal(expected, options.CacheRetention));
        var registry = new ProviderRegistry(); registry.Register("cache-test", provider);
        var store = new ModelConfigurationStore([]);
        var auth = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: store);
        var options = new SimpleStreamOptions { ApiKey = "fixture", Env = new Dictionary<string, string> { ["PI_CACHE_RETENTION"] = environment } };
        if (disabled) options = options with { CacheRetention = CacheRetention.None };
        var stream = simple ? StreamFunctions.StreamSimple(registry, Model(), new() { Messages = [] }, options, store, auth)
            : StreamFunctions.Stream(registry, Model(), new() { Messages = [] }, options, store, auth);
        await stream.ResultAsync;
        Assert.True(provider.Called);
    }

    /// <summary>【AI】【请求准备观察】配置合并、缓存和上下文转换先完成，再观察参数，最后才调用提供方。</summary>
    /// <returns>异步回归任务。</returns>
    [Fact]
    public async Task PreparedObserver_SeesMergedOptionsBeforeProviderStarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-cache-options-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"providers":{"cache-fixture":{"api":"cache-test","apiKey":"fixture","options":{"reasoning":"high","cacheRetention":"none","maxTokens":47}}}}""");
        try
        {
            var order = new List<string>(); SimpleStreamOptions? prepared = null;
            var provider = new CaptureProvider(options => { Assert.Same(prepared, options); order.Add("send"); });
            var registry = new ProviderRegistry(); registry.Register("cache-test", provider);
            var store = new ModelConfigurationStore([path]);
            var auth = new ProviderAuthResolver(credentialStore: new OAuthCredentialStore([]), configurationStore: store);
            var stream = StreamFunctions.StreamSimple(registry, Model(), new LlmContext { Messages = [new SystemMessage("system"), new UserMessage("user")] },
                new() { Env = new Dictionary<string, string> { ["PI_CACHE_RETENTION"] = "long" } }, store, auth,
                (model, context, options, actual) =>
                {
                    order.Add("prepared"); prepared = options;
                    Assert.Equal("fixture", options.ApiKey); Assert.Equal(47, options.MaxTokens);
                    Assert.Equal(ThinkingLevel.High, options.Reasoning); Assert.Equal(CacheRetention.None, options.CacheRetention);
                    Assert.Same(options, actual); Assert.Equal("system", context.SystemPrompt);
                    Assert.DoesNotContain(context.Messages, message => message is SystemMessage);
                });
            await stream.ResultAsync;
            Assert.Equal(["prepared", "send"], order);
        }
        finally { File.Delete(path); }
    }

    /// <summary>【AI】【测试模型】创建无网络协议模型。</summary><returns>测试模型。</returns>
    private static Model Model() => new() { Id = "test", Name = "test", Api = "cache-test", Provider = "cache-fixture" };

    /// <summary>【AI】【测试提供方】观察参数并直接完成响应。</summary>
    private sealed class CaptureProvider(Action<StreamOptions> capture) : IStreamProvider
    {
        public string Api => "cache-test";
        public bool Called { get; private set; }
        /// <summary>【AI】【普通测试请求】记录最终参数。</summary><param name="model">模型。</param>
        /// <param name="context">上下文。</param><param name="options">选项。</param><returns>完成流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            capture(options); Called = true;
            var stream = new AssistantMessageStream(); stream.Push(new DoneEvent(new AssistantMessage([]))); return stream;
        }
        /// <summary>【AI】【简化测试请求】复用参数检查。</summary><param name="model">模型。</param>
        /// <param name="context">上下文。</param><param name="options">选项。</param><returns>完成流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }
}
