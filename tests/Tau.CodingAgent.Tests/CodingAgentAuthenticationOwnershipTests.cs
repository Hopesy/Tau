// 作者：xxx
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【空模型登录】原生 API key-only 提供方没有模型时仍可登录，不继承同名内置 OAuth。</summary>
    /// <param name="provider">新增或覆盖的提供方。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("anthropic")]
    [InlineData("pending-models")]
    public async Task AuthenticationOwnership_AllowsLoginBeforeModelsExist(string provider)
    {
        using var fixture = new Fixture("empty-model-auth", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "native.js"), $$$$"""
            export default pi=>pi.registerProvider({id:'{{{{provider}}}}',name:'Pending Models',getModels:()=>[],
              auth:{apiKey:{name:'Pending Key',login:async()=>({type:'api_key',key:'pending-key'}),
                resolve:async({credential})=>credential?.key?{auth:{apiKey:credential.key},source:'stored credential'}:undefined}}});
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        Assert.Empty(session.Runner.GetModels(provider));
        Assert.Contains(provider, session.Runner.GetAuthProviders());
        Assert.Null(session.Runner.GetOAuthProvider(provider));
        var router = new CodingAgentCommandRouter(session.Runner, extensionCommandStore: session.ExtensionCommandStore, authSelector: (state, _) =>
        {
            var option = Assert.Single(state.Options, option => option.Provider == provider);
            Assert.Equal("api_key", option.AuthType);
            Assert.Equal("Pending Models", option.Name);
            return Task.FromResult<string?>(option.SelectionKey);
        });
        var result = await router.TryHandleAsync("/login");
        Assert.False(result!.IsError, result.Message);
        Assert.Contains("authenticated successfully", result.Message);
        Assert.Equal("pending-key", new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]).LoadEntries()[provider].ApiKey);
        Assert.True(session.Runner.GetAuthStatus(provider).IsConfigured);
    }

    /// <summary>【CodingAgent】【认证恢复】旧式名称覆盖保留内置方式，原生替换后屏蔽 OAuth，注销再恢复内置方式。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task AuthenticationOwnership_RestoresBuiltInMethodsAfterUnregister()
    {
        using var fixture = new Fixture("auth-ownership", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ownership.js"), """
            export default pi=>{
              pi.registerProvider('anthropic',{name:'Legacy Anthropic'});
              pi.registerCommand('own-auth',{handler:()=>pi.registerProvider({id:'anthropic',name:'Native Anthropic',getModels:()=>[],
                auth:{apiKey:{name:'Native Key',resolve:async()=>undefined,login:async()=>({type:'api_key',key:'native-key'})}}})});
              pi.registerCommand('restore-auth',{handler:()=>pi.unregisterProvider('anthropic')});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.NotNull(session.Runner.GetOAuthProvider("anthropic"));
        Assert.NotNull(session.Runner.GetApiKeyProvider("anthropic"));
        await DrainAsync(session.RunAsync("/own-auth"));
        Assert.Null(session.Runner.GetOAuthProvider("anthropic"));
        Assert.Equal("Native Key", session.Runner.GetApiKeyProvider("anthropic")!.Name);
        await DrainAsync(session.RunAsync("/restore-auth"));
        Assert.NotNull(session.Runner.GetOAuthProvider("anthropic"));
        Assert.Equal("Anthropic API key", session.Runner.GetApiKeyProvider("anthropic")!.Name);
        Assert.Equal("Anthropic", session.Runner.GetProviderDisplayName("anthropic"));
    }
}
