// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

/// <summary>【AI】【流式认证归属测试】在最终提供方调用处检查密钥和请求头，避免配置合并绕过认证决定。</summary>
public sealed class StreamAuthenticationOwnershipTests
{
    /// <summary>【AI】【认证期间取消】普通和简化入口在认证前或认证中取消，都返回终止事件而不抛出同步异常。</summary>
    /// <param name="simple">是否简化入口。</param><param name="before">是否开始前取消。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelledPreparation_ReturnsAbortedStream(bool simple, bool before)
    {
        using var fixture = new Fixture("api-key-declined"); using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderAuthResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Resolver.SetRuntimeApiKeyProviders(new Dictionary<string, ApiKeyAuthDefinition>
            { ["auth-owner"] = new("Key", _ => { entered.TrySetResult(); return release.Task; }) });
        var provider = new CaptureProvider(); var registry = new ProviderRegistry();
        registry.Register("capture", () => provider, sourceId: "test");
        if (before) source.Cancel();
        var pending = Task.Run(() => simple
            ? StreamFunctions.StreamSimple(registry, fixture.Model, new(), new SimpleStreamOptions { Signal = source.Token }, fixture.Configuration, fixture.Resolver)
            : StreamFunctions.Stream(registry, fixture.Model, new(), new StreamOptions { Signal = source.Token }, fixture.Configuration, fixture.Resolver));
        try
        {
            if (!before) { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.Cancel(); }
            var stream = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await stream.ResultAsync;
            Assert.Equal(StopReason.Aborted, result.StopReason); Assert.Equal("Request was aborted", result.ErrorMessage);
            Assert.Null(provider.Options);
        }
        finally { release.TrySetResult(new("late")); }
    }

    /// <summary>【AI】【流式认证归属测试】普通和简化请求都遵守存储与扩展的认证归属。</summary>
    /// <param name="mode">认证情景。</param><param name="simple">是否简化流。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("oauth-only", false)]
    [InlineData("oauth-only", true)]
    [InlineData("api-key-declined", false)]
    [InlineData("api-key-declined", true)]
    [InlineData("legacy-key-declined", false)]
    [InlineData("legacy-key-declined", true)]
    [InlineData("stored-unsupported", false)]
    [InlineData("stored-unsupported", true)]
    [InlineData("stored-header", false)]
    [InlineData("stored-header", true)]
    [InlineData("native-header", false)]
    [InlineData("native-header", true)]
    [InlineData("no-owner", false)]
    [InlineData("no-owner", true)]
    [InlineData("explicit", false)]
    [InlineData("explicit", true)]
    public async Task FinalRequest_DoesNotRestoreRejectedConfigurationKey(string mode, bool simple)
    {
        using var fixture = new Fixture(mode);
        var provider = new CaptureProvider();
        var registry = new ProviderRegistry(); registry.Register("capture", () => provider, sourceId: "test");
        var context = new LlmContext { Messages = [new UserMessage("test")] };
        var key = mode == "explicit" ? "explicit-key" : null;
        var stream = simple ? StreamFunctions.StreamSimple(registry, fixture.Model, context, new SimpleStreamOptions { ApiKey = key }, fixture.Configuration, fixture.Resolver)
            : StreamFunctions.Stream(registry, fixture.Model, context, new StreamOptions { ApiKey = key }, fixture.Configuration, fixture.Resolver);
        await stream.ResultAsync;
        var options = Assert.IsAssignableFrom<StreamOptions>(provider.Options);
        Assert.Equal(mode switch { "no-owner" => "configured-key", "explicit" => "explicit-key", _ => null }, options.ApiKey);
        var header = options.Headers?.TryGetValue("Authorization", out var authorization) == true ? authorization : null;
        Assert.Equal(mode switch { "no-owner" => "Bearer configured-key", "explicit" => "Bearer explicit-key", "stored-header" => "Bearer stored-header", _ => null }, header);
        if (mode == "native-header") Assert.Equal("native-header", options.Headers!["X-Auth"]);
    }

    /// <summary>【AI】【认证预检测试】只有请求头的认证保持有效，拒绝认证时不误用配置密钥通过预检。</summary>
    /// <param name="mode">认证情景。</param><param name="allowed">是否能通过认证预检。</param>
    [Theory]
    [InlineData("native-header", true)]
    [InlineData("stored-header", true)]
    [InlineData("api-key-declined", false)]
    [InlineData("oauth-only", false)]
    [InlineData("stored-unsupported", false)]
    public void Preflight_AcceptsHeaderAuthenticationAndRejectsMissingAuth(string mode, bool allowed)
    {
        using var fixture = new Fixture(mode);
        if (allowed)
        {
            var auth = StreamFunctions.ResolveRequestAuthentication(fixture.Model, new(), fixture.Configuration, fixture.Resolver);
            Assert.Null(auth.ApiKey); Assert.NotEmpty(auth.Headers!);
        }
        else Assert.Throws<ProviderAuthException>(() => StreamFunctions.ResolveRequestAuthentication(fixture.Model, new(), fixture.Configuration, fixture.Resolver));
    }

    /// <summary>【AI】【认证测试配置】隔离模型配置和凭据，注册指定认证行为。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _modelsPath = Path.GetTempFileName();
        private readonly string _authPath = Path.GetTempFileName();
        public Model Model { get; } = new() { Provider = "auth-owner", Id = "chat", Name = "Chat", Api = "capture" };
        public ModelConfigurationStore Configuration { get; }
        public ProviderAuthResolver Resolver { get; }
        /// <summary>创建当前情景的独立配置。</summary><param name="mode">认证情景。</param>
        public Fixture(string mode)
        {
            File.WriteAllText(_modelsPath, """{"providers":{"auth-owner":{"apiKey":"configured-key","authHeader":true}}}""");
            File.WriteAllText(_authPath, "{}");
            Configuration = new([_modelsPath]);
            var store = new OAuthCredentialStore([_authPath]);
            var oauth = new HeaderOAuth();
            Resolver = new(new OAuthProviderRegistry(mode == "stored-header" ? [oauth] : []), store, configurationStore: Configuration);
            if (mode is "stored-unsupported" or "stored-header") store.Save("auth-owner", new() { Access = "stored", Refresh = "refresh", ExpiresAt = DateTimeOffset.MaxValue });
            if (mode is "oauth-only" or "explicit") Resolver.SetRuntimeAuthenticationProviders(new Dictionary<string, IOAuthProvider> { ["auth-owner"] = oauth }, new Dictionary<string, ApiKeyAuthDefinition>(), ["auth-owner"]);
            if (mode is "api-key-declined" or "legacy-key-declined" or "native-header")
            {
                var keys = new Dictionary<string, ApiKeyAuthDefinition> { ["auth-owner"] = new("Key", _ => Task.FromResult<ProviderAuthResult?>(mode == "native-header"
                    ? new(Headers: new Dictionary<string, string> { ["X-Auth"] = "native-header" }) : null)) };
                Resolver.SetRuntimeAuthenticationProviders(new Dictionary<string, IOAuthProvider>(), keys, mode == "legacy-key-declined" ? [] : ["auth-owner"]);
            }
        }
        /// <summary>删除本测试的两个临时文件。</summary>
        public void Dispose() { File.Delete(_modelsPath); File.Delete(_authPath); }
    }

    /// <summary>【AI】【认证捕获提供方】保存最终协议选项并立即完成合成响应。</summary>
    private sealed class CaptureProvider : IStreamProvider
    {
        public string Api => "capture";
        public StreamOptions? Options { get; private set; }
        /// <summary>捕获普通请求。</summary><param name="model">模型。</param><param name="context">上下文。</param><param name="options">协议选项。</param><returns>完成的事件流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Options = options; var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage { Content = [new TextContent("ok")] })); return stream;
        }
        /// <summary>捕获简化请求。</summary><param name="model">模型。</param><param name="context">上下文。</param><param name="options">简化选项。</param><returns>完成的事件流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }

    /// <summary>【AI】【认证头替身】使用只包含请求头的 OAuth 认证。</summary>
    private sealed class HeaderOAuth : IOAuthProvider
    {
        public string Id => "auth-owner";
        public string Name => "Headers";
        /// <summary>测试不执行登录。</summary><param name="callbacks">交互。</param><param name="cancellationToken">信号。</param><returns>未使用。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>测试不执行刷新。</summary><param name="credentials">凭据。</param><param name="cancellationToken">信号。</param><returns>未使用。</returns>
        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>只允许完整认证解析。</summary><param name="credentials">凭据。</param><returns>未使用。</returns>
        public string GetApiKey(OAuthCredentials credentials) => throw new NotSupportedException();
        /// <summary>返回合成授权头。</summary><param name="credentials">凭据。</param><param name="token">信号。</param><returns>不包含 API key 的认证。</returns>
        public ProviderAuthResult ResolveAuth(OAuthCredentials credentials, CancellationToken token = default) => new(Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer stored-header" });
    }
}
