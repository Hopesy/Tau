// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【登录配置测试】验证 Models SDK 新选项与旧委托的兼容。</summary>
public sealed class OAuthLoginOptionsTests
{
    /// <summary>【AI】【登录元数据测试】SDK 保留说明链接及选择项说明，不退化为普通文本。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Models_ForwardInfoAndSelectMetadata()
    {
        var provider = new OptionsProvider { Detailed = true }; var config = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(new OAuthProviderRegistry([provider]), new OAuthCredentialStore([]), configurationStore: config);
        var oauth = BuiltInProviders.CreateBuiltInModels(config, resolver).GetProvider("openai")!.Auth!.OAuth!;
        var interaction = new NoInteraction();
        await oauth.LoginWithOptionsAsync(interaction, new OAuthLoginOptions(() => "device"));
        Assert.Equal("info", interaction.Notification!.Type);
        Assert.Equal(new ProviderAuthInfoLink("https://guide.invalid", "Guide"), Assert.Single(interaction.Notification.Links!));
        Assert.Equal("Additional description", Assert.Single(interaction.Prompt!.Options!).Description);
    }

    /// <summary>【AI】【设备码精度测试】SDK 适配器保留小数、大数及缺省秒数。</summary>
    /// <param name="interval">轮询间隔。</param><param name="expires">有效期限。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(0.125, 0.25)]
    [InlineData(2147483647.5, 9007199254740991.0)]
    [InlineData(null, null)]
    public async Task Models_ForwardPreciseDeviceCode(double? interval, double? expires)
    {
        var provider = new OptionsProvider { DeviceCode = new("CODE", "https://device.invalid", interval, expires) };
        var config = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(new OAuthProviderRegistry([provider]), new OAuthCredentialStore([]), configurationStore: config);
        var oauth = BuiltInProviders.CreateBuiltInModels(config, resolver).GetProvider("openai")!.Auth!.OAuth!;
        var interaction = new NoInteraction();
        await oauth.LoginWithOptionsAsync(interaction, new OAuthLoginOptions(() => "device"));
        Assert.Equal("CODE", interaction.Notification!.UserCode);
        Assert.Equal(interval, interaction.Notification.IntervalSeconds); Assert.Equal(expires, interaction.Notification.ExpiresInSeconds);
    }

    /// <summary>【AI】【登录配置测试】SDK 向内置适配层传递原始惰性设备标识委托。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Models_ForwardDeviceIdentityOptions()
    {
        var provider = new OptionsProvider(); var config = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(new OAuthProviderRegistry([provider]), new OAuthCredentialStore([]), configurationStore: config);
        var oauth = BuiltInProviders.CreateBuiltInModels(config, resolver).GetProvider("openai")!.Auth!.OAuth!;
        var calls = 0;
        var result = await oauth.LoginWithOptionsAsync(new NoInteraction(), new OAuthLoginOptions(() => { calls++; return "device-id"; }));
        Assert.Equal("device-id", result.Access);
        Assert.Equal(1, calls);
    }

    /// <summary>【AI】【登录配置测试】旧 SDK 委托无需实现新重载，也不会调用设备回调。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ExistingDefinition_IgnoresUnusedOptions()
    {
        var credential = new ProviderOAuthCredential("refresh", "access", DateTimeOffset.MaxValue);
        var definition = new OAuthAuthDefinition("Legacy", _ => Task.FromResult(credential), (value, _) => Task.FromResult(value), _ => Task.FromResult(new ProviderAuthResult("access")));
        Assert.Same(credential, await definition.LoginWithOptionsAsync(new NoInteraction(), new OAuthLoginOptions(() => throw new InvalidOperationException("Must remain lazy"))));
    }

    /// <summary>【AI】【登录配置测试】要求带选项的登录重载。</summary>
    private sealed class OptionsProvider : IOAuthProvider
    {
        public string Id => "openai";
        public string Name => "Options";
        public OAuthDeviceCodeNotification? DeviceCode { get; init; }
        public bool Detailed { get; init; }
        /// <summary>禁止忽略选项。</summary><param name="callbacks">交互。</param><param name="cancellationToken">取消。</param><returns>未使用。</returns>
        public Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>把设备值作为测试凭据回传。</summary><param name="callbacks">交互。</param><param name="options">选项。</param><param name="cancellationToken">取消。</param><returns>测试凭据。</returns>
        public async Task<OAuthCredentials> LoginAsync(IOAuthLoginCallbacks callbacks, OAuthLoginOptions? options, CancellationToken cancellationToken = default)
        {
            if (DeviceCode is { } notification) callbacks.OnDeviceCode(notification);
            if (Detailed)
            {
                callbacks.OnInfo("Setup", [new("https://guide.invalid", "Guide")]);
                Assert.Equal("choice", await callbacks.OnSelectAsync("Choose", [new("choice", "Choice", "Additional description")]));
            }
            return new OAuthCredentials { Access = options!.GetDeviceId!(), Refresh = "refresh", ExpiresAt = DateTimeOffset.MaxValue };
        }
        /// <summary>返回原始凭据。</summary><param name="credentials">凭据。</param><param name="cancellationToken">取消。</param><returns>凭据。</returns>
        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default) => Task.FromResult(credentials);
        /// <summary>读取访问令牌。</summary><param name="credentials">凭据。</param><returns>访问令牌。</returns>
        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }
    /// <summary>【AI】【登录配置测试】无需交互的测试宿主。</summary>
    private sealed class NoInteraction : AuthInteraction
    {
        public CancellationToken CancellationToken => default;
        public ProviderAuthNotification? Notification { get; private set; }
        public ProviderAuthPrompt? Prompt { get; private set; }
        /// <summary>保存结构化选择提示。</summary><param name="prompt">提示。</param><returns>合成选择。</returns>
        public Task<string> PromptAsync(ProviderAuthPrompt prompt) { Prompt = prompt; return Task.FromResult("choice"); }
        /// <summary>保存设备码通知以检查数值精度。</summary><param name="notification">类型化通知。</param>
        public void Notify(ProviderAuthNotification notification) => Notification = notification;
        /// <summary>禁止意外输入。</summary><param name="prompt">提示。</param><returns>未使用。</returns>
        public Task<string> PromptAsync(string prompt) => throw new NotSupportedException();
        /// <summary>禁止意外通知。</summary><param name="message">消息。</param>
        public void Notify(string message) => throw new NotSupportedException();
    }
}
