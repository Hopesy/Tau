// 作者：xxx
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【设备码精度】真实 Node 通知经新旧扩展及宿主双向桥接保留数值，旧回调不溢出。</summary>
    /// <param name="native">是否原生提供方。</param><param name="size">秒数类型。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, "fraction")]
    [InlineData(true, "fraction")]
    [InlineData(false, "large")]
    [InlineData(true, "large")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    public async Task NodeDeviceCode_PreservesFractionalAndLargeSeconds(bool native, string size)
    {
        using var fixture = new Fixture("device-code-precision", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions"); Directory.CreateDirectory(directory);
        var fields = size switch
        {
            "fraction" => "intervalSeconds:0.125,expiresInSeconds:0.25",
            "large" => "intervalSeconds:2147483647.5,expiresInSeconds:Number.MAX_SAFE_INTEGER",
            _ => ""
        };
        var definition = native
            ? "pi.registerProvider({id:'precise-device',name:'Device',getModels:()=>[],auth:{oauth:{name:'Device',login,refresh:async c=>c,toAuth:async c=>({apiKey:c.access})}}});"
            : "pi.registerProvider('precise-device',{api:'openai-completions',baseUrl:'https://unused.invalid',models:[],oauth:{name:'Device',login,refreshToken:async c=>c,getApiKey:c=>c.access}});";
        File.WriteAllText(Path.Combine(directory, "device.js"), "export default pi=>{const login=async callback=>{const notice={type:'device_code',userCode:'CODE',verificationUri:'https://device.invalid',"
            + fields + "};if(callback.notify)callback.notify(notice);else callback.onDeviceCode(notice);return{type:'oauth',access:'test-access',refresh:'',expires:Number.MAX_SAFE_INTEGER};};" + definition + "}");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        var provider = session.Runner.GetOAuthProvider("precise-device")!;
        var precise = new PreciseDeviceCallbacks();
        await provider.LoginAsync(precise);
        AssertDeviceCodeSeconds(precise.Notification, size);
        precise.Notification = null;
        await provider.LoginAsync(new CodingAgentInteractionOAuthCallbacks(new CodingAgentCallbackAuthInteraction(precise, default)));
        AssertDeviceCodeSeconds(precise.Notification, size);
        var legacy = new LegacyDeviceCallbacks();
        await provider.LoginAsync(legacy);
        Assert.True(legacy.Notified);
        Assert.Equal(size == "fraction" ? 1 : (int?)null, legacy.Interval);
        Assert.Equal(size == "fraction" ? 1 : (int?)null, legacy.Expires);
    }

    /// <summary>【CodingAgent】【设备码精度】比较通知中的原始秒数及标识。</summary>
    /// <param name="notification">捕获通知。</param><param name="size">数值类型。</param>
    private static void AssertDeviceCodeSeconds(OAuthDeviceCodeNotification? notification, string size)
    {
        Assert.NotNull(notification); Assert.Equal("CODE", notification.UserCode);
        Assert.Equal("https://device.invalid", notification.VerificationUri);
        Assert.Equal(size switch { "fraction" => 0.125, "large" => 2147483647.5, _ => (double?)null }, notification.IntervalSeconds);
        Assert.Equal(size switch { "fraction" => 0.25, "large" => 9007199254740991.0, _ => (double?)null }, notification.ExpiresInSeconds);
    }

    /// <summary>【CodingAgent】【设备码测试回调】拒绝无关交互。</summary>
    private abstract class DeviceCallbacksBase : IOAuthLoginCallbacks
    {
        /// <summary>禁止意外授权链接。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new NotSupportedException();
        /// <summary>禁止意外输入。</summary><param name="message">提示。</param><param name="placeholder">占位。</param><param name="allowEmpty">空值策略。</param><returns>未使用。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new NotSupportedException();
        /// <summary>禁止意外进度。</summary><param name="message">文本。</param>
        public void OnProgress(string message) => throw new NotSupportedException();
        /// <summary>不提供手动输入。</summary><returns>空值。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }

    /// <summary>【CodingAgent】【精确回调】捕获类型化通知。</summary>
    private sealed class PreciseDeviceCallbacks : DeviceCallbacksBase, IOAuthLoginCallbacks
    {
        public OAuthDeviceCodeNotification? Notification { get; set; }
        /// <summary>保存未舍入的通知。</summary><param name="notification">完整通知。</param>
        public void OnDeviceCode(OAuthDeviceCodeNotification notification) => Notification = notification;
    }

    /// <summary>【CodingAgent】【兼容回调】只实现原有整数秒接口。</summary>
    private sealed class LegacyDeviceCallbacks : DeviceCallbacksBase, IOAuthLoginCallbacks
    {
        public bool Notified { get; private set; }
        public int? Interval { get; private set; }
        public int? Expires { get; private set; }
        /// <summary>保存旧接口投影。</summary><param name="userCode">代码。</param><param name="verificationUri">地址。</param><param name="intervalSeconds">间隔。</param><param name="expiresInSeconds">期限。</param>
        public void OnDeviceCode(string userCode, string verificationUri, int? intervalSeconds = null, int? expiresInSeconds = null)
        { Notified = true; Interval = intervalSeconds; Expires = expiresInSeconds; }
    }
}
