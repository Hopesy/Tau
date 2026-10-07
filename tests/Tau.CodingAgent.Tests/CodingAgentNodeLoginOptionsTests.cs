// 作者：xxx
using System.Text.Json;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【Node 登录选项】新旧扩展均同步读取安装身份，未读取时不创建全局配置。</summary>
    /// <param name="native">是否原生认证定义。</param><param name="read">是否实际读取身份。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NodeLoginOptions_AreLazyAndSynchronous(bool native, bool read)
    {
        using var fixture = new Fixture("device-options", "models");
        WriteDeviceOptionsExtension(fixture, native, read ? "const id=options.getDeviceId(); if(typeof id!=='string'||id!==options.getDeviceId())throw Error('Not synchronous or stable'); return id;"
            : "if(typeof options?.getDeviceId!=='function')throw Error('Missing options'); return 'unused';");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        var path = Path.Combine(fixture.AgentDirectory, "coding-agent-settings.json");
        Assert.False(File.Exists(path));
        var router = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore, settingsStore: session.SettingsStore);
        var result = await router.TryHandleAsync("/login device-options");
        Assert.False(result!.IsError, result.Message);
        var saved = new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]).Load()["device-options"];
        if (read)
        {
            Assert.True(Guid.TryParseExact(saved.Access, "D", out _));
            Assert.Equal(saved.Access, session.SettingsStore.GetOrCreateDeviceId());
        }
        else
        {
            Assert.Equal("unused", saved.Access);
            Assert.False(File.Exists(path));
        }
    }

    /// <summary>【CodingAgent】【Node 登录选项】同一次登录多次读取只调用宿主一次，宿主异常可以传回扩展并再次读取。</summary>
    /// <param name="native">是否原生认证定义。</param><param name="fail">宿主是否抛出异常。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NodeLoginOptions_CacheHostResultAndError(bool native, bool fail)
    {
        using var fixture = new Fixture("device-options-result", "models");
        WriteDeviceOptionsExtension(fixture, native, fail
            ? "for(let i=0;i<2;i++){let caught;try{options.getDeviceId();}catch(e){caught=e.message;}if(caught!=='host identity failure')throw Error('Host error lost: '+caught);} return 'recovered';"
            : "const value=options.getDeviceId(); if(value!==options.getDeviceId())throw Error('Unstable identity'); return value;");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        var calls = 0;
        var result = await session.Runner.GetOAuthProvider("device-options")!.LoginAsync(new BuiltInKeyLoginCallbacks(),
            new OAuthLoginOptions(() => { Interlocked.Increment(ref calls); return fail ? throw new InvalidOperationException("host identity failure") : "synthetic-device"; }));
        Assert.Equal(fail ? "recovered" : "synthetic-device", result.Access);
        Assert.Equal(1, calls);
    }

    /// <summary>【CodingAgent】【Node 登录选项】旧入口未提供身份委托时，JavaScript 仍收到 undefined。</summary>
    /// <param name="native">是否原生认证定义。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NodeLoginOptions_LegacyOverloadLeavesOptionsUndefined(bool native)
    {
        using var fixture = new Fixture("device-options-none", "models");
        WriteDeviceOptionsExtension(fixture, native, "if(options!==undefined)throw Error('Unexpected options'); return 'no-options';");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        var result = await session.Runner.GetOAuthProvider("device-options")!.LoginAsync(new BuiltInKeyLoginCallbacks());
        Assert.Equal("no-options", result.Access);
    }

    /// <summary>【CodingAgent】【Node 登录选项】生成只包含本地测试凭据的原生或兼容扩展。</summary>
    /// <param name="fixture">隔离目录。</param><param name="native">是否原生定义。</param><param name="readBody">读取身份的 JavaScript 函数体。</param>
    private static void WriteDeviceOptionsExtension(Fixture fixture, bool native, string readBody)
    {
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        var definition = native
            ? "pi.registerProvider({id:'device-options',name:'Device',getModels:()=>[],auth:{oauth:{name:'Device Login',login,refresh,toAuth:async c=>({apiKey:c.access})}}});"
            : "pi.registerProvider('device-options',{api:'openai-completions',baseUrl:'https://unused.invalid',models:[],oauth:{name:'Device Login',login,refreshToken:refresh,getApiKey:c=>c.access}});";
        File.WriteAllText(Path.Combine(directory, "device.js"), "export default pi=>{const read=options=>{" + readBody
            + "}; const login=async(interaction,options)=>({type:'oauth',access:read(options),refresh:'',expires:Number.MAX_SAFE_INTEGER}); const refresh=async c=>c;" + definition + "}");
    }
}

/// <summary>【CodingAgent】【身份结果文件】验证惰性发布、取消及迟到任务的清理边界。</summary>
public sealed class CodingAgentOAuthDeviceReplyTests
{
    /// <summary>【CodingAgent】【身份结果文件】多次发布仅调用一次，关闭后删除结果文件。</summary>
    [Fact]
    public void Reply_IsLazyAndPublishesOnce()
    {
        var calls = 0;
        using var reply = new CodingAgentOAuthDeviceReply(() => { calls++; return "test-id"; });
        Assert.False(File.Exists(reply.ReplyPath)); Assert.Equal(0, calls);
        reply.Publish(default); reply.Publish(default);
        using var saved = JsonDocument.Parse(File.ReadAllText(reply.ReplyPath));
        Assert.Equal("test-id", saved.RootElement.GetProperty("value").GetString()); Assert.Equal(1, calls);
        reply.Dispose();
        Assert.False(File.Exists(reply.ReplyPath));
        reply.Publish(default); Assert.Equal(1, calls);
    }

    /// <summary>【CodingAgent】【身份结果文件】取消后不得调用宿主身份委托，但会发布可供 Node 读取的失败结果。</summary>
    [Fact]
    public void CancelledReply_DoesNotInvokeIdentity()
    {
        var calls = 0;
        using var reply = new CodingAgentOAuthDeviceReply(() => { calls++; return "test-id"; });
        reply.Publish(new CancellationToken(true));
        using var saved = JsonDocument.Parse(File.ReadAllText(reply.ReplyPath));
        Assert.False(saved.RootElement.GetProperty("ok").GetBoolean()); Assert.Equal(0, calls);
    }

    /// <summary>【CodingAgent】【身份结果文件】已结束的登录不能调用身份委托或重新创建结果文件。</summary>
    [Fact]
    public void DisposedReply_DoesNotInvokeIdentity()
    {
        var calls = 0;
        using var reply = new CodingAgentOAuthDeviceReply(() => { calls++; return "test-id"; });
        reply.Dispose(); reply.Publish(default);
        Assert.Equal(0, calls); Assert.False(File.Exists(reply.ReplyPath));
    }

    /// <summary>【CodingAgent】【身份结果文件】身份委托尚未结束也能释放，迟到结果不会复活临时文件。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task InFlightReply_CannotWriteAfterDisposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var reply = new CodingAgentOAuthDeviceReply(() => { entered.SetResult(); release.Wait(); return "late-id"; });
        var pending = Task.Run(() => reply.Publish(default));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reply.Dispose(); Assert.False(File.Exists(reply.ReplyPath));
        }
        finally { release.Set(); await pending; }
        Assert.False(File.Exists(reply.ReplyPath));
    }
}
